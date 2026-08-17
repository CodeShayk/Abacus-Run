using System.Text.Json;
using Abacus.Run.Abstractions;
using Abacus.Run.Abstractions.Middleware;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Abacus.Run.Core;

public sealed record RunOutcome(InstanceStatus Status, object? Result, string? TerminalReason, Exception? Exception)
{
    public static RunOutcome Completed(object? result) => new(InstanceStatus.Completed, result, null, null);
}

public sealed class WorkflowRunnerDependencies
{
    public required IWorkflowRegistry Registry { get; init; }
    public required IInstanceStore Instances { get; init; }
    public required IEventSink Events { get; init; }
    public required EventSequencer Sequencer { get; init; }
    public required MiddlewarePipelineFactory Pipelines { get; init; }
    public required ICheckpointStore<JsonElement> Checkpoints { get; init; }
    public IApprovalStore? Approvals { get; init; }
    public IApprovalService? ApprovalService { get; init; }
    public IGatePolicyStore? GatePolicies { get; init; }
    public IAuditStore? Audit { get; init; }

    /// <summary>
    /// Backing store for workflow audit records. Only consulted for definitions that implement
    /// <see cref="IAuditedWorkflowDefinition"/>.
    /// </summary>
    public IAuditRecordStore? AuditRecords { get; init; }

    /// <summary>Present when the host runs the event broker; used to drop waits on termination.</summary>
    public IEventSubscriptionStore? EventSubscriptions { get; init; }
    public ILogStore? Logs { get; init; }
    public IServiceProvider? Services { get; init; }
    public WorkflowHostOptions Options { get; init; } = new();
    public TimeProvider Clock { get; init; } = TimeProvider.System;
    public ILogger Logger { get; init; } = NullLogger.Instance;
}

/// <summary>
/// Owns one instance's execution on one replica: builds the graph, drives the engine, translates
/// events, and applies the workflow's failure disposition.
/// </summary>
public sealed class WorkflowRunner
{
    private readonly WorkflowRunnerDependencies _deps;
    private int _currentSuperstep;
    private string? _latestCheckpointId;
    private readonly Queue<string> _hostInvocationEvents = new();

    /// <summary>
    /// Resolved once per run rather than per event, so the hot path is a dictionary lookup. Defaults
    /// to emitting everything when the definition declares no policy.
    /// </summary>
    private NotificationPolicy _notifications = NotificationPolicy.Default;

    public WorkflowRunner(WorkflowRunnerDependencies dependencies)
        => _deps = dependencies ?? throw new ArgumentNullException(nameof(dependencies));

    public async Task<RunOutcome> RunAsync(WorkflowInstance instance, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instance);

        WorkflowDescriptor? descriptor = _deps.Registry.Resolve(instance.WorkflowName, instance.WorkflowVersion);
        if (descriptor is null)
        {
            await FinalizeAsync(instance, InstanceStatus.DeadStopped, null, "WorkflowVersionUnavailable", cancellationToken)
                .ConfigureAwait(false);
            return new RunOutcome(InstanceStatus.DeadStopped, null, "WorkflowVersionUnavailable", null);
        }

        _deps.Sequencer.Seed(instance.InstanceId, await MaxSequenceAsync(instance.InstanceId, cancellationToken).ConfigureAwait(false));

        var invocation = new WorkflowInvocationContext
        {
            InstanceId = instance.InstanceId,
            TenantId = instance.TenantId,
            WorkflowName = instance.WorkflowName,
            WorkflowVersion = instance.WorkflowVersion,
            Attempt = instance.AttemptCount + 1,
            Context = instance.ContextJson,
            Services = _deps.Services
        };

        WorkflowDelegate pipeline = _deps.Pipelines.BuildWorkflowPipeline(
            (ctx, ct) => ExecuteRunAsync(instance, descriptor, ctx, ct));

        try
        {
            await pipeline(invocation, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            invocation.Exception = ex;
        }

        if (invocation.Items.TryGetValue("outcome", out object? outcome) && outcome is RunOutcome runOutcome)
        {
            return runOutcome;
        }

        if (invocation.Exception is { } failure)
        {
            return await ApplyFailureAsync(instance, descriptor, "workflow", failure, cancellationToken).ConfigureAwait(false);
        }

        return RunOutcome.Completed(invocation.Result);
    }

    private async ValueTask ExecuteRunAsync(
        WorkflowInstance instance,
        WorkflowDescriptor descriptor,
        WorkflowInvocationContext invocation,
        CancellationToken cancellationToken)
    {
        var gates = new Dictionary<string, ApprovalGate>(StringComparer.Ordinal);

        _notifications = descriptor.Definition is INotifyingWorkflow notifying
            ? notifying.Notifications
            : NotificationPolicy.Default;

        // A definition that declares an audit record gets a recorder bound to its declared shape;
        // one that doesn't gets null, and the hook costs it nothing.
        IWorkflowAuditRecorder? auditRecorder = CreateAuditRecorder(instance, descriptor);

        var buildContext = new WorkflowBuildContext(
            instance.InstanceId, instance.TenantId, instance.WorkflowName, instance.WorkflowVersion,
            invocation.Attempt, _deps.Services,
            (executor, gate) => Attach(executor, gate, instance, invocation, gates, auditRecorder),
            auditRecorder);

        Workflow workflow = await descriptor.Definition.BuildAsync(buildContext, cancellationToken).ConfigureAwait(false);

        CheckpointManager checkpointManager = CheckpointManager.CreateJson(_deps.Checkpoints);

        await PublishAsync(instance, WorkflowEventTypes.WorkflowStarted, new
        {
            workflow = instance.WorkflowName,
            version = instance.WorkflowVersion,
            attempt = invocation.Attempt,
            resumed = instance.LatestCheckpointId is not null
        }, null, cancellationToken).ConfigureAwait(false);

        StreamingRun run = instance.LatestCheckpointId is { Length: > 0 } checkpointId
            ? await InProcessExecution.ResumeStreamingAsync(
                workflow, new CheckpointInfo(instance.InstanceId, checkpointId), checkpointManager, cancellationToken)
                .ConfigureAwait(false)
            : await StartTypedAsync(
                workflow, descriptor, DeserializeContext(descriptor, instance.ContextJson),
                checkpointManager, instance.InstanceId, cancellationToken).ConfigureAwait(false);

        RunOutcome outcome = await PumpAsync(run, instance, descriptor, invocation, cancellationToken).ConfigureAwait(false);
        invocation.Items["outcome"] = outcome;
        invocation.Result = outcome.Result;
    }

    private async Task<RunOutcome> PumpAsync(
        StreamingRun run,
        WorkflowInstance instance,
        WorkflowDescriptor descriptor,
        WorkflowInvocationContext invocation,
        CancellationToken cancellationToken)
    {
        object? output = null;

        await foreach (WorkflowEvent evt in run.WatchStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            switch (evt)
            {
                case SuperStepCompletedEvent step:
                    _currentSuperstep = step.StepNumber;
                    await PublishAsync(instance, WorkflowEventTypes.SuperstepCompleted,
                        new { superstep = step.StepNumber }, null, cancellationToken).ConfigureAwait(false);
                    break;

                case ExecutorInvokedEvent invoked:
                    if (_hostInvocationEvents.TryPeek(out string? hostExecutorId) &&
                        string.Equals(hostExecutorId, invoked.ExecutorId, StringComparison.Ordinal))
                    {
                        _hostInvocationEvents.Dequeue();
                    }
                    else
                    {
                        await PublishAsync(instance, WorkflowEventTypes.ExecutorInvoked,
                            new { executorId = invoked.ExecutorId, superstep = _currentSuperstep },
                            invoked.ExecutorId, cancellationToken).ConfigureAwait(false);
                    }
                    break;

                case ExecutorCompletedEvent completed:
                    await PublishAsync(instance, WorkflowEventTypes.ExecutorCompleted,
                        new { executorId = completed.ExecutorId, superstep = _currentSuperstep },
                        completed.ExecutorId, cancellationToken).ConfigureAwait(false);
                    break;

                case ExecutorFailedEvent failed:
                {
                    Exception error = failed.Data ?? new InvalidOperationException("Executor failed without an exception.");
                    await PublishAsync(instance, WorkflowEventTypes.ExecutorFailed, new
                    {
                        executorId = failed.ExecutorId,
                        superstep = _currentSuperstep,
                        exceptionType = error.GetType().Name,
                        message = error.Message
                    }, failed.ExecutorId, cancellationToken).ConfigureAwait(false);

                    await LogAsync(instance, "Error", failed.ExecutorId, error, cancellationToken).ConfigureAwait(false);
                    return await ApplyFailureAsync(instance, descriptor, failed.ExecutorId ?? "unknown", error, cancellationToken)
                        .ConfigureAwait(false);
                }

                case RequestInfoEvent request:
                    await PublishAsync(instance, WorkflowEventTypes.RequestPending, new
                    {
                        requestId = request.Request.RequestId,
                        portId = request.Request.PortInfo.PortId
                    }, null, cancellationToken).ConfigureAwait(false);

                    await TransitionAsync(instance, InstanceStatus.AwaitingInput, null, cancellationToken).ConfigureAwait(false);
                    return new RunOutcome(InstanceStatus.AwaitingInput, null, null, null);

                case WorkflowOutputEvent outputEvent:
                    output = outputEvent.Data;
                    await PublishAsync(instance, WorkflowEventTypes.WorkflowOutput,
                        new { executorId = outputEvent.ExecutorId, result = Describe(outputEvent.Data) },
                        outputEvent.ExecutorId, cancellationToken).ConfigureAwait(false);
                    break;

                case WorkflowErrorEvent errorEvent:
                {
                    Exception error = errorEvent.Data as Exception
                        ?? new InvalidOperationException(errorEvent.Data?.ToString() ?? "Workflow error.");
                    return await ApplyFailureAsync(instance, descriptor, "workflow", error, cancellationToken)
                        .ConfigureAwait(false);
                }

                case LlmDeltaWorkflowEvent delta:
                    await PublishTransientAsync(instance, WorkflowEventTypes.LlmDelta,
                        new { executorId = delta.ExecutorId, delta = delta.Delta },
                        delta.ExecutorId, cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    // The class of bug rather than one instance of it: an unrecognised engine event
                    // used to vanish in silence, which is how llm.delta stayed broken while being
                    // written down in three places.
                    _deps.Logger.LogDebug(
                        "Unhandled workflow event {EventType} on instance {InstanceId}.",
                        evt.GetType().Name, instance.InstanceId);
                    break;
            }
        }

        // Stream ended. An approval raised during this run parks the instance rather than completing it.
        if (_deps.Approvals is not null)
        {
            IReadOnlyList<ApprovalRequest> pending =
                await _deps.Approvals.ListForInstanceAsync(instance.InstanceId, cancellationToken).ConfigureAwait(false);

            if (pending.Any(a => a.State == ApprovalState.Pending))
            {
                await TransitionAsync(instance, InstanceStatus.AwaitingApproval, null, cancellationToken).ConfigureAwait(false);
                return new RunOutcome(InstanceStatus.AwaitingApproval, null, null, null);
            }
        }

        // Same reasoning for an event wait: the executor halted and emitted nothing, so the stream
        // ended with the graph unfinished. Without this the instance would look Completed while a
        // downstream node had never run.
        if (_deps.EventSubscriptions is { } subscriptions)
        {
            IReadOnlyList<EventSubscription> waits = await subscriptions.QueryAsync(new SubscriptionQuery
            {
                InstanceId = instance.InstanceId,
                Kind = SubscriptionKind.Wait,
                PendingOnly = true
            }, cancellationToken).ConfigureAwait(false);

            if (waits.Count > 0)
            {
                await TransitionAsync(instance, InstanceStatus.AwaitingInput, null, cancellationToken).ConfigureAwait(false);
                return new RunOutcome(InstanceStatus.AwaitingInput, null, null, null);
            }
        }

        await FinalizeAsync(instance, InstanceStatus.Completed, output, null, cancellationToken).ConfigureAwait(false);
        return RunOutcome.Completed(output);
    }

    /// <summary>
    /// Builds the per-instance audit recorder when the definition declares a record shape. Returns
    /// null otherwise — auditing is opt-in per workflow, not a tax on every one.
    /// </summary>
    private IWorkflowAuditRecorder? CreateAuditRecorder(WorkflowInstance instance, WorkflowDescriptor descriptor)
    {
        if (descriptor.Definition is not IAuditedWorkflowDefinition audited) return null;

        if (_deps.AuditRecords is not { } store)
        {
            _deps.Logger.LogWarning(
                "Workflow '{Workflow}' declares an audit record but no IAuditRecordStore is registered; auditing is off.",
                instance.WorkflowName);
            return null;
        }

        return new WorkflowAuditRecorder(
            audited.AuditRecord,
            store,
            instance.InstanceId,
            instance.WorkflowName,
            instance.WorkflowVersion,
            _deps.Clock,
            _deps.Logger);
    }

    private ExecutorBinding Attach(
        IHostExecutor executor,
        ApprovalGate gate,
        WorkflowInstance instance,
        WorkflowInvocationContext invocation,
        Dictionary<string, ApprovalGate> gates,
        IWorkflowAuditRecorder? audit)
    {
        gates[executor.Id] = gate;

        var descriptor = new ExecutorDescriptor(
            executor.Id, executor.GetType(), instance.WorkflowName, instance.WorkflowVersion, gate.Mode)
        {
            Metadata = new Dictionary<string, object?>(executor.Metadata)
            {
                ["attempt"] = invocation.Attempt,
                ["instanceId"] = instance.InstanceId
            }
        };

        ExecutorDelegate terminal = ResolveTerminal(executor);
        ExecutorDelegate pipeline = _deps.Pipelines.BuildExecutorPipeline(descriptor, terminal);

        executor.Runtime = new HostExecutorRuntime
        {
            InstanceId = instance.InstanceId,
            TenantId = instance.TenantId,
            Descriptor = descriptor,
            Attempt = invocation.Attempt,
            Pipeline = pipeline,
            Gates = new GateEvaluator(
                instance.WorkflowName, instance.WorkflowVersion, instance.TenantId, gates,
                _deps.GatePolicies, _deps.Approvals),
            Approvals = _deps.ApprovalService,
            Services = _deps.Services,
            Audit = audit,
            Notify = new NodeNotifier(
                instance.InstanceId, instance.TenantId, executor.Id,
                _deps.Events, _deps.Sequencer, _notifications,
                () => _currentSuperstep, _deps.Clock, instance.WorkflowName),
            ExecutorInvoked = async (executorId, superstep) =>
            {
                _hostInvocationEvents.Enqueue(executorId);
                await PublishAsync(instance, WorkflowEventTypes.ExecutorInvoked,
                    new { executorId, superstep }, executorId, CancellationToken.None).ConfigureAwait(false);
            },
            SuperstepAccessor = () => _currentSuperstep
        };

        return (Executor)executor;
    }

    private static ExecutorDelegate ResolveTerminal(IHostExecutor executor)
    {
        // ExecuteTerminalAsync is declared on the closed generic base; bind it without reflection cost
        // per invocation by resolving the delegate once here.
        Type type = executor.GetType();
        System.Reflection.MethodInfo? method = type.GetMethod(
            nameof(HostExecutor<object, object>.ExecuteTerminalAsync),
            [typeof(ExecutorInvocationContext), typeof(CancellationToken)]);

        if (method is null)
        {
            throw new InvalidOperationException($"Executor '{executor.Id}' does not expose a terminal delegate.");
        }

        return (ExecutorDelegate)Delegate.CreateDelegate(typeof(ExecutorDelegate), executor, method);
    }

    private async Task<RunOutcome> ApplyFailureAsync(
        WorkflowInstance instance,
        WorkflowDescriptor descriptor,
        string executorId,
        Exception error,
        CancellationToken cancellationToken)
    {
        var failure = new WorkflowFailure(
            executorId, error, instance.AttemptCount + 1, _currentSuperstep, new Dictionary<string, object?>());

        FailureDisposition disposition;
        try
        {
            disposition = descriptor.Definition.Classify(failure);
        }
        catch (Exception classifierError)
        {
            _deps.Logger.LogError(classifierError,
                "Failure classifier threw for instance {InstanceId}; treating as DeadStop.", instance.InstanceId);
            disposition = FailureDisposition.DeadStop;
        }

        DateTimeOffset now = _deps.Clock.GetUtcNow();
        int nextAttempt = instance.AttemptCount + 1;
        bool attemptsExhausted = nextAttempt >= instance.MaxAttempts;
        bool lifetimeExceeded = now - instance.CreatedAt > _deps.Options.Retry.MaxLifetime;

        switch (disposition)
        {
            case FailureDisposition.Retry when !attemptsExhausted && !lifetimeExceeded:
            {
                TimeSpan delay = Backoff.Exponential(
                    nextAttempt, _deps.Options.Retry.BackoffBase, _deps.Options.Retry.BackoffCap, _deps.Options.Retry.Jitter);

                await _deps.Instances.UpdateAsync(instance.InstanceId, m =>
                {
                    m.Status = InstanceStatus.RetryScheduled;
                    m.AttemptCount = nextAttempt;
                    m.NextRetryAt = now + delay;
                    m.TerminalReason = Describe(error);
                    m.ClearLease = true;
                }, cancellationToken).ConfigureAwait(false);

                return new RunOutcome(InstanceStatus.RetryScheduled, null, Describe(error), error);
            }

            case FailureDisposition.Escalate:
                await _deps.Instances.UpdateAsync(instance.InstanceId, m =>
                {
                    m.Status = InstanceStatus.AwaitingInput;
                    m.AttemptCount = nextAttempt;
                    m.TerminalReason = Describe(error);
                    m.ClearLease = true;
                }, cancellationToken).ConfigureAwait(false);

                return new RunOutcome(InstanceStatus.AwaitingInput, null, Describe(error), error);

            case FailureDisposition.DeadStop:
                await FinalizeAsync(instance, InstanceStatus.DeadStopped, null, Describe(error), cancellationToken)
                    .ConfigureAwait(false);
                return new RunOutcome(InstanceStatus.DeadStopped, null, Describe(error), error);

            case FailureDisposition.Retry:
            default:
                await FinalizeAsync(instance, InstanceStatus.Failed, null, Describe(error), cancellationToken)
                    .ConfigureAwait(false);
                return new RunOutcome(InstanceStatus.Failed, null, Describe(error), error);
        }
    }

    private async Task FinalizeAsync(
        WorkflowInstance instance, InstanceStatus status, object? result, string? reason, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _deps.Clock.GetUtcNow();

        await _deps.Instances.UpdateAsync(instance.InstanceId, m =>
        {
            m.Status = status;
            m.CompletedAt = now;
            m.TerminalReason = reason;
            m.ResultJson = result is null ? null : SafeSerialize(result);
            m.ClearLease = true;
        }, cancellationToken).ConfigureAwait(false);

        // A wait outliving its instance would keep matching messages forever and log a warning for
        // each one. Terminal means nothing is listening any more.
        if (_deps.EventSubscriptions is { } subscriptions)
        {
            await subscriptions.RemoveForInstanceAsync(instance.InstanceId, cancellationToken).ConfigureAwait(false);
        }

        await PublishAsync(instance, WorkflowEventTypes.WorkflowTerminated, new
        {
            status = status.ToString(),
            reason,
            correlationId = instance.CorrelationId
        }, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task TransitionAsync(
        WorkflowInstance instance, InstanceStatus status, string? reason, CancellationToken cancellationToken)
        => await _deps.Instances.UpdateAsync(instance.InstanceId, m =>
        {
            m.Status = status;
            m.TerminalReason = reason;
            m.LatestCheckpointId = _latestCheckpointId;
            m.ClearLease = true;
        }, cancellationToken).ConfigureAwait(false);

    private ValueTask PublishAsync(
        WorkflowInstance instance, string eventType, object payload, string? executorId, CancellationToken cancellationToken)
    {
        // Before Next(), never after. A suppressed event that had already consumed a sequence number
        // would leave a hole in the gapless sequence, and Last-Event-ID catch-up would wait forever
        // for an event that is never coming.
        if (!_notifications.ShouldEmit(eventType, executorId))
        {
            return ValueTask.CompletedTask;
        }

        return _deps.Events.PublishAsync(
            EventFactory.Create(
                instance.InstanceId, _deps.Sequencer.Next(instance.InstanceId), eventType, payload,
                executorId, _currentSuperstep, instance.TenantId, _deps.Clock.GetUtcNow(),
                instance.WorkflowName, _notifications.DeliveryFor(EventDeliveryMode.StreamAndLog)),
            cancellationToken);
    }

    /// <summary>
    /// Live fan-out with no durable record and no sequence number. Used for streamed tokens, where
    /// the complete text is in the executor's output and a replayed chunk would mean nothing.
    /// </summary>
    private ValueTask PublishTransientAsync(
        WorkflowInstance instance, string eventType, object payload, string? executorId, CancellationToken cancellationToken)
    {
        if (!_notifications.ShouldEmit(eventType, executorId))
        {
            return ValueTask.CompletedTask;
        }

        // Stream-only under a log-only workflow means nowhere, which is the correct reading: opting
        // out of streaming opts out of streamed tokens too.
        if (_notifications.IsLogOnly)
        {
            return ValueTask.CompletedTask;
        }

        return _deps.Events.PublishAsync(
            EventFactory.Create(
                instance.InstanceId, 0, eventType, payload,
                executorId, _currentSuperstep, instance.TenantId, _deps.Clock.GetUtcNow(),
                instance.WorkflowName, EventDeliveryMode.StreamOnly),
            cancellationToken);
    }

    private ValueTask LogAsync(
        WorkflowInstance instance, string level, string? executorId, Exception error, CancellationToken cancellationToken)
        => _deps.Logs is null
            ? ValueTask.CompletedTask
            : _deps.Logs.AppendAsync(new InstanceLogEntry
            {
                InstanceId = instance.InstanceId,
                Sequence = _deps.Sequencer.Peek(instance.InstanceId),
                Level = level,
                ExecutorId = executorId,
                Superstep = _currentSuperstep,
                Message = error.Message,
                ExceptionType = error.GetType().FullName,
                StackTrace = error.StackTrace,
                LoggedAt = _deps.Clock.GetUtcNow()
            }, cancellationToken);

    private async ValueTask<long> MaxSequenceAsync(string instanceId, CancellationToken cancellationToken)
    {
        if (_deps.Services?.GetService(typeof(IEventStore)) is IEventStore store)
        {
            return await store.MaxSequenceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        }
        return _deps.Sequencer.Peek(instanceId);
    }

    /// <summary>
    /// Starts the run with the context bound to its <em>declared</em> type.
    /// </summary>
    /// <remarks>
    /// <c>RunStreamingAsync&lt;TInput&gt;</c> infers <c>TInput</c> from the static type of the argument.
    /// Passing an <c>object</c>-typed context would make the start message an <c>object</c>, which
    /// matches no typed executor route — the workflow would complete having invoked nothing. The
    /// generic method is therefore closed over the descriptor's context type at run time.
    /// </remarks>
    private static async ValueTask<StreamingRun> StartTypedAsync(
        Workflow workflow,
        WorkflowDescriptor descriptor,
        object context,
        CheckpointManager checkpointManager,
        string sessionId,
        CancellationToken cancellationToken)
    {
        System.Reflection.MethodInfo definition = typeof(InProcessExecution)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Single(m => m.Name == nameof(InProcessExecution.RunStreamingAsync)
                         && m.IsGenericMethodDefinition
                         && m.GetParameters().Length == 5
                         && m.GetParameters()[2].ParameterType == typeof(CheckpointManager));

        object? result = definition
            .MakeGenericMethod(context.GetType())
            .Invoke(null, [workflow, context, checkpointManager, sessionId, cancellationToken]);

        return await (ValueTask<StreamingRun>)result!;
    }

    private static object DeserializeContext(WorkflowDescriptor descriptor, string? contextJson)
    {
        if (string.IsNullOrWhiteSpace(contextJson))
        {
            return Activator.CreateInstance(descriptor.ContextType)
                ?? throw new InvalidOperationException($"Cannot construct empty context of '{descriptor.ContextType.Name}'.");
        }

        return JsonSerializer.Deserialize(contextJson, descriptor.ContextType, JsonOptions.Default)
            ?? throw new WorkflowValidationException("context", "Context payload deserialized to null.");
    }

    private static string Describe(Exception error) => $"{error.GetType().Name}: {error.Message}";

    private static string? Describe(object? value)
        => value is null ? null : SafeSerialize(value);

    private static string SafeSerialize(object value)
    {
        try
        {
            return JsonSerializer.Serialize(value, JsonOptions.Default);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return JsonSerializer.Serialize(value.ToString());
        }
    }
}

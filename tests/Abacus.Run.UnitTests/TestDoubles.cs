using System.Text.Json;
using Abacus.Run.Abstractions;
using Abacus.Run.Abstractions.Middleware;
using Abacus.Run.Core;
using Microsoft.Agents.AI.Workflows;

namespace Abacus.Run.UnitTests;

/// <summary>Minimal <see cref="IWorkflowContext"/> so executors can be exercised in isolation.</summary>
public sealed class FakeWorkflowContext : IWorkflowContext
{
    private readonly Dictionary<string, object?> _state = [];

    public List<WorkflowEvent> Events { get; } = [];
    public List<object> SentMessages { get; } = [];
    public List<object> Outputs { get; } = [];
    public int HaltRequests { get; private set; }

    public IReadOnlyDictionary<string, string>? TraceContext => null;

    public bool ConcurrentRunsEnabled => false;

    public ValueTask AddEventAsync(WorkflowEvent workflowEvent, CancellationToken cancellationToken = default)
    {
        Events.Add(workflowEvent);
        return ValueTask.CompletedTask;
    }

    public ValueTask SendMessageAsync(object message, string? targetId, CancellationToken cancellationToken = default)
    {
        SentMessages.Add(message);
        return ValueTask.CompletedTask;
    }

    public ValueTask YieldOutputAsync(object output, CancellationToken cancellationToken = default)
    {
        Outputs.Add(output);
        return ValueTask.CompletedTask;
    }

    public ValueTask RequestHaltAsync()
    {
        HaltRequests++;
        return ValueTask.CompletedTask;
    }

    public ValueTask<T?> ReadStateAsync<T>(string key, string? scopeName = null, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_state.TryGetValue(Key(key, scopeName), out object? value) ? (T?)value : default);

    public ValueTask<T> ReadOrInitStateAsync<T>(
        string key, Func<T> initialStateFactory, string? scopeName = null, CancellationToken cancellationToken = default)
    {
        string composite = Key(key, scopeName);
        if (!_state.TryGetValue(composite, out object? value))
        {
            value = initialStateFactory();
            _state[composite] = value;
        }
        return ValueTask.FromResult((T)value!);
    }

    public ValueTask<HashSet<string>> ReadStateKeysAsync(string? scopeName = null, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_state.Keys.ToHashSet());

    public ValueTask QueueStateUpdateAsync<T>(
        string key, T? value, string? scopeName = null, CancellationToken cancellationToken = default)
    {
        _state[Key(key, scopeName)] = value;
        return ValueTask.CompletedTask;
    }

    public ValueTask QueueClearScopeAsync(string? scopeName = null, CancellationToken cancellationToken = default)
    {
        _state.Clear();
        return ValueTask.CompletedTask;
    }

    private static string Key(string key, string? scope) => scope is null ? key : $"{scope}::{key}";
}

public sealed record Payload(string Value = "in", decimal Amount = 0m);

public sealed record Outcome(string Value);

/// <summary>Executor that records whether its core body ran — the pivot for gate tests.</summary>
public sealed class ProbeExecutor : HostExecutor<Payload, Outcome>
{
    private readonly Func<Payload, Outcome>? _project;
    private readonly Exception? _throw;

    public ProbeExecutor(string id = "probe", Func<Payload, Outcome>? project = null, Exception? throws = null)
        : base(id)
    {
        _project = project;
        _throw = throws;
    }

    public int CoreInvocations { get; private set; }
    public Payload? LastInput { get; private set; }

    protected override ValueTask<Outcome> ExecuteCoreAsync(
        Payload input, IWorkflowContext context, CancellationToken cancellationToken)
    {
        CoreInvocations++;
        LastInput = input;

        if (_throw is not null)
        {
            throw _throw;
        }

        return ValueTask.FromResult(_project?.Invoke(input) ?? new Outcome($"handled:{input.Value}"));
    }
}

/// <summary>Gate evaluator returning a scripted outcome.</summary>
public sealed class ScriptedGateEvaluator : IGateEvaluator
{
    private readonly GateOutcome _outcome;

    public ScriptedGateEvaluator(GateOutcome outcome) => _outcome = outcome;

    public int Evaluations { get; private set; }

    public ValueTask<GateOutcome> EvaluateAsync(
        string instanceId, string executorId, object? input, CancellationToken cancellationToken)
    {
        Evaluations++;
        return ValueTask.FromResult(_outcome);
    }
}

public sealed class RecordingApprovalCoordinator : IApprovalCoordinator
{
    public List<(string InstanceId, string ExecutorId, ApprovalGate Gate, object? Input)> Raised { get; } = [];

    public ValueTask<ApprovalRequest> RaiseAsync(
        string instanceId, string executorId, ApprovalGate gate, object? input, int superstep, CancellationToken cancellationToken)
    {
        Raised.Add((instanceId, executorId, gate, input));

        return ValueTask.FromResult(new ApprovalRequest
        {
            ApprovalId = $"apr_{Raised.Count}",
            InstanceId = instanceId,
            TenantId = "t1",
            ExecutorId = executorId,
            CreatedAt = DateTimeOffset.UnixEpoch,
            ExpiresAt = DateTimeOffset.UnixEpoch.AddHours(1)
        });
    }
}

/// <summary>Middleware that records ordering and optionally mutates or short-circuits.</summary>
public sealed class RecordingExecutorMiddleware : IExecutorMiddleware
{
    private readonly List<string> _log;
    private readonly string _name;
    private readonly Action<ExecutorInvocationContext>? _before;
    private readonly Action<ExecutorInvocationContext>? _after;
    private readonly bool _shortCircuit;

    public RecordingExecutorMiddleware(
        List<string> log,
        string name,
        int order = 0,
        Action<ExecutorInvocationContext>? before = null,
        Action<ExecutorInvocationContext>? after = null,
        bool shortCircuit = false,
        Func<ExecutorDescriptor, bool>? appliesTo = null)
    {
        _log = log;
        _name = name;
        Order = order;
        _before = before;
        _after = after;
        _shortCircuit = shortCircuit;
        AppliesToPredicate = appliesTo;
    }

    public int Order { get; }
    public Func<ExecutorDescriptor, bool>? AppliesToPredicate { get; }

    public bool AppliesTo(ExecutorDescriptor descriptor) => AppliesToPredicate?.Invoke(descriptor) ?? true;

    public async ValueTask InvokeAsync(
        ExecutorInvocationContext context, ExecutorDelegate next, CancellationToken cancellationToken)
    {
        _log.Add($"{_name}:before");
        _before?.Invoke(context);

        if (!_shortCircuit)
        {
            await next(context, cancellationToken);
        }

        _after?.Invoke(context);
        _log.Add($"{_name}:after");
    }
}

public sealed class RecordingWorkflowMiddleware : IWorkflowMiddleware
{
    private readonly List<string> _log;
    private readonly string _name;

    public RecordingWorkflowMiddleware(List<string> log, string name, int order = 0)
    {
        _log = log;
        _name = name;
        Order = order;
    }

    public int Order { get; }

    public async ValueTask InvokeAsync(
        WorkflowInvocationContext context, WorkflowDelegate next, CancellationToken cancellationToken)
    {
        _log.Add($"{_name}:before");
        await next(context, cancellationToken);
        _log.Add($"{_name}:after");
    }
}

public static class TestFactory
{
    public static ExecutorDescriptor Descriptor(
        string executorId = "e1",
        Type? type = null,
        string workflow = "wf",
        string version = "1.0.0",
        ExecutionMode mode = ExecutionMode.Autonomous)
        => new(executorId, type ?? typeof(ProbeExecutor), workflow, version, mode);

    public static ExecutorInvocationContext Invocation(
        object? input = null, ExecutorDescriptor? descriptor = null, string instanceId = "i1")
        => new()
        {
            InstanceId = instanceId,
            Descriptor = descriptor ?? Descriptor(),
            Input = input ?? new Payload(),
            WorkflowContext = new FakeWorkflowContext()
        };

    public static WorkflowInvocationContext WorkflowInvocation(string instanceId = "i1")
        => new()
        {
            InstanceId = instanceId,
            TenantId = "t1",
            WorkflowName = "wf",
            WorkflowVersion = "1.0.0",
            Attempt = 1
        };

    public static EventEnvelope Event(
        string instanceId, long sequence, string type, string? executorId = null, string payload = "{}")
        => new()
        {
            InstanceId = instanceId,
            Sequence = sequence,
            EventType = type,
            ExecutorId = executorId,
            PayloadJson = payload,
            OccurredAt = DateTimeOffset.UnixEpoch.AddSeconds(sequence)
        };

    public static CreateInstanceRequest CreateRequest(
        string? instanceId = null,
        string tenant = "t1",
        string workflow = "wf",
        string version = "1.0.0",
        string? idempotencyKey = null,
        string? contextJson = null)
        => new()
        {
            InstanceId = instanceId ?? IdGenerator.NewId(),
            TenantId = tenant,
            WorkflowName = workflow,
            WorkflowVersion = version,
            IdempotencyKey = idempotencyKey,
            ContextJson = contextJson
        };

    public static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();
}

using System.Text.Json;
using Abacus.Run.Abstractions;
using Abacus.Run.Api;
using Abacus.Run.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Abacus.Run.Dispatch;

/// <summary>
/// Routes broker messages to workflows: a trigger match starts an instance, a wait match resumes one.
/// </summary>
/// <remarks>
/// <para>
/// This is where the broker stops being a transport and becomes part of the engine, and the whole
/// design rests on one rule: <b>delivery is a durable state transition, not a message.</b> A trigger
/// creates an instance row; a wait writes the payload to a subscription row and marks the instance
/// <see cref="InstanceStatus.Dispatchable"/>. The existing dispatcher then claims it exactly as it
/// claims post-approval work. Nothing is held in memory waiting to be acted on, which is what lets an
/// event-driven pipeline survive a restart.
/// </para>
/// <para>
/// The transport only makes that transition fast. It is never what makes it happen.
/// </para>
/// </remarks>
public sealed class DomainEventDispatcher : BackgroundService
{
    private readonly IDomainEventBroker _broker;
    private readonly IDomainEventSubscriptionStore _subscriptions;
    private readonly IInstanceLauncher _launcher;
    private readonly IInstanceStore _instances;
    private readonly IWorkflowRegistry _registry;
    private readonly INotificationSink _events;
    private readonly NotificationSequencer _sequencer;
    private readonly ILogger<DomainEventDispatcher> _logger;
    private readonly TimeProvider _clock;

    private IAsyncDisposable? _subscription;
    private long _unrouted;

    public DomainEventDispatcher(
        IDomainEventBroker broker,
        IDomainEventSubscriptionStore subscriptions,
        IInstanceLauncher launcher,
        IInstanceStore instances,
        IWorkflowRegistry registry,
        INotificationSink events,
        NotificationSequencer sequencer,
        ILogger<DomainEventDispatcher> logger,
        TimeProvider? clock = null)
    {
        _broker = broker;
        _subscriptions = subscriptions;
        _launcher = launcher;
        _instances = instances;
        _registry = registry;
        _events = events;
        _sequencer = sequencer;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Messages that matched no subscription. Exposed because a silently unrouted message is the most
    /// likely production complaint and the hardest to see from the outside.
    /// </summary>
    public long UnroutedCount => Interlocked.Read(ref _unrouted);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await LoadTriggersAsync(stoppingToken).ConfigureAwait(false);

        _subscription = await _broker.SubscribeAsync(
            new DomainEventSubscriptionOptions
            {
                TopicFilter = "#",
                Name = "abacus.broker-dispatch",
                ConsumerGroup = "abacus.broker-dispatch"
            },
            HandleAsync,
            stoppingToken).ConfigureAwait(false);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        finally
        {
            if (_subscription is { } handle)
            {
                await handle.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Rebuilds trigger subscriptions from the registry. Triggers are declared in code, so the
    /// registry is their source of truth and this is safe to run on every start.
    /// </summary>
    internal async Task LoadTriggersAsync(CancellationToken cancellationToken)
    {
        foreach (WorkflowDescriptor descriptor in _registry.All)
        {
            if (descriptor.Definition is not IDomainEventTriggeredWorkflow triggered)
            {
                continue;
            }

            foreach (DomainEventTrigger trigger in triggered.Triggers)
            {
                if (!TopicPattern.IsValidPattern(trigger.TopicFilter, out string? error))
                {
                    // Startup, not delivery time: a bad filter must not look like a quiet upstream.
                    throw new InvalidOperationException(
                        $"Workflow '{descriptor.Name}' declares an invalid event trigger. {error}");
                }

                await _subscriptions.RegisterAsync(new DomainEventSubscription
                {
                    SubscriptionId = $"trigger:{descriptor.Name}:{descriptor.Version}:{trigger.TopicFilter}",
                    Kind = DomainSubscriptionKind.Trigger,
                    TopicFilter = trigger.TopicFilter,
                    CorrelationKey = trigger.CorrelationKey,
                    WorkflowName = descriptor.Name,
                    WorkflowVersion = trigger.WorkflowVersion,
                    CreatedAt = _clock.GetUtcNow()
                }, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "Workflow {Workflow} is triggered by {TopicFilter}.", descriptor.Name, trigger.TopicFilter);
            }
        }
    }

    /// <summary>Routes one message. Public so tests can drive it without a running host.</summary>
    public async ValueTask<DeliveryResult> HandleAsync(DomainEventDelivery delivery, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        DomainEventMessage message = delivery.Message;

        IReadOnlyList<DomainEventSubscription> matches =
            await _subscriptions.MatchAsync(message, cancellationToken).ConfigureAwait(false);

        if (matches.Count == 0)
        {
            Interlocked.Increment(ref _unrouted);
            _logger.LogDebug(
                "Message {MessageId} on {Topic} matched no subscription.", message.MessageId, message.Topic);
            return DeliveryResult.Ack;
        }

        var failures = new List<string>();

        foreach (DomainEventSubscription subscription in matches)
        {
            try
            {
                switch (subscription.Kind)
                {
                    case DomainSubscriptionKind.Trigger:
                        await TriggerAsync(subscription, message, cancellationToken).ConfigureAwait(false);
                        break;

                    case DomainSubscriptionKind.Wait:
                        await ResumeAsync(subscription, message, cancellationToken).ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad subscription must not stop the others from being served.
                _logger.LogError(ex,
                    "Subscription {SubscriptionId} failed handling message {MessageId} on {Topic}.",
                    subscription.SubscriptionId, message.MessageId, message.Topic);
                failures.Add(subscription.SubscriptionId);
            }
        }

        return failures.Count == 0
            ? DeliveryResult.Ack
            : DeliveryResult.Retry($"{failures.Count} subscription(s) failed: {string.Join(", ", failures)}");
    }

    private async Task TriggerAsync(
        DomainEventSubscription subscription, DomainEventMessage message, CancellationToken cancellationToken)
    {
        string contextJson = message.PayloadJson;

        if (_registry.Resolve(subscription.WorkflowName!, subscription.WorkflowVersion) is { } descriptor &&
            descriptor.Definition is IDomainEventTriggeredWorkflow triggered)
        {
            DomainEventTrigger? declared = triggered.Triggers.FirstOrDefault(t =>
                string.Equals(t.TopicFilter, subscription.TopicFilter, StringComparison.Ordinal));

            if (declared?.ContextSelector is { } selector)
            {
                contextJson = selector(message);
            }
        }

        using JsonDocument document = JsonDocument.Parse(contextJson);

        // The launcher already de-duplicates on idempotency key, so a redelivered message costs a
        // lookup rather than a second instance. No separate dedup table is needed.
        StartResult result = await _launcher.StartAsync(
            subscription.WorkflowName!,
            subscription.WorkflowVersion,
            new StartInstanceRequest
            {
                Context = document.RootElement.Clone(),
                CorrelationId = message.CorrelationKey
            },
            message.TenantId ?? "default",
            $"evt:{subscription.SubscriptionId}:{message.MessageId}",
            cancellationToken).ConfigureAwait(false);

        switch (result.Kind)
        {
            case StartResultKind.Accepted when result.Instance is { } instance:
                await EmitAsync(instance.InstanceId, instance.TenantId, DomainEventNotifications.EventTriggered, new
                {
                    topic = message.Topic,
                    messageId = message.MessageId,
                    subscriptionId = subscription.SubscriptionId,
                    sourceInstanceId = message.SourceInstanceId
                }, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "Message {MessageId} on {Topic} started {Workflow} instance {InstanceId}.",
                    message.MessageId, message.Topic, subscription.WorkflowName, instance.InstanceId);
                break;

            case StartResultKind.Duplicate:
                _logger.LogDebug(
                    "Message {MessageId} was already delivered to {SubscriptionId}; no instance started.",
                    message.MessageId, subscription.SubscriptionId);
                break;

            case StartResultKind.InvalidContext:
                // Retrying will not fix a payload the workflow cannot accept. Say so loudly instead
                // of looping, because this is a contract mismatch between publisher and consumer.
                _logger.LogError(
                    "Message {MessageId} on {Topic} is not a valid context for {Workflow}: {Errors}.",
                    message.MessageId, message.Topic, subscription.WorkflowName,
                    result.Errors is null ? "unspecified" : string.Join("; ", result.Errors.Select(e => $"{e.Key}: {string.Join(",", e.Value)}")));
                break;

            case StartResultKind.UnknownWorkflow:
                _logger.LogError(
                    "Trigger {SubscriptionId} names workflow '{Workflow}', which is not registered.",
                    subscription.SubscriptionId, subscription.WorkflowName);
                break;
        }
    }

    private async Task ResumeAsync(
        DomainEventSubscription subscription, DomainEventMessage message, CancellationToken cancellationToken)
    {
        bool won = await _subscriptions
            .TryDeliverAsync(subscription.SubscriptionId, message, cancellationToken).ConfigureAwait(false);

        if (!won)
        {
            // Another replica, or an earlier delivery of the same message, already resumed it.
            return;
        }

        string instanceId = subscription.InstanceId!;
        WorkflowInstance? instance = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);

        if (instance is null || instance.Status.IsTerminal())
        {
            _logger.LogWarning(
                "Wait {SubscriptionId} resolved for instance {InstanceId}, which is {Status}.",
                subscription.SubscriptionId, instanceId, instance?.Status.ToString() ?? "missing");
            return;
        }

        await EmitAsync(instanceId, instance.TenantId, DomainEventNotifications.EventDelivered, new
        {
            topic = message.Topic,
            messageId = message.MessageId,
            executorId = subscription.ExecutorId,
            correlationKey = message.CorrelationKey,
            sourceInstanceId = message.SourceInstanceId
        }, cancellationToken).ConfigureAwait(false);

        // The payload is already durable on the subscription row, so this transition is the only
        // thing standing between the message and the instance running again.
        await _instances.UpdateAsync(instanceId, m =>
        {
            m.Status = InstanceStatus.Dispatchable;
            m.ClearLease = true;
        }, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Message {MessageId} on {Topic} resumed instance {InstanceId} at executor {ExecutorId}.",
            message.MessageId, message.Topic, instanceId, subscription.ExecutorId);
    }

    private ValueTask EmitAsync(
        string instanceId, string? tenantId, string eventType, object payload, CancellationToken cancellationToken)
        => _events.PublishAsync(
            NotificationFactory.Create(
                instanceId, _sequencer.Next(instanceId), eventType, payload,
                tenantId: tenantId, at: _clock.GetUtcNow()),
            cancellationToken);
}

/// <summary>Applies wait-expiry policy on a fixed cadence, mirroring approval expiry.</summary>
public sealed class DomainEventWaitSweeper : BackgroundService
{
    private readonly IDomainEventSubscriptionStore _subscriptions;
    private readonly IInstanceStore _instances;
    private readonly INotificationSink _events;
    private readonly NotificationSequencer _sequencer;
    private readonly ILogger<DomainEventWaitSweeper> _logger;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _interval;

    public DomainEventWaitSweeper(
        IDomainEventSubscriptionStore subscriptions,
        IInstanceStore instances,
        INotificationSink events,
        NotificationSequencer sequencer,
        ILogger<DomainEventWaitSweeper> logger,
        TimeProvider? clock = null,
        TimeSpan? interval = null)
    {
        _subscriptions = subscriptions;
        _instances = instances;
        _events = events;
        _sequencer = sequencer;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _interval = interval ?? TimeSpan.FromSeconds(30);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, _clock, stoppingToken).ConfigureAwait(false);
                int swept = await SweepAsync(stoppingToken).ConfigureAwait(false);
                if (swept > 0)
                {
                    _logger.LogInformation("Applied expiry policy to {Count} event wait(s).", swept);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Event wait expiry sweep failed.");
            }
        }
    }

    /// <summary>One sweep. Public so tests can drive it deterministically.</summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<DomainEventSubscription> expired = await _subscriptions
            .ClaimExpiredAsync(_clock.GetUtcNow(), 100, cancellationToken).ConfigureAwait(false);

        foreach (DomainEventSubscription subscription in expired)
        {
            string instanceId = subscription.InstanceId!;
            WorkflowInstance? instance = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);

            if (instance is null || instance.Status.IsTerminal())
            {
                continue;
            }

            await _events.PublishAsync(
                NotificationFactory.Create(
                    instanceId, _sequencer.Next(instanceId), DomainEventNotifications.EventWaitExpired, new
                    {
                        topic = subscription.TopicFilter,
                        executorId = subscription.ExecutorId,
                        onExpiry = subscription.OnExpiry.ToString()
                    },
                    tenantId: instance.TenantId, at: _clock.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);

            await _instances.UpdateAsync(instanceId, m =>
            {
                if (subscription.OnExpiry == WaitExpiryAction.Resume)
                {
                    m.Status = InstanceStatus.Dispatchable;
                    m.ClearLease = true;
                }
                else
                {
                    m.Status = InstanceStatus.DeadStopped;
                    m.TerminalReason = $"Timed out waiting for '{subscription.TopicFilter}'.";
                    m.CompletedAt = _clock.GetUtcNow();
                    m.ClearLease = true;
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        return expired.Count;
    }
}

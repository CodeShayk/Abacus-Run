using System.Text.Json;
using Abacus.Run.Abstractions;
using Abacus.Run.Core;
using Microsoft.Agents.AI.Workflows;

namespace Abacus.Run.Executors;

/// <summary>Publishes a domain message and passes its input through unchanged.</summary>
/// <remarks>
/// Pass-through rather than transforming: publishing is a side effect on the way past, so a node can
/// be dropped into an existing edge without rewiring the graph around it.
/// </remarks>
public sealed class PublishDomainEventExecutor<T> : HostExecutor<T, T>
    where T : class
{
    private readonly IDomainEventBroker _broker;
    private readonly Func<T, string> _topic;
    private readonly Func<T, object>? _payload;
    private readonly Func<T, string?>? _correlationKey;
    private readonly DeliveryScope _scope;
    private readonly TimeProvider _clock;

    public PublishDomainEventExecutor(
        string id,
        IDomainEventBroker broker,
        Func<T, string> topic,
        Func<T, object>? payload = null,
        Func<T, string?>? correlationKey = null,
        DeliveryScope scope = DeliveryScope.Local,
        TimeProvider? clock = null) : base(id)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _topic = topic ?? throw new ArgumentNullException(nameof(topic));
        _payload = payload;
        _correlationKey = correlationKey;
        _scope = scope;
        _clock = clock ?? TimeProvider.System;

        if (scope == DeliveryScope.Distributed && !_broker.Capabilities.SupportsDistributed)
        {
            // Construction time, not publish time: this is a composition mistake, and finding it on
            // the first message would mean finding it in production.
            throw new NotSupportedException(
                $"Executor '{id}' publishes at {nameof(DeliveryScope.Distributed)} scope, but the registered " +
                "broker delivers locally only. Register a distributed broker or publish as Local.");
        }
    }

    /// <summary>Convenience overload for a fixed topic.</summary>
    public PublishDomainEventExecutor(
        string id,
        IDomainEventBroker broker,
        string topic,
        Func<T, object>? payload = null,
        Func<T, string?>? correlationKey = null,
        DeliveryScope scope = DeliveryScope.Local,
        TimeProvider? clock = null)
        : this(id, broker, _ => topic, payload, correlationKey, scope, clock)
        => TopicPattern.ValidatePattern(topic);

    public override IReadOnlyDictionary<string, object?> Metadata =>
        new Dictionary<string, object?> { ["node.kind"] = "publish-event", ["event.scope"] = _scope.ToString() };

    protected override async ValueTask<T> ExecuteCoreAsync(
        T input, IWorkflowContext context, CancellationToken cancellationToken)
    {
        string topic = _topic(input);

        if (!TopicPattern.IsValidTopic(topic, out string? error))
        {
            throw new InvalidOperationException($"Executor '{Id}' produced an invalid topic. {error}");
        }

        object payload = _payload is null ? input : _payload(input);

        await _broker.PublishAsync(new DomainEventMessage
        {
            MessageId = IdGenerator.NewId("msg"),
            Topic = topic,
            PayloadJson = JsonSerializer.Serialize(payload, JsonOptions.Default),
            Scope = _scope,
            CorrelationKey = _correlationKey?.Invoke(input),
            TenantId = Runtime.TenantId,
            SourceInstanceId = Runtime.InstanceId,
            OccurredAt = _clock.GetUtcNow()
        }, cancellationToken).ConfigureAwait(false);

        return input;
    }
}

/// <summary>
/// Parks the instance until a matching message arrives, then resumes with its payload.
/// </summary>
/// <remarks>
/// <para>
/// The first pass registers a durable wait and halts; the instance checkpoints and releases its
/// lease, so a workflow can wait for days without costing execution capacity. When
/// <see cref="Dispatch.DomainEventDispatcher"/> records a delivery it marks the instance
/// dispatchable, the runner replays to this executor, and the second pass finds the payload and
/// returns it.
/// </para>
/// <para>
/// The park mechanism — <c>RequestHaltAsync</c> then a null result the engine declines to send — is
/// the same one an approval gate uses, so no change to the runner is needed to support waiting.
/// </para>
/// </remarks>
public sealed class WaitForDomainEventExecutor<TIn, TPayload> : HostExecutor<TIn, TPayload>
    where TPayload : class
{
    private readonly IDomainEventSubscriptionStore _subscriptions;
    private readonly string _topicFilter;
    private readonly Func<TIn, string?>? _correlationKey;
    private readonly TimeSpan? _timeout;
    private readonly WaitExpiryAction _onExpiry;
    private readonly TimeProvider _clock;

    public WaitForDomainEventExecutor(
        string id,
        IDomainEventSubscriptionStore subscriptions,
        string topicFilter,
        Func<TIn, string?>? correlationKey = null,
        TimeSpan? timeout = null,
        WaitExpiryAction onExpiry = WaitExpiryAction.DeadStop,
        TimeProvider? clock = null) : base(id)
    {
        _subscriptions = subscriptions ?? throw new ArgumentNullException(nameof(subscriptions));
        TopicPattern.ValidatePattern(topicFilter);
        _topicFilter = topicFilter;
        _correlationKey = correlationKey;
        _timeout = timeout;
        _onExpiry = onExpiry;
        _clock = clock ?? TimeProvider.System;
    }

    public override IReadOnlyDictionary<string, object?> Metadata =>
        new Dictionary<string, object?>
        {
            ["node.kind"] = "wait-for-event",
            ["event.topicFilter"] = _topicFilter,
            ["event.timeoutSeconds"] = _timeout?.TotalSeconds
        };

    protected override async ValueTask<TPayload> ExecuteCoreAsync(
        TIn input, IWorkflowContext context, CancellationToken cancellationToken)
    {
        DomainEventSubscription? existing = await _subscriptions
            .FindWaitAsync(Runtime.InstanceId, Id, cancellationToken).ConfigureAwait(false);

        if (existing?.DeliveredPayloadJson is { } json)
        {
            return JsonSerializer.Deserialize<TPayload>(json, JsonOptions.Default)
                ?? throw new InvalidOperationException(
                    $"Executor '{Id}' received a message on '{_topicFilter}' that deserialized to null.");
        }

        if (existing is { Expired: true })
        {
            // Only reachable under WaitExpiryAction.Resume; DeadStop terminates the instance instead.
            throw new WorkflowDeadStopException(
                $"Executor '{Id}' timed out waiting for '{_topicFilter}'.");
        }

        if (existing is null)
        {
            await _subscriptions.RegisterAsync(new DomainEventSubscription
            {
                SubscriptionId = IdGenerator.NewId("sub"),
                Kind = DomainSubscriptionKind.Wait,
                TopicFilter = _topicFilter,
                CorrelationKey = _correlationKey?.Invoke(input),
                InstanceId = Runtime.InstanceId,
                ExecutorId = Id,
                ExpiresAt = _timeout is { } timeout ? _clock.GetUtcNow() + timeout : null,
                OnExpiry = _onExpiry,
                CreatedAt = _clock.GetUtcNow()
            }, cancellationToken).ConfigureAwait(false);
        }

        await context.RequestHaltAsync().ConfigureAwait(false);
        return null!;   // never auto-sent: the engine skips null handler results
    }
}

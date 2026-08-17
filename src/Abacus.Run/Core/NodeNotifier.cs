using Abacus.Run.Abstractions;

namespace Abacus.Run.Core;

/// <summary>
/// The per-executor <see cref="INodeNotifier"/> the runner binds into
/// <see cref="HostExecutorRuntime"/>.
/// </summary>
/// <remarks>
/// It publishes through the same <see cref="INotificationSink"/> and the same <see cref="NotificationSequencer"/>
/// the runner uses. That shared sequencer is the point: a custom event lands correctly interleaved
/// with the lifecycle events around it, rather than on a parallel numbering that a consumer would
/// have to reconcile.
/// </remarks>
public sealed class NodeNotifier : INodeNotifier
{
    /// <summary>Prefix applied to every author-supplied name. Not optional, and not configurable.</summary>
    public const string CustomPrefix = "custom.";

    private readonly string _instanceId;
    private readonly string? _tenantId;
    private readonly string? _workflowName;
    private readonly string _executorId;
    private readonly INotificationSink _events;
    private readonly NotificationSequencer _sequencer;
    private readonly NotificationPolicy _policy;
    private readonly Func<int> _superstep;
    private readonly TimeProvider _clock;

    public NodeNotifier(
        string instanceId,
        string? tenantId,
        string executorId,
        INotificationSink events,
        NotificationSequencer sequencer,
        NotificationPolicy policy,
        Func<int> superstep,
        TimeProvider clock,
        string? workflowName = null)
    {
        _instanceId = instanceId;
        _tenantId = tenantId;
        _workflowName = workflowName;
        _executorId = executorId;
        _events = events;
        _sequencer = sequencer;
        _policy = policy;
        _superstep = superstep;
        _clock = clock;
    }

    public ValueTask NotifyAsync(string name, object payload, CancellationToken cancellationToken)
    {
        ValidateName(name);
        return PublishAsync(CustomPrefix + name, payload, transient: false, cancellationToken);
    }

    public ValueTask EmitReservedAsync(
        string eventType, object payload, bool transient, CancellationToken cancellationToken)
    {
        if (!WorkflowEventTypes.IsReserved(eventType))
        {
            // Keeps NotifyAsync the only door open to workflow authors: an unknown type here is
            // either a typo or an attempt to forge a framework event, and neither should reach a
            // subscriber looking authentic.
            throw new ArgumentException(
                $"'{eventType}' is not a framework event type. Use {nameof(NotifyAsync)} for workflow-defined events.",
                nameof(eventType));
        }

        return PublishAsync(eventType, payload, transient, cancellationToken);
    }

    private ValueTask PublishAsync(
        string eventType, object payload, bool transient, CancellationToken cancellationToken)
    {
        if (!_policy.ShouldEmit(eventType, _executorId))
        {
            return ValueTask.CompletedTask;
        }

        EventDeliveryMode delivery = _policy.DeliveryFor(
            transient ? EventDeliveryMode.StreamOnly : EventDeliveryMode.StreamAndLog);

        // A stream-only event takes no sequence number, so the durable sequence stays gapless.
        long sequence = delivery == EventDeliveryMode.StreamOnly ? 0 : _sequencer.Next(_instanceId);

        EventEnvelope envelope = NotificationFactory.Create(
            _instanceId, sequence, eventType, payload,
            _executorId, _superstep(), _tenantId, _clock.GetUtcNow(), _workflowName, delivery);

        return _events.PublishAsync(envelope, cancellationToken);
    }

    /// <summary>
    /// A malformed type is indistinguishable from an event that was never sent, so a bad name fails
    /// at the call site rather than producing something a consumer will never match.
    /// </summary>
    public static void ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A notification name is required.", nameof(name));
        }

        if (name.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException(
                $"Notification name '{name}' contains whitespace; use dots to namespace it.", nameof(name));
        }

        if (name.StartsWith('.') || name.EndsWith('.') || name.Contains(".."))
        {
            throw new ArgumentException(
                $"Notification name '{name}' has an empty segment.", nameof(name));
        }
    }
}

/// <summary>How much of a run's progress reaches subscribers.</summary>
public enum NotificationLevel
{
    /// <summary>
    /// Start, output and terminal only. For a high-fan-out workflow whose per-node traffic is the
    /// dominant write volume in the system.
    /// </summary>
    Minimal,

    /// <summary>Adds superstep boundaries — progress without per-node chatter.</summary>
    Lifecycle,

    /// <summary>Adds <c>executor.*</c>, <c>llm.*</c> and workflow-defined events. The default.</summary>
    Standard
}

/// <summary>
/// Declares that a workflow has an opinion about what its runs emit. Opt-in, mirroring
/// <c>IAuditedWorkflowDefinition</c>: a definition that says nothing keeps the default behaviour and
/// pays nothing for the feature.
/// </summary>
public interface INotifyingWorkflow
{
    NotificationPolicy Notifications { get; }
}

public sealed record NotificationPolicy
{
    /// <summary>Emits everything, streamed and logged. Used when a definition declares no policy.</summary>
    public static NotificationPolicy Default { get; } = new();

    public NotificationLevel Level { get; init; } = NotificationLevel.Standard;

    /// <summary>
    /// Whether this workflow's events also reach live SSE subscribers. Defaults to true.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the only delivery knob a workflow has, and it switches one thing: the live stream.
    /// <strong>The durable event log is not optional and cannot be turned off.</strong> Every event
    /// this workflow emits is written to the log whatever this is set to — the run is always
    /// reconstructable afterwards, and no configuration can make it otherwise.
    /// </para>
    /// <para>
    /// Setting it false suits a run nobody watches as it happens: a nightly batch, or work whose
    /// events are read afterwards for reconciliation. Only the timing of observability changes, not
    /// whether it exists. Read the log at <c>GET /v2/workflows/{name}/instances/{id}/events</c>.
    /// </para>
    /// <para>
    /// The SSE endpoint then refuses rather than holding open a stream that will never produce
    /// anything, because a silently empty stream is indistinguishable from a stalled run.
    /// </para>
    /// </remarks>
    public bool StreamEvents { get; init; } = true;

    /// <summary>True when this workflow has opted out of live streaming. The log is unaffected.</summary>
    public bool IsLogOnly => !StreamEvents;

    /// <summary>Per-executor overrides: quiet a chatty fan-out, keep the interesting node loud.</summary>
    public IReadOnlyDictionary<string, NotificationLevel> ByNode { get; init; } =
        new Dictionary<string, NotificationLevel>(StringComparer.Ordinal);

    /// <summary>
    /// Workflow-defined names this workflow emits, advertised by the catalog API the way node
    /// descriptors already advertise gates. Declared without the <c>custom.</c> prefix.
    /// </summary>
    public IReadOnlyList<string> Emits { get; init; } = [];

    /// <summary>
    /// Whether this event reaches subscribers. Consulted <em>before</em> a sequence number is taken —
    /// a suppressed event that had consumed one would leave a hole in the gapless sequence, and
    /// <c>Last-Event-ID</c> catch-up would wait forever for an event that will never arrive.
    /// </summary>
    public bool ShouldEmit(string eventType, string? executorId)
    {
        ArgumentNullException.ThrowIfNull(eventType);

        // A subscriber's stream closes on workflow.terminated, and approvals, control actions and
        // broker deliveries are facts about the system rather than run chatter. A workflow has no
        // business hiding any of them.
        if (IsNeverSuppressed(eventType))
        {
            return true;
        }

        NotificationLevel level = executorId is not null && ByNode.TryGetValue(executorId, out NotificationLevel node)
            ? node
            : Level;

        return level switch
        {
            NotificationLevel.Standard => true,
            NotificationLevel.Lifecycle => IsSuperstep(eventType),
            _ => false
        };
    }

    private static bool IsSuperstep(string eventType)
        => eventType is WorkflowEventTypes.SuperstepStarted or WorkflowEventTypes.SuperstepCompleted;

    private static bool IsNeverSuppressed(string eventType)
        => eventType is WorkflowEventTypes.WorkflowStarted
                     or WorkflowEventTypes.WorkflowOutput
                     or WorkflowEventTypes.WorkflowWarning
                     or WorkflowEventTypes.WorkflowTerminated
                     or WorkflowEventTypes.RequestPending
        || eventType.StartsWith("approval.", StringComparison.Ordinal)
        || eventType.StartsWith("instance.", StringComparison.Ordinal)
        || eventType.StartsWith("event.", StringComparison.Ordinal);

    /// <summary>
    /// Resolves where one event goes. Anything durable is logged unconditionally and only loses the
    /// stream; <see cref="EventDeliveryMode.StreamOnly"/> is a framework-internal mode for events
    /// with no replay value, and a workflow cannot ask for it.
    /// </summary>
    /// <remarks>
    /// A stream-only event under a non-streaming workflow goes nowhere, and that is the correct
    /// reading: a workflow that has opted out of streaming has opted out of streamed tokens too. It
    /// costs no durable record, because a stream-only event never had one.
    /// </remarks>
    public EventDeliveryMode DeliveryFor(EventDeliveryMode requested)
        => requested == EventDeliveryMode.StreamOnly
            ? EventDeliveryMode.StreamOnly
            : StreamEvents ? EventDeliveryMode.StreamAndLog : EventDeliveryMode.LogOnly;

    /// <summary>Validates declared names at composition time, so a bad one fails startup.</summary>
    public void Validate(string workflowName)
    {
        foreach (string name in Emits)
        {
            try
            {
                NodeNotifier.ValidateName(name);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException(
                    $"Workflow '{workflowName}' declares an invalid notification name. {ex.Message}", ex);
            }
        }
    }
}

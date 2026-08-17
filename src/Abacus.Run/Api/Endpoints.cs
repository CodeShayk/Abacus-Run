using System.Text.Json;
using Abacus.Run.Abstractions;
using Abacus.Run.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Abacus.Run.Api;

public sealed record InstanceDto(
    string InstanceId,
    string WorkflowName,
    string WorkflowVersion,
    string Status,
    string TenantId,
    int AttemptCount,
    string? TerminalReason,
    string? CorrelationId,
    string? RerunOfInstanceId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed record ApprovalDto(
    string ApprovalId,
    string InstanceId,
    string ExecutorId,
    string? Reason,
    string State,
    IReadOnlyList<string> Assignees,
    int RequiredApprovers,
    bool AllowModification,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string DecisionUrl);

public sealed record DecisionRequestDto(string Decision, string? Comment, JsonElement? ModifiedInput);

public sealed record CancelRequestDto(string? Reason);

public sealed record RerunRequestDto(string? Mode, JsonElement? Context, string? FromCheckpointId, string? Reason);

public sealed record PublishEventRequestDto(
    string? Topic, JsonElement? Payload, string? CorrelationKey, string? Scope);

public sealed record SubscriptionDto(
    string SubscriptionId,
    string Kind,
    string TopicFilter,
    string? CorrelationKey,
    string? TenantId,
    string? InstanceId,
    string? ExecutorId,
    string? WorkflowName,
    bool Satisfied,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset CreatedAt);

public static class Endpoints
{
    public static IEndpointRouteBuilder MapWorkflowApi(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        MapCatalog(app);
        MapInstances(app);
        MapEvents(app);
        MapControl(app);
        MapApprovals(app);
        MapDiagnostics(app);

        return app;
    }

    private static void MapDiagnostics(IEndpointRouteBuilder app)
    {
        app.MapGet("/diagnostics/metrics", async (IInstanceStore instances, IApprovalStore approvals, CancellationToken ct) =>
        {
            Page<WorkflowInstance> active = await instances.QueryAsync(new InstanceQuery { Statuses = [InstanceStatus.Running] }, ct);
            Page<WorkflowInstance> completed = await instances.QueryAsync(new InstanceQuery { Statuses = [InstanceStatus.Completed] }, ct);
            Page<WorkflowInstance> failed = await instances.QueryAsync(new InstanceQuery { Statuses = [InstanceStatus.Failed, InstanceStatus.DeadStopped] }, ct);
            Page<WorkflowInstance> pending = await instances.QueryAsync(new InstanceQuery { Statuses = [InstanceStatus.Pending, InstanceStatus.RetryScheduled, InstanceStatus.Dispatchable] }, ct);
            IReadOnlyList<ApprovalRequest> pendingApprovals = await approvals.QueryPendingAsync(null, null, 1000, ct);

            return Results.Ok(new
            {
                activeInstances = active.Total,
                completedInstances = completed.Total,
                failedInstances = failed.Total,
                awaitingApproval = pendingApprovals.Count,
                pendingInstances = pending.Total,
                throughputPerMinute = 0.0,
                errorRate = 0.0
            });
        });
    }

    private static void MapCatalog(IEndpointRouteBuilder app)
    {
        app.MapGet("/workflows", (IWorkflowRegistry registry) => Results.Ok(
            registry.All.Select(d => new
            {
                name = d.Name,
                version = d.Version,
                contextType = d.ContextType.Name,
                resultType = d.ResultType.Name
            })));

        app.MapGet("/workflows/{name}", (string name, IWorkflowRegistry registry) =>
        {
            IReadOnlyList<WorkflowDescriptor> versions = registry.VersionsOf(name);
            return versions.Count == 0
                ? Results.NotFound()
                : Results.Ok(new
                {
                    name,
                    versions = versions.Select(v => new
                    {
                        version = v.Version,
                        contextType = v.ContextType.Name,
                        resultType = v.ResultType.Name
                    })
                });
        });

        MapNodeConfiguration(app);
    }

    /// <summary>
    /// The executor nodes of one workflow version, and the tenant's execution policy over them.
    /// Nodes are autonomous unless the definition declared a gate or the tenant configured one.
    /// </summary>
    private static void MapNodeConfiguration(IEndpointRouteBuilder app)
    {
        app.MapGet("/workflows/{name}/versions/{version}/nodes", async (
            string name, string version, HttpContext http, IGateConfigurationService config, CancellationToken cancellationToken) =>
            (await config.GetNodesAsync(name, version, http.TenantId(), cancellationToken).ConfigureAwait(false))
                .ToHttpResult());

        app.MapPut("/workflows/{name}/versions/{version}/nodes", async (
            string name, string version, NodeConfigurationRequestDto? body, HttpContext http,
            IGateConfigurationService config, CancellationToken cancellationToken) =>
        {
            if (body?.Nodes is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["nodes"] = ["A map of executor id to policy is required."]
                });
            }

            return (await config
                .SetNodesAsync(name, version, http.TenantId(), body.Nodes, http.User, cancellationToken)
                .ConfigureAwait(false))
                .ToHttpResult();
        });

        app.MapPut("/workflows/{name}/versions/{version}/nodes/{executorId}", async (
            string name, string version, string executorId, ExecutionPolicyDto? body, HttpContext http,
            IGateConfigurationService config, CancellationToken cancellationToken) =>
        {
            if (body is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["mode"] = ["A policy body is required."]
                });
            }

            return (await config
                .SetNodesAsync(name, version, http.TenantId(),
                    new Dictionary<string, ExecutionPolicyDto> { [executorId] = body }, http.User, cancellationToken)
                .ConfigureAwait(false))
                .ToHttpResult();
        });

        app.MapDelete("/workflows/{name}/versions/{version}/nodes/{executorId}", async (
            string name, string version, string executorId, HttpContext http,
            IGateConfigurationService config, CancellationToken cancellationToken) =>
            (await config
                .ResetNodeAsync(name, version, http.TenantId(), executorId, http.User, cancellationToken)
                .ConfigureAwait(false))
                .ToHttpResult());
    }

    private static void MapInstances(IEndpointRouteBuilder app)
    {
        app.MapPost("/workflows/{name}/instances", async (
            string name,
            string? version,
            StartInstanceRequest body,
            HttpContext http,
            IInstanceLauncher launcher,
            CancellationToken cancellationToken) =>
        {
            string tenantId = http.TenantId();
            string? idempotencyKey = http.Request.Headers["Idempotency-Key"].FirstOrDefault();

            StartResult result = await launcher
                .StartAsync(name, version, body ?? new StartInstanceRequest(), tenantId, idempotencyKey, cancellationToken)
                .ConfigureAwait(false);

            switch (result.Kind)
            {
                case StartResultKind.UnknownWorkflow:
                    return Results.Problem(
                        title: "Unknown workflow",
                        detail: $"No workflow named '{name}'{(version is null ? "" : $" at version '{version}'")} is registered.",
                        statusCode: StatusCodes.Status404NotFound);

                case StartResultKind.InvalidContext:
                    return Results.ValidationProblem(result.Errors!.ToDictionary(kv => kv.Key, kv => kv.Value));

                case StartResultKind.Duplicate:
                    // Idempotent replay returns the original instance rather than creating a second.
                    return Results.Accepted(
                        $"/instances/{result.Instance!.InstanceId}", result.Instance.ToDto());

                default:
                    break;
            }

            string? prefer = http.Request.Headers["Prefer"].FirstOrDefault();
            if (PreferParser.TryGetWait(prefer, out int wait))
            {
                WorkflowInstance? completed = await launcher.WaitForTerminalAsync(
                    result.Instance!.InstanceId, TimeSpan.FromSeconds(Math.Min(wait, 60)), cancellationToken)
                    .ConfigureAwait(false);

                if (completed is not null)
                {
                    return Results.Ok(completed.ToDto());
                }
            }

            return Results.Accepted($"/instances/{result.Instance!.InstanceId}", result.Instance.ToDto());
        });

        app.MapGet("/instances/{id}", async (string id, IInstanceStore instances, CancellationToken cancellationToken) =>
        {
            WorkflowInstance? instance = await instances.GetAsync(id, cancellationToken).ConfigureAwait(false);
            return instance is null ? Results.NotFound() : Results.Ok(instance.ToDto());
        });

        // The instance's state as its own workflow defines it: lifecycle status plus, for a workflow
        // that declares an audit record, the record built so far — grouped by the sections the
        // definition declared, so the presentation follows the declaration rather than this file
        // knowing anything about a particular workflow.
        app.MapGet("/workflows/{name}/instances/{id}/state", async (
            string name,
            string id,
            [FromQuery] string? section,
            IInstanceStore instances,
            IWorkflowRegistry registry,
            IAuditRecordStore auditRecords,
            CancellationToken cancellationToken) =>
        {
            WorkflowInstance? instance = await instances.GetAsync(id, cancellationToken).ConfigureAwait(false);

            // A mismatched workflow name is a wrong URL, not a different resource — reading an
            // instance through another workflow's route would make the route meaningless.
            if (instance is null || !string.Equals(instance.WorkflowName, name, StringComparison.OrdinalIgnoreCase))
            {
                return Results.NotFound();
            }

            AuditRecordDefinition? definition =
                registry.Resolve(instance.WorkflowName, instance.WorkflowVersion)?.Definition
                    is IAuditedWorkflowDefinition audited ? audited.AuditRecord : null;

            AuditRecordDocument? document = definition is null
                ? null
                : await auditRecords.GetAsync(id, cancellationToken).ConfigureAwait(false);

            return Results.Ok(new
            {
                instance = instance.ToDto(),
                audit = BuildAuditState(definition, document, section)
            });
        })
        .WithName("GetWorkflowInstanceState")
        .WithSummary("Lifecycle status and the workflow's own audit record for one instance.");

        app.MapGet("/instances", async (
            [FromQuery] string? status,
            [FromQuery] string? workflow,
            [FromQuery] string? correlationId,
            [FromQuery] int? limit,
            [FromQuery] int? offset,
            HttpContext http,
            IInstanceStore instances,
            CancellationToken cancellationToken) =>
        {
            InstanceStatus[]? statuses = status?
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => Enum.TryParse(s, ignoreCase: true, out InstanceStatus parsed) ? parsed : (InstanceStatus?)null)
                .Where(s => s is not null)
                .Select(s => s!.Value)
                .ToArray();

            Page<WorkflowInstance> page = await instances.QueryAsync(new InstanceQuery
            {
                TenantId = http.TenantId(),
                Statuses = statuses is { Length: > 0 } ? statuses : null,
                WorkflowName = workflow,
                CorrelationId = correlationId,
                Limit = Math.Clamp(limit ?? 50, 1, 200),
                Offset = Math.Max(0, offset ?? 0)
            }, cancellationToken).ConfigureAwait(false);

            return Results.Ok(new { items = page.Items.Select(i => i.ToDto()), total = page.Total });
        });

        app.MapGet("/instances/{id}/logs", async (
            string id, [FromQuery] string? level, [FromQuery] string? executorId, [FromQuery] int? limit,
            ILogStore logs, CancellationToken cancellationToken) =>
        {
            IReadOnlyList<InstanceLogEntry> entries = await logs
                .QueryAsync(id, level, executorId, Math.Clamp(limit ?? 200, 1, 1000), cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(new { items = entries });
        });

        app.MapGet("/instances/{id}/checkpoints", (string id, ICheckpointDescriber checkpoints) =>
            Results.Ok(new
            {
                items = checkpoints.Describe(id).Select(c => new
                {
                    checkpointId = c.CheckpointId,
                    sizeBytes = c.SizeBytes,
                    committedAt = c.CommittedAt
                })
            }));
    }

    private static void MapEvents(IEndpointRouteBuilder app)
    {
        // Pull the same events the SSE stream delivers, in the same shape.
        app.MapGet("/instances/{id}/events/history", async (
            string id,
            [FromQuery] long? from,
            [FromQuery] long? to,
            [FromQuery] int? limit,
            [FromQuery] string? types,
            [FromQuery] string? format,
            HttpContext http,
            IEventStore store,
            CancellationToken cancellationToken) =>
        {
            string[]? typeFilter = types?
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            Page<EventEnvelope> page = await store.QueryAsync(
                new EventQuery(id, from ?? 0, to, Math.Clamp(limit ?? 100, 1, 1000), typeFilter), cancellationToken)
                .ConfigureAwait(false);

            if (string.Equals(format, "sse", StringComparison.OrdinalIgnoreCase))
            {
                http.Response.ContentType = "text/event-stream";
                foreach (EventEnvelope envelope in page.Items)
                {
                    await Sse.WriteEventAsync(http.Response, envelope, cancellationToken).ConfigureAwait(false);
                }
                return Results.Empty;
            }

            return Results.Ok(new { items = page.Items, total = page.Total, nextCursor = page.NextCursor });
        });

        app.MapGet("/instances/{id}/events", async (
            string id, HttpContext http, IInstanceStore instances, IEventStore store,
            IServiceProvider services, CancellationToken cancellationToken) =>
        {
            WorkflowInstance? instance = await instances.GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (instance is null)
            {
                return Results.NotFound();
            }

            long from = 0;
            string? lastEventId = http.Request.Headers["Last-Event-ID"].FirstOrDefault();
            if (long.TryParse(lastEventId, out long parsed))
            {
                from = parsed;
            }

            var bus = services.GetService<IEventBus>();
            await Sse.StreamAsync(http, id, from, instance.Status.IsTerminal(), store, bus, cancellationToken)
                .ConfigureAwait(false);

            return Results.Empty;
        });

        app.MapGet("/instances/{id}/graph", async (
            string id, IInstanceStore instances, IEventStore store, CancellationToken cancellationToken) =>
        {
            WorkflowInstance? instance = await instances.GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (instance is null)
            {
                return Results.NotFound();
            }

            var events = new List<EventEnvelope>();
            await foreach (EventEnvelope envelope in store.ReadAsync(id, 0, cancellationToken).ConfigureAwait(false))
            {
                events.Add(envelope);
            }

            NodeStates states = NodeStateProjector.Project(events);

            return Results.Ok(new
            {
                instanceId = id,
                status = instance.Status.ToString(),
                nodes = states.States.ToDictionary(kv => kv.Key, kv => kv.Value.ToString()),
                traversedEdges = states.TraversedEdges.Select(e => new { from = e.From, to = e.To }),
                currentExecutorIds = states.Executing,
                failedExecutorIds = states.Failed
            });
        });
    }

    private static void MapControl(IEndpointRouteBuilder app)
    {
        app.MapPost("/instances/{id}/cancel", async (
            string id, CancelRequestDto? body, HttpContext http, IInstanceControl control, CancellationToken cancellationToken) =>
            (await control.CancelAsync(id, body?.Reason, http.User, cancellationToken).ConfigureAwait(false)).ToHttpResult());

        app.MapPost("/instances/{id}/rerun", async (
            string id, RerunRequestDto? body, HttpContext http, IInstanceControl control, CancellationToken cancellationToken) =>
        {
            RerunMode mode = string.Equals(body?.Mode, "resume", StringComparison.OrdinalIgnoreCase)
                ? RerunMode.Resume
                : RerunMode.Restart;   // safe default: a new instance, source untouched

            ControlResult result = mode == RerunMode.Resume
                ? await control.ResumeAsync(id, body?.FromCheckpointId, body?.Reason, http.User, cancellationToken).ConfigureAwait(false)
                : await control.RestartAsync(id, body?.Context, body?.Reason, http.User, cancellationToken).ConfigureAwait(false);

            return result.ToHttpResult();
        });

        app.MapPost("/instances/{id}/retry", async (
            string id, CancelRequestDto? body, HttpContext http, IInstanceControl control, CancellationToken cancellationToken) =>
            (await control.RetryNowAsync(id, body?.Reason, http.User, cancellationToken).ConfigureAwait(false)).ToHttpResult());

        app.MapPost("/instances/{id}/suspend", async (
            string id, CancelRequestDto? body, HttpContext http, IInstanceControl control, CancellationToken cancellationToken) =>
            (await control.SuspendAsync(id, body?.Reason, http.User, cancellationToken).ConfigureAwait(false)).ToHttpResult());

        app.MapPost("/instances/{id}/resume", async (
            string id, CancelRequestDto? body, HttpContext http, IInstanceControl control, CancellationToken cancellationToken) =>
            (await control.ResumeSuspendedAsync(id, body?.Reason, http.User, cancellationToken).ConfigureAwait(false)).ToHttpResult());
    }

    private static void MapApprovals(IEndpointRouteBuilder app)
    {
        app.MapGet("/instances/{id}/approvals", async (
            string id, IApprovalStore approvals, CancellationToken cancellationToken) =>
            Results.Ok(new
            {
                items = (await approvals.ListForInstanceAsync(id, cancellationToken).ConfigureAwait(false))
                    .Select(a => a.ToDto())
            }));

        app.MapGet("/approvals", async (
            [FromQuery] string? assignee, [FromQuery] int? limit,
            HttpContext http, IApprovalStore approvals, CancellationToken cancellationToken) =>
        {
            string[]? assignees = assignee is { Length: > 0 } ? [assignee] : null;

            IReadOnlyList<ApprovalRequest> pending = await approvals
                .QueryPendingAsync(http.TenantId(), assignees, Math.Clamp(limit ?? 50, 1, 200), cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(new { items = pending.Select(a => a.ToDto()) });
        });

        app.MapGet("/approvals/{approvalId}", async (
            string approvalId, IApprovalStore approvals, CancellationToken cancellationToken) =>
        {
            ApprovalRequest? approval = await approvals.GetAsync(approvalId, cancellationToken).ConfigureAwait(false);
            return approval is null ? Results.NotFound() : Results.Ok(approval.ToDto());
        });

        app.MapPost("/approvals/{approvalId}/decision", async (
            string approvalId, DecisionRequestDto body, HttpContext http,
            IApprovalService approvals, CancellationToken cancellationToken) =>
        {
            if (!TryParseOutcome(body?.Decision, out ApprovalOutcomeKind outcome))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["decision"] = ["Must be one of: approve, reject, approve_with_modification."]
                });
            }

            DecisionResult result = await approvals.ApplyDecisionAsync(approvalId, new ApprovalDecisionInput
            {
                Decision = outcome,
                Comment = body!.Comment,
                ModifiedInput = body.ModifiedInput
            }, http.User, cancellationToken).ConfigureAwait(false);

            return result.Kind switch
            {
                DecisionResultKind.Accepted => Results.Accepted(),
                DecisionResultKind.QuorumPending => Results.Accepted(value: new { detail = result.Detail }),
                DecisionResultKind.NotFound => Results.NotFound(),
                DecisionResultKind.Forbidden => Results.Problem(
                    title: "Not authorized to decide", detail: result.Detail, statusCode: StatusCodes.Status403Forbidden),
                DecisionResultKind.InvalidModification => Results.Problem(
                    title: "Modification not permitted", detail: result.Detail, statusCode: StatusCodes.Status400BadRequest),
                _ => Results.Problem(
                    title: "Approval already decided", detail: result.Detail, statusCode: StatusCodes.Status409Conflict)
            };
        });

        // Publishes a domain message from outside the engine, so an external system can start or
        // resume a workflow without knowing which one is listening. Scoped Local by default: crossing
        // the service boundary is something a caller asks for, not something an endpoint decides.
        app.MapPost("/events", async (
            PublishEventRequestDto body,
            HttpContext http,
            IEventBroker broker,
            CancellationToken cancellationToken) =>
        {
            if (body is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["topic"] = ["A topic is required."]
                });
            }

            if (!TopicPattern.IsValidTopic(body.Topic, out string? topicError))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["topic"] = [topicError!]
                });
            }

            if (!Enum.TryParse(body.Scope ?? nameof(DeliveryScope.Local), ignoreCase: true, out DeliveryScope scope))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["scope"] = [$"Must be one of: {nameof(DeliveryScope.Local)}, {nameof(DeliveryScope.Distributed)}."]
                });
            }

            if (scope == DeliveryScope.Distributed && !broker.Capabilities.SupportsDistributed)
            {
                return Results.Problem(
                    title: "Distributed delivery is not configured",
                    detail: "The registered broker delivers within this service only. Publish as Local, " +
                            "or configure a distributed broker.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // An idempotency key becomes the message id, so a retried publish is the same message
            // rather than a second one — consumers deduplicate on it.
            string messageId = http.Request.Headers["Idempotency-Key"].FirstOrDefault() is { Length: > 0 } key
                ? key
                : IdGenerator.NewId("msg");

            var message = new BrokerMessage
            {
                MessageId = messageId,
                Topic = body.Topic!,
                PayloadJson = body.Payload?.GetRawText() ?? "{}",
                Scope = scope,
                CorrelationKey = body.CorrelationKey,
                TenantId = http.TenantId()
            };

            await broker.PublishAsync(message, cancellationToken).ConfigureAwait(false);

            return Results.Accepted(value: new { messageId = message.MessageId, topic = message.Topic });
        });

        // What is listening, and what is waiting. An instance parked on an event with no visible
        // reason is the worst version of this feature.
        app.MapGet("/subscriptions", async (
            [FromQuery] string? instanceId,
            [FromQuery] string? topic,
            [FromQuery] string? kind,
            [FromQuery] bool? pendingOnly,
            [FromQuery] int? limit,
            HttpContext http,
            IEventSubscriptionStore subscriptions,
            CancellationToken cancellationToken) =>
        {
            SubscriptionKind? parsedKind = null;
            if (kind is { Length: > 0 })
            {
                if (!Enum.TryParse(kind, ignoreCase: true, out SubscriptionKind value))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["kind"] = [$"Must be one of: {nameof(SubscriptionKind.Trigger)}, {nameof(SubscriptionKind.Wait)}."]
                    });
                }
                parsedKind = value;
            }

            if (topic is { Length: > 0 } && !TopicPattern.IsValidTopic(topic, out string? topicError))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["topic"] = [topicError!] });
            }

            IReadOnlyList<EventSubscription> items = await subscriptions.QueryAsync(new SubscriptionQuery
            {
                InstanceId = instanceId,
                Topic = topic,
                Kind = parsedKind,
                PendingOnly = pendingOnly ?? false,
                Limit = Math.Clamp(limit ?? 50, 1, 200)
            }, cancellationToken).ConfigureAwait(false);

            return Results.Ok(new { items = items.Select(s => s.ToDto()) });
        });
    }

    /// <summary>
    /// Shapes an audit record for the wire. The declared sections drive the shape — every declared
    /// section appears, empty ones included, so a caller can see what the workflow has yet to record
    /// as readily as what it has. Undeclared entries are ignored the same way the recorder refuses
    /// them. Returns null when the workflow keeps no record at all.
    /// </summary>
    private static object? BuildAuditState(
        AuditRecordDefinition? definition, AuditRecordDocument? document, string? sectionFilter)
    {
        if (definition is null) return null;

        string[]? wanted = sectionFilter?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        IEnumerable<AuditSectionDefinition> sections = definition.Sections;
        if (wanted is { Length: > 0 })
        {
            sections = sections.Where(s => wanted.Contains(s.Kind, StringComparer.OrdinalIgnoreCase));
        }

        var shaped = sections.Select(s => new
        {
            s.Kind,
            s.Description,
            s.Multiple,
            entries = (document?.Entries ?? [])
                .Where(e => string.Equals(e.SectionKind, s.Kind, StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.Sequence)
                .Select(e => new { e.Key, e.Sequence, e.RecordedUtc, payload = ParseJson(e.PayloadJson) })
        });

        return new
        {
            rootKind = definition.RootKind,
            definition.Description,
            // Null until the workflow opens the record — the shape is known from the definition
            // before any run has recorded anything against it.
            rootKey = document?.Root.RootKey,
            status = document?.Root.Status,
            openedUtc = document?.Root.OpenedUtc,
            closedUtc = document?.Root.ClosedUtc,
            attributes = document is null ? null : ParseJson(document.Root.AttributesJson),
            sections = shaped
        };
    }

    /// <summary>
    /// Payloads are stored as JSON text. Re-emitting them as elements keeps the response readable
    /// rather than nesting escaped strings inside it.
    /// </summary>
    private static JsonElement? ParseJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonDocument.Parse(json).RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static bool TryParseOutcome(string? value, out ApprovalOutcomeKind outcome)
    {
        switch (value?.ToLowerInvariant())
        {
            case "approve":
                outcome = ApprovalOutcomeKind.Approve;
                return true;
            case "reject":
                outcome = ApprovalOutcomeKind.Reject;
                return true;
            case "approve_with_modification":
            case "approvewithmodification":
                outcome = ApprovalOutcomeKind.ApproveWithModification;
                return true;
            default:
                outcome = default;
                return false;
        }
    }

    public static InstanceDto ToDto(this WorkflowInstance instance) => new(
        instance.InstanceId, instance.WorkflowName, instance.WorkflowVersion, instance.Status.ToString(),
        instance.TenantId, instance.AttemptCount, instance.TerminalReason, instance.CorrelationId,
        instance.RerunOfInstanceId, instance.CreatedAt, instance.CompletedAt);

    public static ApprovalDto ToDto(this ApprovalRequest approval) => new(
        approval.ApprovalId, approval.InstanceId, approval.ExecutorId, approval.Reason, approval.State.ToString(),
        approval.Assignees, approval.RequiredApprovers, approval.AllowModification, approval.CreatedAt,
        approval.ExpiresAt, $"/approvals/{approval.ApprovalId}/decision");

    /// <summary>
    /// The delivered payload is deliberately absent: it is domain data that has already been
    /// redacted on its way to the event stream, and repeating it unredacted here would undo that.
    /// </summary>
    public static SubscriptionDto ToDto(this EventSubscription subscription) => new(
        subscription.SubscriptionId, subscription.Kind.ToString(), subscription.TopicFilter,
        subscription.CorrelationKey, subscription.TenantId, subscription.InstanceId, subscription.ExecutorId,
        subscription.WorkflowName, subscription.IsSatisfied, subscription.ExpiresAt, subscription.CreatedAt);

    public static IResult ToHttpResult(this GateConfigResult result) => result.Kind switch
    {
        GateConfigResultKind.Ok => Results.Ok(result.Nodes),
        GateConfigResultKind.UnknownWorkflow => Results.NotFound(),
        GateConfigResultKind.UnknownExecutor => Results.Problem(
            title: "Unknown executor", detail: result.Detail, statusCode: StatusCodes.Status404NotFound),
        GateConfigResultKind.NotConfigurable => Results.Problem(
            title: "Executor is not configurable", detail: result.Detail, statusCode: StatusCodes.Status400BadRequest),
        GateConfigResultKind.Rejected => Results.Problem(
            title: "Policy rejected by the workflow definition", detail: result.Detail,
            statusCode: StatusCodes.Status409Conflict),
        _ => Results.ValidationProblem(
            result.Errors?.ToDictionary(kv => kv.Key, kv => kv.Value) ?? new Dictionary<string, string[]>())
    };

    public static IResult ToHttpResult(this ControlResult result) => result.Kind switch
    {
        ControlResultKind.Accepted => Results.Accepted(
            result.Instance is null ? null : $"/instances/{result.Instance.InstanceId}",
            result.Instance?.ToDto()),
        ControlResultKind.NotFound => Results.NotFound(),
        ControlResultKind.AlreadyTerminal => Results.Problem(
            title: "Instance already terminal", detail: result.Detail, statusCode: StatusCodes.Status409Conflict),
        ControlResultKind.NotResumable => Results.Problem(
            title: "Instance is not resumable", detail: result.Detail, statusCode: StatusCodes.Status409Conflict),
        ControlResultKind.UnknownWorkflow => Results.Problem(
            title: "Workflow unavailable", detail: result.Detail, statusCode: StatusCodes.Status409Conflict),
        _ => Results.Problem(
            title: "Invalid state for this action", detail: result.Detail, statusCode: StatusCodes.Status409Conflict)
    };

    public static string TenantId(this HttpContext http)
        => http.Request.Headers["X-Tenant-Id"].FirstOrDefault()
           ?? http.User?.FindFirst("tenant_id")?.Value
           ?? "default";
}

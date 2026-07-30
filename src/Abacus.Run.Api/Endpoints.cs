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

        return app;
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

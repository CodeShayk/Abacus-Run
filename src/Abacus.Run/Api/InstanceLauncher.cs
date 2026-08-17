using System.Text.Json;
using Abacus.Run.Abstractions;
using Abacus.Run.Core;

namespace Abacus.Run.Api;

public enum StartResultKind
{
    Accepted,
    UnknownWorkflow,
    InvalidContext,
    Duplicate
}

public sealed record StartResult(
    StartResultKind Kind,
    WorkflowInstance? Instance = null,
    IReadOnlyDictionary<string, string[]>? Errors = null)
{
    public bool IsSuccess => Kind is StartResultKind.Accepted or StartResultKind.Duplicate;
}

public sealed record StartInstanceRequest
{
    public JsonElement? Context { get; init; }
    public string? CorrelationId { get; init; }
    public int? MaxAttempts { get; init; }
}

public interface IInstanceLauncher
{
    Task<StartResult> StartAsync(
        string workflowName, string? version, StartInstanceRequest request,
        string tenantId, string? idempotencyKey, CancellationToken cancellationToken);

    Task<WorkflowInstance?> WaitForTerminalAsync(string instanceId, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// Creates instances. The record is durable before any execution is dispatched, so an accepted start
/// can never be lost to a process crash.
/// </summary>
public sealed class InstanceLauncher : IInstanceLauncher
{
    private readonly IWorkflowRegistry _registry;
    private readonly IInstanceStore _instances;
    private readonly TimeProvider _clock;

    public InstanceLauncher(IWorkflowRegistry registry, IInstanceStore instances, TimeProvider? clock = null)
    {
        _registry = registry;
        _instances = instances;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<StartResult> StartAsync(
        string workflowName, string? version, StartInstanceRequest request,
        string tenantId, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        WorkflowDescriptor? descriptor = _registry.Resolve(workflowName, version);
        if (descriptor is null)
        {
            return new StartResult(StartResultKind.UnknownWorkflow);
        }

        // Definitions are global, but an instance is somebody's work. Starting one "for all tenants"
        // would create a row no tenant-filtered query returns and every tenant could arguably see —
        // so it is refused rather than quietly owned by a tenant literally named "0".
        if (!Tenancy.CanOwnInstance(tenantId))
        {
            return new StartResult(StartResultKind.InvalidContext, Errors: new Dictionary<string, string[]>
            {
                ["tenantId"] = [$"'{Tenancy.AllTenants}' is the all-tenants scope and cannot own an instance."]
            });
        }

        ContextValidationResult validation = _registry.ValidateContext(descriptor, request.Context);
        if (!validation.IsValid)
        {
            // No instance row is created for an invalid context.
            return new StartResult(StartResultKind.InvalidContext, Errors: validation.Errors);
        }

        if (idempotencyKey is { Length: > 0 })
        {
            WorkflowInstance? existing = await _instances
                .FindByIdempotencyKeyAsync(tenantId, idempotencyKey, cancellationToken).ConfigureAwait(false);

            if (existing is not null)
            {
                return new StartResult(StartResultKind.Duplicate, existing);
            }
        }

        WorkflowInstance instance = await _instances.CreateAsync(new CreateInstanceRequest
        {
            InstanceId = IdGenerator.NewId(),
            TenantId = tenantId,
            WorkflowName = descriptor.Name,
            WorkflowVersion = descriptor.Version,
            ContextJson = request.Context?.GetRawText(),
            CorrelationId = request.CorrelationId,
            IdempotencyKey = idempotencyKey,
            MaxAttempts = request.MaxAttempts ?? 5
        }, cancellationToken).ConfigureAwait(false);

        return new StartResult(StartResultKind.Accepted, instance);
    }

    public async Task<WorkflowInstance?> WaitForTerminalAsync(
        string instanceId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = _clock.GetUtcNow() + timeout;

        while (_clock.GetUtcNow() < deadline && !cancellationToken.IsCancellationRequested)
        {
            WorkflowInstance? instance = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
            if (instance is not null && instance.Status.IsTerminal())
            {
                return instance;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), _clock, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }
}

public static class PreferParser
{
    /// <summary>Parses <c>Prefer: wait=30</c>. Values are clamped by the caller to the hard cap.</summary>
    public static bool TryGetWait(string? prefer, out int seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(prefer))
        {
            return false;
        }

        foreach (string token in prefer.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!token.StartsWith("wait=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (int.TryParse(token["wait=".Length..], out int parsed) && parsed > 0)
            {
                seconds = parsed;
                return true;
            }
        }

        return false;
    }
}

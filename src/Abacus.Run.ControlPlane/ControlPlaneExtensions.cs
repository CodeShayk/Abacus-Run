using Abacus.Run.Abstractions;
using Abacus.Run.ControlPlane.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Abacus.Run.ControlPlane;

public static class ControlPlaneExtensions
{
    /// <summary>
    /// Registers the control-plane Razor Pages and their backing services.
    /// The <see cref="WorkflowApiClient"/> uses a named <c>HttpClient</c> pointing at the
    /// local API base, so the control plane consumes only the public API surface (§12.1).
    /// </summary>
    public static IServiceCollection AddControlPlane(this IServiceCollection services, string? apiBaseUrl = null)
    {
        services.AddRazorPages();

        services.AddHttpClient<WorkflowApiClient>(client =>
        {
            client.BaseAddress = new Uri(apiBaseUrl ?? "http://localhost:5000");
        });

        return services;
    }

    /// <summary>Maps the control-plane Razor Pages at the given <paramref name="prefix"/>.</summary>
    public static IEndpointRouteBuilder MapControlPlane(this IEndpointRouteBuilder endpoints, string prefix = "/control")
    {
        endpoints.MapRazorPages();
        return endpoints;
    }
}

/// <summary>
/// Computes which control actions are available for a given instance status and user scopes.
/// Buttons are rendered disabled-with-tooltip rather than hidden — FR-11.6.
/// </summary>
public static class InstanceActions
{
    public sealed record ActionAvailability(
        bool Enabled,
        string? DisabledReason = null);

    public sealed record AvailableActions
    {
        public ActionAvailability Cancel { get; init; } = new(false, "Not available");
        public ActionAvailability RerunRestart { get; init; } = new(false, "Not available");
        public ActionAvailability RerunResume { get; init; } = new(false, "Not available");
        public ActionAvailability Retry { get; init; } = new(false, "Not available");
        public ActionAvailability Suspend { get; init; } = new(false, "Not available");
    }

    public static AvailableActions For(InstanceStatus status, IReadOnlySet<string>? userScopes = null)
    {
        bool hasControl = userScopes is null || userScopes.Contains("workflow.instances.control");

        return new AvailableActions
        {
            Cancel = status switch
            {
                InstanceStatus.Running or InstanceStatus.AwaitingInput or InstanceStatus.AwaitingApproval
                    => hasControl ? new(true) : new(false, "Insufficient permissions"),
                _ when status.IsTerminal()
                    => new(false, "Instance already terminal"),
                InstanceStatus.Pending or InstanceStatus.RetryScheduled
                    => hasControl ? new(true) : new(false, "Insufficient permissions"),
                _ => new(false, $"Cannot cancel in {status} state")
            },

            RerunRestart = status switch
            {
                _ when status.IsTerminal()
                    => hasControl ? new(true) : new(false, "Insufficient permissions"),
                _ => new(false, "Instance must be terminal to restart")
            },

            RerunResume = status switch
            {
                InstanceStatus.DeadStopped or InstanceStatus.Failed
                    => hasControl ? new(true) : new(false, "Insufficient permissions"),
                _ when status.IsTerminal()
                    => new(false, "Completed/cancelled instances cannot be resumed"),
                _ => new(false, "Instance must be DeadStopped or Failed to resume")
            },

            Retry = status switch
            {
                InstanceStatus.Failed or InstanceStatus.DeadStopped
                    => hasControl ? new(true) : new(false, "Insufficient permissions"),
                _ => new(false, "Instance must be Failed or DeadStopped to retry")
            },

            Suspend = status switch
            {
                InstanceStatus.Running
                    => hasControl ? new(true) : new(false, "Insufficient permissions"),
                _ => new(false, "Only running instances can be suspended")
            }
        };
    }
}

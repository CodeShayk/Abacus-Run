using Abacus.Run.Abstractions;
using Abacus.Run.ControlPlane.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Abacus.Run.ControlPlane.Pages;

public sealed class DashboardModel : PageModel
{
    private readonly WorkflowApiClient _api;

    public DashboardModel(WorkflowApiClient api) => _api = api;

    public DashboardMetrics? Metrics { get; private set; }
    public Page<WorkflowInstance>? RecentInstances { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        Metrics = await _api.GetDashboardMetricsAsync(ct);
        RecentInstances = await _api.ListInstancesAsync(limit: 10, ct: ct);
    }
}

using Abacus.Run.Abstractions;
using Abacus.Run.Service.ControlPlane.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Abacus.Run.Service.ControlPlane.Pages.Workflows;

public sealed class DetailModel : PageModel
{
    private readonly WorkflowApiClient _api;

    public DetailModel(WorkflowApiClient api) => _api = api;

    [FromQuery] public string Name { get; set; } = "";
    [FromQuery] public string Version { get; set; } = "";

    public Page<WorkflowInstance>? RecentInstances { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        RecentInstances = await _api.ListInstancesAsync(workflowName: Name, limit: 20, ct: ct);
    }
}

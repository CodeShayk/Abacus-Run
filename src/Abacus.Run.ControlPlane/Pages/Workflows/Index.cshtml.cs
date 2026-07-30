using Abacus.Run.ControlPlane.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Abacus.Run.ControlPlane.Pages.Workflows;

public sealed class IndexModel : PageModel
{
    private readonly WorkflowApiClient _api;

    public IndexModel(WorkflowApiClient api) => _api = api;

    public IReadOnlyList<WorkflowDescriptorDto>? Workflows { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        Workflows = await _api.ListWorkflowsAsync(ct);
    }
}

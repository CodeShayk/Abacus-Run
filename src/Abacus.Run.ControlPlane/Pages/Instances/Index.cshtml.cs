using Abacus.Run.Abstractions;
using Abacus.Run.ControlPlane.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Abacus.Run.ControlPlane.Pages.Instances;

public sealed class IndexModel : PageModel
{
    private readonly WorkflowApiClient _api;

    public IndexModel(WorkflowApiClient api) => _api = api;

    public Page<WorkflowInstance>? Instances { get; private set; }

    [FromQuery] public string? WorkflowName { get; set; }
    [FromQuery] public string? Statuses { get; set; }
    [FromQuery] public int Offset { get; set; }
    public int Limit => 50;

    public async Task OnGetAsync(CancellationToken ct)
    {
        Instances = await _api.ListInstancesAsync(
            workflowName: WorkflowName,
            statuses: Statuses,
            limit: Limit,
            offset: Offset,
            ct: ct);
    }

    public async Task<IActionResult> OnPostBulkCancelAsync(string instanceIds, int confirmCount, CancellationToken ct)
    {
        string[] ids = (instanceIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (ids.Length == 0 || ids.Length != confirmCount)
        {
            TempData["Toast"] = $"Confirm count ({confirmCount}) does not match selected count ({ids.Length}).";
            return RedirectToPage();
        }

        int succeeded = 0;
        foreach (string id in ids)
        {
            ApiResult result = await _api.CancelAsync(id, "Bulk cancel from control plane", ct);
            if (result.IsSuccess) succeeded++;
        }

        TempData["Toast"] = $"Cancelled {succeeded} of {ids.Length} instance(s).";
        return RedirectToPage();
    }
}

using Abacus.Run.Abstractions;
using Abacus.Run.Service.ControlPlane.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Abacus.Run.Service.ControlPlane.Pages.Approvals;

public sealed class IndexModel : PageModel
{
    private readonly WorkflowApiClient _api;

    public IndexModel(WorkflowApiClient api) => _api = api;

    public Page<ApprovalRequest>? Approvals { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        Approvals = await _api.ListPendingApprovalsAsync(ct: ct);
    }

    public async Task<IActionResult> OnPostApproveAsync(string approvalId, string? comment, CancellationToken ct)
    {
        ApiResult result = await _api.ApproveAsync(approvalId, comment, ct);
        TempData["Toast"] = result.IsSuccess ? "Approval submitted." : result.Error;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRejectAsync(string approvalId, string? comment, CancellationToken ct)
    {
        ApiResult result = await _api.RejectAsync(approvalId, comment, ct);
        TempData["Toast"] = result.IsSuccess ? "Rejection submitted." : result.Error;
        return RedirectToPage();
    }
}

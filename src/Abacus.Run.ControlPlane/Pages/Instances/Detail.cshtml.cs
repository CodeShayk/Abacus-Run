using Abacus.Run.Abstractions;
using Abacus.Run.ControlPlane.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Abacus.Run.ControlPlane.Pages.Instances;

public sealed class DetailModel : PageModel
{
    private readonly WorkflowApiClient _api;

    public DetailModel(WorkflowApiClient api) => _api = api;

    public WorkflowInstance? Instance { get; private set; }
    public GraphResponse? Graph { get; private set; }
    public Page<EventEnvelope>? Events { get; private set; }
    public InstanceActions.AvailableActions Actions { get; private set; } = new();

    [BindProperty] public string? Reason { get; set; }

    public async Task OnGetAsync(string id, CancellationToken ct)
    {
        Instance = await _api.GetInstanceAsync(id, ct);
        if (Instance is null) return;

        Actions = InstanceActions.For(Instance.Status);
        Graph = await _api.GetGraphAsync(id, ct);
        Events = await _api.GetEventsAsync(id, limit: 200, ct: ct);
    }

    public async Task<IActionResult> OnPostCancelAsync(string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Reason))
        {
            ModelState.AddModelError(nameof(Reason), "Reason required.");
            await OnGetAsync(id, ct);
            return Page();
        }

        ApiResult result = await _api.CancelAsync(id, Reason, ct);
        TempData["Toast"] = result.IsSuccess ? "Cancellation requested." : result.Error;
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRerunRestartAsync(string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Reason))
        {
            ModelState.AddModelError(nameof(Reason), "Reason required.");
            await OnGetAsync(id, ct);
            return Page();
        }

        ApiResult result = await _api.RerunRestartAsync(id, Reason, ct);
        TempData["Toast"] = result.IsSuccess ? "Restart requested." : result.Error;
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRerunResumeAsync(string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Reason))
        {
            ModelState.AddModelError(nameof(Reason), "Reason required.");
            await OnGetAsync(id, ct);
            return Page();
        }

        ApiResult result = await _api.RerunResumeAsync(id, null, Reason, ct);
        TempData["Toast"] = result.IsSuccess ? "Resume requested." : result.Error;
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRetryAsync(string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Reason))
        {
            ModelState.AddModelError(nameof(Reason), "Reason required.");
            await OnGetAsync(id, ct);
            return Page();
        }

        ApiResult result = await _api.RetryAsync(id, Reason, ct);
        TempData["Toast"] = result.IsSuccess ? "Retry requested." : result.Error;
        return RedirectToPage(new { id });
    }
}

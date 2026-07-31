using System.Net.Http.Json;
using System.Text.Json;
using Abacus.Run.Abstractions;
using Abacus.Run.Core;

namespace Abacus.Run.Service.ControlPlane.Services;

/// <summary>
/// Typed HTTP client that proxies the control plane UI to the public workflow API.
/// This is the <em>only</em> way pages reach data — the control plane holds no <c>DbContext</c>
/// reference and does not reference <c>Abacus.Run.Persistence</c> at all (§12.1, §15.6).
/// </summary>
public sealed class WorkflowApiClient
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public WorkflowApiClient(HttpClient http)
    {
        _http = http;
    }

    // ─── Instances ───────────────────────────────────────────────────────

    public async Task<WorkflowInstance?> GetInstanceAsync(string instanceId, CancellationToken ct = default)
    {
        HttpResponseMessage response = await _http.GetAsync($"/instances/{instanceId}", ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        InstanceDto? dto = await response.Content.ReadFromJsonAsync<InstanceDto>(Json, ct).ConfigureAwait(false);
        return dto?.ToModel();
    }

    public async Task<Page<WorkflowInstance>> ListInstancesAsync(
        string? tenantId = null,
        string? workflowName = null,
        string? statuses = null,
        int limit = 50,
        int offset = 0,
        CancellationToken ct = default)
    {
        string url = $"/instances?limit={limit}&offset={offset}";
        if (!string.IsNullOrEmpty(tenantId)) url += $"&tenantId={Uri.EscapeDataString(tenantId)}";
        if (!string.IsNullOrEmpty(workflowName)) url += $"&workflowName={Uri.EscapeDataString(workflowName)}";
        if (!string.IsNullOrEmpty(statuses)) url += $"&statuses={Uri.EscapeDataString(statuses)}";

        HttpResponseMessage response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var pageDto = await response.Content.ReadFromJsonAsync<PageDto<InstanceDto>>(Json, ct).ConfigureAwait(false);
        var items = pageDto?.Items?.Select(i => i.ToModel()).ToList() ?? [];
        return new Page<WorkflowInstance>(items, pageDto?.Total ?? 0);
    }

    // ─── Events ──────────────────────────────────────────────────────────

    public async Task<Page<EventEnvelope>> GetEventsAsync(
        string instanceId,
        long from = 0,
        int limit = 100,
        string? types = null,
        CancellationToken ct = default)
    {
        string url = $"/instances/{instanceId}/events/history?from={from}&limit={limit}";
        if (!string.IsNullOrEmpty(types)) url += $"&types={Uri.EscapeDataString(types)}";

        HttpResponseMessage response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Page<EventEnvelope>>(Json, ct).ConfigureAwait(false))!;
    }

    // ─── Graph ───────────────────────────────────────────────────────────

    public async Task<GraphResponse?> GetGraphAsync(string instanceId, CancellationToken ct = default)
    {
        HttpResponseMessage response = await _http.GetAsync($"/instances/{instanceId}/graph", ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<GraphResponse>(Json, ct).ConfigureAwait(false)
            : null;
    }

    // ─── Controls ────────────────────────────────────────────────────────

    public async Task<ApiResult> CancelAsync(string instanceId, string reason, CancellationToken ct = default)
    {
        HttpResponseMessage response = await _http.PostAsJsonAsync(
            $"/instances/{instanceId}/cancel",
            new { reason }, ct).ConfigureAwait(false);
        return new ApiResult(response.IsSuccessStatusCode, await ReadErrorAsync(response, ct).ConfigureAwait(false));
    }

    public async Task<ApiResult> RerunRestartAsync(string instanceId, string reason, CancellationToken ct = default)
    {
        HttpResponseMessage response = await _http.PostAsJsonAsync(
            $"/instances/{instanceId}/rerun",
            new { mode = "Restart", reason }, ct).ConfigureAwait(false);
        return new ApiResult(response.IsSuccessStatusCode, await ReadErrorAsync(response, ct).ConfigureAwait(false));
    }

    public async Task<ApiResult> RerunResumeAsync(string instanceId, string? fromCheckpointId, string reason, CancellationToken ct = default)
    {
        HttpResponseMessage response = await _http.PostAsJsonAsync(
            $"/instances/{instanceId}/rerun",
            new { mode = "Resume", fromCheckpointId, reason }, ct).ConfigureAwait(false);
        return new ApiResult(response.IsSuccessStatusCode, await ReadErrorAsync(response, ct).ConfigureAwait(false));
    }

    public async Task<ApiResult> RetryAsync(string instanceId, string reason, CancellationToken ct = default)
    {
        HttpResponseMessage response = await _http.PostAsJsonAsync(
            $"/instances/{instanceId}/retry",
            new { reason }, ct).ConfigureAwait(false);
        return new ApiResult(response.IsSuccessStatusCode, await ReadErrorAsync(response, ct).ConfigureAwait(false));
    }

    // ─── Approvals ───────────────────────────────────────────────────────

    public async Task<Page<ApprovalRequest>> ListPendingApprovalsAsync(
        string? tenantId = null, int limit = 50, CancellationToken ct = default)
    {
        string url = $"/approvals?limit={limit}";
        if (!string.IsNullOrEmpty(tenantId)) url += $"&tenantId={Uri.EscapeDataString(tenantId)}";

        HttpResponseMessage response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Page<ApprovalRequest>>(Json, ct).ConfigureAwait(false))!;
    }

    public async Task<ApiResult> ApproveAsync(string approvalId, string? comment, CancellationToken ct = default)
    {
        HttpResponseMessage response = await _http.PostAsJsonAsync(
            $"/approvals/{approvalId}/decision",
            new { decision = "approve", comment }, ct).ConfigureAwait(false);
        return new ApiResult(response.IsSuccessStatusCode, await ReadErrorAsync(response, ct).ConfigureAwait(false));
    }

    public async Task<ApiResult> RejectAsync(string approvalId, string? comment, CancellationToken ct = default)
    {
        HttpResponseMessage response = await _http.PostAsJsonAsync(
            $"/approvals/{approvalId}/decision",
            new { decision = "reject", comment }, ct).ConfigureAwait(false);
        return new ApiResult(response.IsSuccessStatusCode, await ReadErrorAsync(response, ct).ConfigureAwait(false));
    }

    // ─── Workflows ───────────────────────────────────────────────────────

    public async Task<IReadOnlyList<WorkflowDescriptorDto>> ListWorkflowsAsync(CancellationToken ct = default)
    {
        HttpResponseMessage response = await _http.GetAsync("/workflows", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<IReadOnlyList<WorkflowDescriptorDto>>(Json, ct).ConfigureAwait(false))!;
    }

    // ─── Dashboard ───────────────────────────────────────────────────────

    public async Task<DashboardMetrics?> GetDashboardMetricsAsync(CancellationToken ct = default)
    {
        HttpResponseMessage response = await _http.GetAsync("/diagnostics/metrics", ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<DashboardMetrics>(Json, ct).ConfigureAwait(false)
            : null;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return null;
        try
        {
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("detail", out JsonElement detail)
                ? detail.GetString()
                : body;
        }
        catch
        {
            return $"HTTP {(int)response.StatusCode}";
        }
    }
}

public sealed record PageDto<T>(IReadOnlyList<T> Items, int Total);

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
    DateTimeOffset? CompletedAt)
{
    public WorkflowInstance ToModel() => new()
    {
        InstanceId = InstanceId,
        TenantId = TenantId,
        WorkflowName = WorkflowName,
        WorkflowVersion = WorkflowVersion,
        Status = Enum.TryParse(Status, ignoreCase: true, out InstanceStatus s) ? s : InstanceStatus.Pending,
        AttemptCount = AttemptCount,
        TerminalReason = TerminalReason,
        CorrelationId = CorrelationId,
        RerunOfInstanceId = RerunOfInstanceId,
        CreatedAt = CreatedAt,
        CompletedAt = CompletedAt,
        UpdatedAt = CreatedAt
    };
}

public sealed record ApiResult(bool IsSuccess, string? Error);

public sealed record GraphResponse(
    string Mermaid,
    Dictionary<string, string> Nodes,
    HashSet<(string From, string To)>? TraversedEdges,
    IReadOnlyList<string>? CurrentExecutorIds);

public sealed record WorkflowDescriptorDto(
    string Name,
    string Version,
    string ContextTypeName,
    string ResultTypeName);

public sealed record DashboardMetrics
{
    public int ActiveInstances { get; init; }
    public int CompletedInstances { get; init; }
    public int FailedInstances { get; init; }
    public int AwaitingApproval { get; init; }
    public int PendingInstances { get; init; }
    public double ThroughputPerMinute { get; init; }
    public double ErrorRate { get; init; }
}

namespace Abacus.Run.Core;

public enum CheckpointCadence
{
    None,
    SuperStep,
    Manual
}

public enum JitterMode
{
    None,
    Full
}

public sealed class WorkflowHostOptions
{
    public const string SectionName = "WorkflowHost";

    public string? ReplicaId { get; set; }
    public int MaxConcurrentInstances { get; set; } = 100;
    public int ClaimBatchSize { get; set; } = 10;
    public Dictionary<string, int> PerWorkflowConcurrency { get; set; } = [];

    public LeaseOptions Lease { get; set; } = new();
    public DrainOptions Drain { get; set; } = new();
    public CheckpointOptions Checkpoint { get; set; } = new();
    public RetryOptions Retry { get; set; } = new();
    public ApprovalOptions Approvals { get; set; } = new();
    public EventOptions Events { get; set; } = new();
    public SseOptions Sse { get; set; } = new();
    public RetentionOptions Retention { get; set; } = new();
    public LoggingOptions Logging { get; set; } = new();
    public DriftOptions Drift { get; set; } = new();
    public EgressOptions Egress { get; set; } = new();
}

public sealed class LeaseOptions
{
    public int DurationSeconds { get; set; } = 60;
    public int RenewalSeconds { get; set; } = 20;

    public TimeSpan Duration => TimeSpan.FromSeconds(DurationSeconds);
    public TimeSpan Renewal => TimeSpan.FromSeconds(RenewalSeconds);
}

public sealed class DrainOptions
{
    public int GraceSeconds { get; set; } = 45;
    public TimeSpan Grace => TimeSpan.FromSeconds(GraceSeconds);
}

public sealed class CheckpointOptions
{
    public CheckpointCadence Cadence { get; set; } = CheckpointCadence.SuperStep;
    public int InlineThresholdBytes { get; set; } = 256 * 1024;
}

public sealed class RetryOptions
{
    public int MaxAttempts { get; set; } = 5;
    public double BackoffBaseSeconds { get; set; } = 2;
    public double BackoffCapSeconds { get; set; } = 300;
    public JitterMode Jitter { get; set; } = JitterMode.Full;
    public int MaxLifetimeHours { get; set; } = 24;

    public TimeSpan BackoffBase => TimeSpan.FromSeconds(BackoffBaseSeconds);
    public TimeSpan BackoffCap => TimeSpan.FromSeconds(BackoffCapSeconds);
    public TimeSpan MaxLifetime => TimeSpan.FromHours(MaxLifetimeHours);
}

public sealed class ApprovalOptions
{
    public int DefaultExpiryHours { get; set; } = 24;
    public int SweepIntervalSeconds { get; set; } = 30;
    public int PolicyCacheSeconds { get; set; } = 30;
}

public sealed class EventOptions
{
    public int BatchSize { get; set; } = 200;
    public int ChannelCapacity { get; set; } = 10_000;
    public int RetentionDays { get; set; } = 90;
}

public sealed class SseOptions
{
    public int HeartbeatSeconds { get; set; } = 15;
    public int MaxSubscribersPerInstance { get; set; } = 50;
}

public sealed class RetentionOptions
{
    public int InstanceDays { get; set; } = 90;
    public int CheckpointDays { get; set; } = 7;
    public int AuditDays { get; set; } = 365;
}

public sealed class LoggingOptions
{
    public double BodySampleRate { get; set; } = 0.10;
    public int MaxBodyBytes { get; set; } = 32 * 1024;
    public HashSet<string> BodyFieldAllowList { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> HeaderDenyList { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Cookie", "Set-Cookie", "x-api-key", "Proxy-Authorization"
    };
}

public sealed class DriftOptions
{
    public int BaselineWindowDays { get; set; } = 7;
    public int MinSamples { get; set; } = 200;
    public double EmbeddingSampleRate { get; set; } = 0.05;
    public int SustainedWindowMinutes { get; set; } = 15;
    public double SigmaThreshold { get; set; } = 3.0;
}

public sealed class EgressOptions
{
    public List<string> AllowedHosts { get; set; } = [];
    public bool Enforce { get; set; } = true;
}

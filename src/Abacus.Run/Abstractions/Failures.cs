namespace Abacus.Run.Abstractions;

/// <summary>How the host should react to a failure. Decided by the concrete workflow.</summary>
public enum FailureDisposition
{
    Retry,
    DeadStop,
    Escalate
}

/// <summary>Everything a classifier needs to decide a disposition.</summary>
public sealed record WorkflowFailure(
    string ExecutorId,
    Exception Exception,
    int AttemptCount,
    int Superstep,
    IReadOnlyDictionary<string, object?> ExecutorMetadata)
{
    public static WorkflowFailure Create(string executorId, Exception exception, int attempt = 1, int superstep = 0)
        => new(executorId, exception, attempt, superstep, new Dictionary<string, object?>());
}

public interface IFailureClassifier
{
    FailureDisposition Classify(WorkflowFailure failure);
}

/// <summary>Thrown by an executor to declare a failure unambiguously terminal, bypassing classification.</summary>
public sealed class WorkflowDeadStopException : Exception
{
    public string? Code { get; }

    public WorkflowDeadStopException(string message, string? code = null) : base(message) => Code = code;

    public WorkflowDeadStopException(string message, Exception inner, string? code = null)
        : base(message, inner) => Code = code;
}

/// <summary>Raised when an approval gate decision was "reject". Routed through the workflow classifier.</summary>
public sealed class ApprovalRejectedException : Exception
{
    public string ExecutorId { get; }
    public string ApprovalId { get; }
    public string? Comment { get; }

    public ApprovalRejectedException(string executorId, string approvalId, string? comment = null)
        : base($"Approval '{approvalId}' for executor '{executorId}' was rejected.{(comment is null ? "" : " " + comment)}")
    {
        ExecutorId = executorId;
        ApprovalId = approvalId;
        Comment = comment;
    }
}

/// <summary>A non-success HTTP outcome from <c>ApiCallExecutor</c>, carrying what a classifier needs.</summary>
public sealed class ApiCallFailureException : Exception
{
    public int StatusCode { get; }
    public string? BodyExcerpt { get; }
    public TimeSpan? RetryAfter { get; }

    public ApiCallFailureException(int statusCode, string? bodyExcerpt = null, TimeSpan? retryAfter = null)
        : base($"API call failed with status {statusCode}.")
    {
        StatusCode = statusCode;
        BodyExcerpt = bodyExcerpt;
        RetryAfter = retryAfter;
    }
}

public sealed class LlmRateLimitException : Exception
{
    public TimeSpan? RetryAfter { get; }
    public LlmRateLimitException(string message = "LLM rate limit exceeded.", TimeSpan? retryAfter = null)
        : base(message) => RetryAfter = retryAfter;
}

public sealed class LlmOverloadedException : Exception
{
    public LlmOverloadedException(string message = "LLM provider overloaded.") : base(message) { }
}

/// <summary>The model could not produce schema-valid output within the allowed re-parse attempts.</summary>
public sealed class StructuredOutputException : Exception
{
    public Type ExpectedType { get; }
    public int Attempts { get; }

    public StructuredOutputException(Type expectedType, int attempts, string? detail = null)
        : base($"Model failed to produce valid '{expectedType.Name}' after {attempts} attempt(s). {detail}")
    {
        ExpectedType = expectedType;
        Attempts = attempts;
    }
}

/// <summary>Input failed schema/precondition validation.</summary>
public sealed class WorkflowValidationException : Exception
{
    public IReadOnlyDictionary<string, string[]> Errors { get; }

    public WorkflowValidationException(IReadOnlyDictionary<string, string[]> errors)
        : base("Validation failed.") => Errors = errors;

    public WorkflowValidationException(string field, string error)
        : this(new Dictionary<string, string[]> { [field] = [error] }) { }
}

/// <summary>
/// Default disposition rules (PRD FR-3.4). Transient transport faults retry; anything that would
/// fail identically on a second attempt dead-stops.
/// </summary>
public sealed class DefaultFailureClassifier : IFailureClassifier
{
    public static DefaultFailureClassifier Instance { get; } = new();

    public FailureDisposition Classify(WorkflowFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return failure.Exception switch
        {
            WorkflowDeadStopException => FailureDisposition.DeadStop,
            ApprovalRejectedException => FailureDisposition.DeadStop,
            WorkflowValidationException => FailureDisposition.DeadStop,
            StructuredOutputException => FailureDisposition.DeadStop,
            System.Text.Json.JsonException => FailureDisposition.DeadStop,

            LlmRateLimitException => FailureDisposition.Retry,
            LlmOverloadedException => FailureDisposition.Retry,

            // Order matters: the retryable status codes must be tested before the 4xx catch-all.
            ApiCallFailureException { StatusCode: 408 or 429 } => FailureDisposition.Retry,
            ApiCallFailureException { StatusCode: >= 500 } => FailureDisposition.Retry,
            ApiCallFailureException { StatusCode: >= 400 } => FailureDisposition.DeadStop,
            ApiCallFailureException => FailureDisposition.Retry,

            TimeoutException => FailureDisposition.Retry,
            TaskCanceledException => FailureDisposition.Retry,
            OperationCanceledException => FailureDisposition.Retry,
            HttpRequestException => FailureDisposition.Retry,

            _ => FailureDisposition.Retry
        };
    }
}

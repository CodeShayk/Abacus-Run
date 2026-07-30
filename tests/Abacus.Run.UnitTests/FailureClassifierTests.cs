using System.Text.Json;
using Abacus.Run.Abstractions;
using FluentAssertions;
using Xunit;

namespace Abacus.Run.UnitTests;

public class FailureClassifierTests
{
    private static FailureDisposition Classify(Exception ex)
        => DefaultFailureClassifier.Instance.Classify(WorkflowFailure.Create("exec", ex));

    public static TheoryData<Exception, FailureDisposition> Cases => new()
    {
        { new WorkflowDeadStopException("fraud detected"), FailureDisposition.DeadStop },
        { new ApprovalRejectedException("pay", "apr_1"), FailureDisposition.DeadStop },
        { new WorkflowValidationException("amount", "must be positive"), FailureDisposition.DeadStop },
        { new StructuredOutputException(typeof(object), 2), FailureDisposition.DeadStop },
        { new JsonException("bad json"), FailureDisposition.DeadStop },

        { new LlmRateLimitException(), FailureDisposition.Retry },
        { new LlmOverloadedException(), FailureDisposition.Retry },
        { new TimeoutException(), FailureDisposition.Retry },
        { new HttpRequestException("socket reset"), FailureDisposition.Retry },
        { new InvalidOperationException("unknown"), FailureDisposition.Retry },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Classifies_known_exceptions(Exception exception, FailureDisposition expected)
        => Classify(exception).Should().Be(expected);

    [Theory]
    [InlineData(408, FailureDisposition.Retry)]
    [InlineData(429, FailureDisposition.Retry)]
    [InlineData(500, FailureDisposition.Retry)]
    [InlineData(502, FailureDisposition.Retry)]
    [InlineData(503, FailureDisposition.Retry)]
    [InlineData(400, FailureDisposition.DeadStop)]
    [InlineData(401, FailureDisposition.DeadStop)]
    [InlineData(403, FailureDisposition.DeadStop)]
    [InlineData(404, FailureDisposition.DeadStop)]
    [InlineData(409, FailureDisposition.DeadStop)]
    [InlineData(422, FailureDisposition.DeadStop)]
    public void Http_status_drives_disposition(int status, FailureDisposition expected)
        => Classify(new ApiCallFailureException(status)).Should().Be(expected);

    [Fact]
    public void Retryable_statuses_are_tested_before_the_4xx_catch_all()
    {
        // 429 is a 4xx but must retry: ordering inside the switch is the thing under test.
        Classify(new ApiCallFailureException(429)).Should().Be(FailureDisposition.Retry);
        Classify(new ApiCallFailureException(408)).Should().Be(FailureDisposition.Retry);
    }

    [Fact]
    public void DeadStop_exception_carries_its_code()
    {
        var ex = new WorkflowDeadStopException("negative amount", "NEG_AMOUNT");
        ex.Code.Should().Be("NEG_AMOUNT");
        ex.Message.Should().Contain("negative amount");
    }

    [Fact]
    public void ApiCallFailure_carries_status_body_and_retry_after()
    {
        var ex = new ApiCallFailureException(503, "upstream down", TimeSpan.FromSeconds(30));
        ex.StatusCode.Should().Be(503);
        ex.BodyExcerpt.Should().Be("upstream down");
        ex.RetryAfter.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void ApprovalRejected_message_includes_comment_when_present()
    {
        var ex = new ApprovalRejectedException("pay", "apr_9", "duplicate invoice");
        ex.Message.Should().Contain("apr_9").And.Contain("duplicate invoice");
        ex.ExecutorId.Should().Be("pay");
    }

    [Fact]
    public void Validation_exception_exposes_field_errors()
    {
        var ex = new WorkflowValidationException("amount", "required");
        ex.Errors.Should().ContainKey("amount");
        ex.Errors["amount"].Should().ContainSingle().Which.Should().Be("required");
    }

    [Fact]
    public void Classify_rejects_null_failure()
    {
        Action act = () => DefaultFailureClassifier.Instance.Classify(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WorkflowFailure_create_defaults_attempt_and_superstep()
    {
        WorkflowFailure failure = WorkflowFailure.Create("e1", new TimeoutException());
        failure.AttemptCount.Should().Be(1);
        failure.Superstep.Should().Be(0);
        failure.ExecutorMetadata.Should().BeEmpty();
    }
}

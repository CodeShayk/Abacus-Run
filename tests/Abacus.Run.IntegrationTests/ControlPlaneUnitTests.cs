using System.Net;
using Abacus.Run.Abstractions;
using Abacus.Run.Service.ControlPlane;
using Abacus.Run.Service.ControlPlane.Services;
using FluentAssertions;
using Xunit;

namespace Abacus.Run.IntegrationTests;

/// <summary>
/// Plain unit tests, hosted here because the control plane ships in Abacus.Run.Service and the unit
/// test project deliberately references only the framework library.
/// </summary>
public sealed class ControlPlaneTests
{
    [Theory]
    [InlineData(InstanceStatus.Running, true)]
    [InlineData(InstanceStatus.AwaitingInput, true)]
    [InlineData(InstanceStatus.AwaitingApproval, true)]
    [InlineData(InstanceStatus.Pending, true)]
    [InlineData(InstanceStatus.Completed, false)]
    [InlineData(InstanceStatus.Failed, false)]
    [InlineData(InstanceStatus.Cancelled, false)]
    [InlineData(InstanceStatus.DeadStopped, false)]
    public void InstanceActions_CancelAvailability_MatchesStatusRules(InstanceStatus status, bool expectedCancel)
    {
        var actions = InstanceActions.For(status);

        actions.Cancel.Enabled.Should().Be(expectedCancel);
        if (!expectedCancel)
        {
            actions.Cancel.DisabledReason.Should().NotBeNullOrEmpty();
        }
    }

    [Theory]
    [InlineData(InstanceStatus.Completed, true)]
    [InlineData(InstanceStatus.Failed, true)]
    [InlineData(InstanceStatus.Cancelled, true)]
    [InlineData(InstanceStatus.DeadStopped, true)]
    [InlineData(InstanceStatus.Running, false)]
    [InlineData(InstanceStatus.Pending, false)]
    public void InstanceActions_RerunRestart_MatchesStatusRules(InstanceStatus status, bool expectedRestart)
    {
        var actions = InstanceActions.For(status);

        actions.RerunRestart.Enabled.Should().Be(expectedRestart);
        if (!expectedRestart)
        {
            actions.RerunRestart.DisabledReason.Should().NotBeNullOrEmpty();
        }
    }

    [Theory]
    [InlineData(InstanceStatus.DeadStopped, true)]
    [InlineData(InstanceStatus.Failed, true)]
    [InlineData(InstanceStatus.Completed, false)]
    [InlineData(InstanceStatus.Running, false)]
    public void InstanceActions_RerunResume_MatchesStatusRules(InstanceStatus status, bool expectedResume)
    {
        var actions = InstanceActions.For(status);

        actions.RerunResume.Enabled.Should().Be(expectedResume);
        if (!expectedResume)
        {
            actions.RerunResume.DisabledReason.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public void InstanceActions_PermissionScope_RestrictsActionsWhenScopeMissing()
    {
        var noScope = new HashSet<string>();
        var actions = InstanceActions.For(InstanceStatus.Running, noScope);

        actions.Cancel.Enabled.Should().BeFalse();
        actions.Cancel.DisabledReason.Should().Be("Insufficient permissions");
    }

    [Fact]
    public async Task WorkflowApiClient_GetInstance_ParsesResponseCorrectly()
    {
        var handler = new MockHttpMessageHandler(@"{
            ""instanceId"": ""inst-123"",
            ""tenantId"": ""tenant-1"",
            ""workflowName"": ""my-wf"",
            ""workflowVersion"": ""1.0.0"",
            ""status"": ""Running"",
            ""createdAt"": ""2026-07-30T10:00:00Z"",
            ""updatedAt"": ""2026-07-30T10:00:00Z""
        }");

        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var client = new WorkflowApiClient(http);

        WorkflowInstance? instance = await client.GetInstanceAsync("inst-123");

        instance.Should().NotBeNull();
        instance!.InstanceId.Should().Be("inst-123");
        instance.Status.Should().Be(InstanceStatus.Running);
    }

    [Fact]
    public async Task WorkflowApiClient_Cancel_SendsPostRequest()
    {
        var handler = new MockHttpMessageHandler("{}", HttpStatusCode.OK);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var client = new WorkflowApiClient(http);

        ApiResult result = await client.CancelAsync("inst-123", "Operator request");

        result.IsSuccess.Should().BeTrue();
        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Method.Method.Should().Be("POST");
        handler.LastRequest.RequestUri!.AbsolutePath.Should().Be("/instances/inst-123/cancel");
    }

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _responseJson;
        private readonly HttpStatusCode _statusCode;

        public MockHttpMessageHandler(string responseJson, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _responseJson = responseJson;
            _statusCode = statusCode;
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseJson, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}

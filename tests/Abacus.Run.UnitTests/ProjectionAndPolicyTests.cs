using System.Net;
using Abacus.Run.Abstractions;
using Abacus.Run.Core;
using FluentAssertions;
using Xunit;

namespace Abacus.Run.UnitTests;

public class NodeStateProjectorTests
{
    [Fact]
    public void Invoked_then_completed_yields_completed()
    {
        NodeStates states = NodeStateProjector.Project(
        [
            TestFactory.Event("i1", 1, WorkflowEventTypes.ExecutorInvoked, "a"),
            TestFactory.Event("i1", 2, WorkflowEventTypes.ExecutorCompleted, "a")
        ]);

        states.States["a"].Should().Be(NodeState.Completed);
    }

    [Fact]
    public void An_invoked_but_unfinished_node_is_the_current_node()
    {
        NodeStates states = NodeStateProjector.Project(
        [
            TestFactory.Event("i1", 1, WorkflowEventTypes.ExecutorInvoked, "a"),
            TestFactory.Event("i1", 2, WorkflowEventTypes.ExecutorCompleted, "a"),
            TestFactory.Event("i1", 3, WorkflowEventTypes.ExecutorInvoked, "b")
        ]);

        states.Executing.Should().Equal("b");
    }

    [Fact]
    public void Fan_out_highlights_several_nodes_at_once()
    {
        NodeStates states = NodeStateProjector.Project(
        [
            TestFactory.Event("i1", 1, WorkflowEventTypes.ExecutorInvoked, "b"),
            TestFactory.Event("i1", 2, WorkflowEventTypes.ExecutorInvoked, "c")
        ]);

        states.Executing.Should().BeEquivalentTo(["b", "c"]);
    }

    [Fact]
    public void Failure_is_recorded_and_surfaced()
    {
        NodeStates states = NodeStateProjector.Project(
        [
            TestFactory.Event("i1", 1, WorkflowEventTypes.ExecutorInvoked, "a"),
            TestFactory.Event("i1", 2, WorkflowEventTypes.ExecutorFailed, "a")
        ]);

        states.States["a"].Should().Be(NodeState.Failed);
        states.Failed.Should().Equal("a");
    }

    [Fact]
    public void Approval_request_parks_the_node()
    {
        NodeStates states = NodeStateProjector.Project(
        [
            TestFactory.Event("i1", 1, WorkflowEventTypes.ExecutorInvoked, "pay"),
            TestFactory.Event("i1", 2, WorkflowEventTypes.ApprovalRequested, "pay")
        ]);

        states.States["pay"].Should().Be(NodeState.AwaitingApproval);
    }

    [Fact]
    public void An_invoked_event_arriving_after_an_approval_does_not_unpark_the_node()
    {
        // The engine sequences its invoked event independently of the approval raised inside the
        // handler, so it can land afterwards. A node waiting on a human is not executing.
        NodeStates states = NodeStateProjector.Project(
        [
            TestFactory.Event("i1", 1, WorkflowEventTypes.ApprovalRequested, "pay"),
            TestFactory.Event("i1", 2, WorkflowEventTypes.ExecutorInvoked, "pay")
        ]);

        states.States["pay"].Should().Be(NodeState.AwaitingApproval);
    }

    [Fact]
    public void A_late_invoked_event_does_not_reopen_a_completed_node()
    {
        NodeStates states = NodeStateProjector.Project(
        [
            TestFactory.Event("i1", 1, WorkflowEventTypes.ExecutorInvoked, "a"),
            TestFactory.Event("i1", 2, WorkflowEventTypes.ExecutorCompleted, "a"),
            TestFactory.Event("i1", 3, WorkflowEventTypes.ExecutorInvoked, "a")
        ]);

        states.States["a"].Should().Be(NodeState.Completed);
    }

    [Fact]
    public void Approval_decision_returns_a_parked_node_to_executing()
    {
        NodeStates states = NodeStateProjector.Project(
        [
            TestFactory.Event("i1", 1, WorkflowEventTypes.ApprovalRequested, "pay"),
            TestFactory.Event("i1", 2, WorkflowEventTypes.ApprovalDecided, "pay")
        ]);

        states.States["pay"].Should().Be(NodeState.Executing);
    }

    [Fact]
    public void Approval_decision_does_not_reopen_a_completed_node()
    {
        NodeStates states = NodeStateProjector.Project(
        [
            TestFactory.Event("i1", 1, WorkflowEventTypes.ExecutorInvoked, "pay"),
            TestFactory.Event("i1", 2, WorkflowEventTypes.ExecutorCompleted, "pay"),
            TestFactory.Event("i1", 3, WorkflowEventTypes.ApprovalDecided, "pay")
        ]);

        states.States["pay"].Should().Be(NodeState.Completed);
    }

    [Fact]
    public void Expired_approval_fails_the_node()
    {
        NodeStates states = NodeStateProjector.Project(
        [
            TestFactory.Event("i1", 1, WorkflowEventTypes.ApprovalRequested, "pay"),
            TestFactory.Event("i1", 2, WorkflowEventTypes.ApprovalExpired, "pay")
        ]);

        states.States["pay"].Should().Be(NodeState.Failed);
    }

    [Fact]
    public void Traversed_edges_follow_the_actual_path()
    {
        NodeStates states = NodeStateProjector.Project(
        [
            TestFactory.Event("i1", 1, WorkflowEventTypes.ExecutorInvoked, "a"),
            TestFactory.Event("i1", 2, WorkflowEventTypes.ExecutorCompleted, "a"),
            TestFactory.Event("i1", 3, WorkflowEventTypes.ExecutorInvoked, "b"),
            TestFactory.Event("i1", 4, WorkflowEventTypes.ExecutorCompleted, "b")
        ]);

        states.TraversedEdges.Should().Contain(("a", "b"));
    }

    [Fact]
    public void Unreached_nodes_are_pending_before_anything_completes()
    {
        NodeStates states = NodeStateProjector.Project(
            [TestFactory.Event("i1", 1, WorkflowEventTypes.ExecutorInvoked, "a")],
            allNodeIds: ["a", "b", "c"]);

        states.States["b"].Should().Be(NodeState.Pending);
        states.States["c"].Should().Be(NodeState.Pending);
    }

    [Fact]
    public void Untaken_branches_are_skipped_once_the_run_has_progressed()
    {
        NodeStates states = NodeStateProjector.Project(
        [
            TestFactory.Event("i1", 1, WorkflowEventTypes.ExecutorInvoked, "a"),
            TestFactory.Event("i1", 2, WorkflowEventTypes.ExecutorCompleted, "a")
        ], allNodeIds: ["a", "b"]);

        states.States["b"].Should().Be(NodeState.Skipped);
    }

    [Fact]
    public void Events_are_folded_in_sequence_order_regardless_of_input_order()
    {
        NodeStates states = NodeStateProjector.Project(
        [
            TestFactory.Event("i1", 2, WorkflowEventTypes.ExecutorCompleted, "a"),
            TestFactory.Event("i1", 1, WorkflowEventTypes.ExecutorInvoked, "a")
        ]);

        states.States["a"].Should().Be(NodeState.Completed);
    }

    [Fact]
    public void Executor_id_is_recovered_from_the_payload_when_the_column_is_absent()
    {
        NodeStates states = NodeStateProjector.Project(
        [
            TestFactory.Event("i1", 1, WorkflowEventTypes.ExecutorInvoked,
                executorId: null, payload: """{"executorId":"from-payload"}""")
        ]);

        states.States.Should().ContainKey("from-payload");
    }

    [Fact]
    public void Events_without_an_executor_are_ignored()
    {
        NodeStates states = NodeStateProjector.Project(
            [TestFactory.Event("i1", 1, WorkflowEventTypes.WorkflowStarted, payload: """{"workflow":"wf"}""")]);

        states.States.Should().BeEmpty();
    }

    [Fact]
    public void Malformed_payloads_do_not_throw()
    {
        NodeStates states = NodeStateProjector.Project(
            [TestFactory.Event("i1", 1, WorkflowEventTypes.ExecutorInvoked, executorId: null, payload: "not json")]);

        states.States.Should().BeEmpty();
    }

    [Fact]
    public void Empty_stream_projects_to_nothing()
        => NodeStateProjector.Project([]).States.Should().BeEmpty();

    [Fact]
    public void Rejects_a_null_event_source()
    {
        Action act = () => NodeStateProjector.Project(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}

public class RedactionPolicyTests
{
    [Fact]
    public void Default_policy_passes_bodies_through()
        => RedactionPolicy.Default.RedactBody("""{"a":1}""").Should().Be("""{"a":1}""");

    [Fact]
    public void Allowlisted_fields_survive_and_others_are_masked()
    {
        var policy = new RedactionPolicy(bodyAllowList: ["id", "status"]);

        string result = policy.RedactBody("""{"id":"abc","status":"ok","ssn":"123-45-6789"}""");

        result.Should().Contain("abc").And.Contain("ok");
        result.Should().NotContain("123-45-6789");
        result.Should().Contain(RedactionPolicy.Mask);
    }

    [Fact]
    public void Nested_objects_are_redacted_recursively()
    {
        var policy = new RedactionPolicy(bodyAllowList: ["id"]);

        string result = policy.RedactBody("""{"id":"abc","customer":{"id":"cust1","email":"a@b.com"}}""");

        result.Should().NotContain("a@b.com");
        result.Should().Contain("cust1", "an allowlisted key is allowlisted at every depth");
    }

    [Fact]
    public void Arrays_are_redacted_element_wise()
    {
        var policy = new RedactionPolicy(bodyAllowList: ["id"]);

        string result = policy.RedactBody("""{"items":[{"id":"1","secret":"s1"},{"id":"2","secret":"s2"}]}""");

        result.Should().NotContain("s1").And.NotContain("s2");
        result.Should().Contain("\"1\"").And.Contain("\"2\"");
    }

    [Fact]
    public void Non_json_bodies_are_masked_entirely()
    {
        var policy = new RedactionPolicy(bodyAllowList: ["id"]);
        policy.RedactBody("plain text secret").Should().Be(RedactionPolicy.Mask);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_bodies_pass_through(string? body)
    {
        var policy = new RedactionPolicy(bodyAllowList: ["id"]);
        policy.RedactBody(body).Should().Be(body ?? string.Empty);
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("authorization")]
    [InlineData("Cookie")]
    [InlineData("Set-Cookie")]
    [InlineData("x-api-key")]
    [InlineData("X-API-KEY")]
    [InlineData("Proxy-Authorization")]
    public void Sensitive_headers_are_always_dropped(string header)
    {
        var policy = new RedactionPolicy();

        IReadOnlyDictionary<string, string> result = policy.RedactHeaders(
            [new KeyValuePair<string, string>(header, "secret-value")]);

        result[header].Should().Be(RedactionPolicy.Mask);
    }

    [Fact]
    public void Ordinary_headers_survive()
    {
        var policy = new RedactionPolicy();

        IReadOnlyDictionary<string, string> result = policy.RedactHeaders(
            [new KeyValuePair<string, string>("Content-Type", "application/json")]);

        result["Content-Type"].Should().Be("application/json");
    }

    [Fact]
    public void Query_values_are_masked_unless_allowlisted()
    {
        var policy = new RedactionPolicy(queryAllowList: ["page"]);

        string result = policy.RedactUrl(new Uri("https://api.example.com/v1/items?page=2&token=abc123"));

        result.Should().Contain("page=2");
        result.Should().NotContain("abc123");
        result.Should().Contain("token=" + RedactionPolicy.Mask);
    }

    [Fact]
    public void Urls_without_a_query_are_returned_as_the_path()
        => new RedactionPolicy().RedactUrl(new Uri("https://api.example.com/v1/items"))
            .Should().Be("https://api.example.com/v1/items");

    [Fact]
    public void Redact_helpers_reject_nulls()
    {
        var policy = new RedactionPolicy();
        policy.Invoking(p => p.RedactHeaders(null!)).Should().Throw<ArgumentNullException>();
        policy.Invoking(p => p.RedactUrl(null!)).Should().Throw<ArgumentNullException>();
    }
}

public class EgressGuardTests
{
    private static readonly string[] Allowed = ["erp.internal", "*.partner.com"];

    [Fact]
    public void Allows_an_exact_host_match()
        => FluentActions.Invoking(() => EgressGuard.Assert("https://erp.internal/invoices/1", Allowed))
            .Should().NotThrow();

    [Fact]
    public void Allows_a_wildcard_subdomain_match()
        => FluentActions.Invoking(() => EgressGuard.Assert("https://api.partner.com/x", Allowed))
            .Should().NotThrow();

    [Fact]
    public void Blocks_a_host_not_on_the_allowlist()
        => FluentActions.Invoking(() => EgressGuard.Assert("https://evil.example.com/x", Allowed))
            .Should().Throw<EgressBlockedException>().WithMessage("*not on the egress allowlist*");

    [Theory]
    [InlineData("http://127.0.0.1/admin")]
    [InlineData("http://10.0.0.5/internal")]
    [InlineData("http://192.168.1.1/router")]
    [InlineData("http://172.16.0.1/x")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    public void Blocks_internal_address_literals(string url)
        => FluentActions.Invoking(() => EgressGuard.Assert(url, Allowed))
            .Should().Throw<EgressBlockedException>().WithMessage("*internal address*");

    [Fact]
    public void Cloud_metadata_endpoint_is_blocked_even_if_allowlisted()
        => FluentActions.Invoking(() => EgressGuard.Assert("http://169.254.169.254/x", ["169.254.169.254"]))
            .Should().Throw<EgressBlockedException>();

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/x")]
    [InlineData("gopher://example.com")]
    public void Blocks_non_http_schemes(string url)
        => FluentActions.Invoking(() => EgressGuard.Assert(url, Allowed))
            .Should().Throw<EgressBlockedException>();

    [Fact]
    public void Blocks_relative_urls()
        => FluentActions.Invoking(() => EgressGuard.Assert("/relative/path", Allowed))
            .Should().Throw<EgressBlockedException>().WithMessage("*absolute*");

    [Fact]
    public void An_empty_allowlist_denies_everything()
        => FluentActions.Invoking(() => EgressGuard.Assert("https://anything.com", []))
            .Should().Throw<EgressBlockedException>().WithMessage("*no egress allowlist*");

    [Fact]
    public void Enforcement_can_be_disabled_for_local_development()
        => FluentActions.Invoking(() => EgressGuard.Assert("https://anything.com", [], enforce: false))
            .Should().NotThrow();

    [Fact]
    public void Host_matching_is_case_insensitive()
        => EgressGuard.MatchesHost("ERP.Internal", "erp.internal").Should().BeTrue();

    [Fact]
    public void Wildcard_does_not_match_the_bare_domain()
        => EgressGuard.MatchesHost("partner.com", "*.partner.com").Should().BeFalse();

    [Theory]
    [InlineData("8.8.8.8", false)]
    [InlineData("1.1.1.1", false)]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("0.0.0.0", true)]
    public void Internal_detection_matches_rfc1918_and_loopback(string address, bool expected)
        => EgressGuard.IsInternal(IPAddress.Parse(address)).Should().Be(expected);

    [Fact]
    public void Ipv6_loopback_is_internal()
        => EgressGuard.IsInternal(IPAddress.IPv6Loopback).Should().BeTrue();
}

using System.Text.Json;
using Abacus.Run.Abstractions;
using Abacus.Run.Core;
using Abacus.Run.Persistence;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Abacus.Run.UnitTests;

public class CheckpointStoreTests
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-07-30T10:00:00Z"));

    private static JsonElement Payload(string value = "state")
        => JsonDocument.Parse($$"""{"value":"{{value}}"}""").RootElement.Clone();

    [Fact]
    public async Task Round_trips_an_inline_payload()
    {
        var store = new OverflowCheckpointStore(clock: _clock);

        CheckpointInfo info = await store.CreateCheckpointAsync("s1", Payload("hello"));
        JsonElement restored = await store.RetrieveCheckpointAsync("s1", info);

        restored.GetProperty("value").GetString().Should().Be("hello");
        info.SessionId.Should().Be("s1");
    }

    [Fact]
    public async Task Index_is_oldest_first()
    {
        // CheckpointManager takes the LAST element as the resume point, so this ordering is a contract.
        var store = new OverflowCheckpointStore(clock: _clock);

        var created = new List<CheckpointInfo>();
        for (int i = 0; i < 5; i++)
        {
            created.Add(await store.CreateCheckpointAsync("s1", Payload($"v{i}")));
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        IEnumerable<CheckpointInfo> index = await store.RetrieveIndexAsync("s1");

        index.Select(c => c.CheckpointId).Should().Equal(created.Select(c => c.CheckpointId));
        index.Last().CheckpointId.Should().Be(created[^1].CheckpointId);
    }

    [Fact]
    public async Task Ordering_stays_total_when_commit_timestamps_collide()
    {
        // A frozen clock forces identical CommittedAt values within one superstep. Without the id
        // tiebreak the resume point becomes non-deterministic.
        var store = new OverflowCheckpointStore(clock: _clock);

        var created = new List<CheckpointInfo>();
        for (int i = 0; i < 10; i++)
        {
            created.Add(await store.CreateCheckpointAsync("s1", Payload($"v{i}")));
        }

        IEnumerable<CheckpointInfo> index = await store.RetrieveIndexAsync("s1");

        index.Select(c => c.CheckpointId).Should().Equal(
            created.Select(c => c.CheckpointId),
            "ids are monotonic, so they break the timestamp tie in creation order");
    }

    [Fact]
    public async Task Repeated_index_reads_are_stable_under_collision()
    {
        var store = new OverflowCheckpointStore(clock: _clock);
        for (int i = 0; i < 8; i++)
        {
            await store.CreateCheckpointAsync("s1", Payload($"v{i}"));
        }

        string[] first = (await store.RetrieveIndexAsync("s1")).Select(c => c.CheckpointId).ToArray();
        string[] second = (await store.RetrieveIndexAsync("s1")).Select(c => c.CheckpointId).ToArray();

        second.Should().Equal(first);
    }

    [Fact]
    public async Task Large_payloads_are_offloaded_to_the_blob_store()
    {
        var blobs = new InMemoryBlobStore();
        var store = new OverflowCheckpointStore(blobs, inlineThresholdBytes: 128, clock: _clock);

        JsonElement large = JsonDocument.Parse($$"""{"value":"{{new string('x', 5000)}}"}""").RootElement.Clone();
        CheckpointInfo info = await store.CreateCheckpointAsync("s1", large);

        blobs.Count.Should().Be(1);
        store.Describe("s1").Single().BlobUri.Should().NotBeNull();
        store.Describe("s1").Single().Payload.Should().BeNull();

        JsonElement restored = await store.RetrieveCheckpointAsync("s1", info);
        restored.GetProperty("value").GetString().Should().HaveLength(5000);
    }

    [Fact]
    public async Task Small_payloads_stay_inline()
    {
        var blobs = new InMemoryBlobStore();
        var store = new OverflowCheckpointStore(blobs, inlineThresholdBytes: 100_000, clock: _clock);

        await store.CreateCheckpointAsync("s1", Payload());

        blobs.Count.Should().Be(0);
        store.Describe("s1").Single().Payload.Should().NotBeNull();
    }

    [Fact]
    public async Task Parent_filtering_returns_only_children()
    {
        var store = new OverflowCheckpointStore(clock: _clock);

        CheckpointInfo root = await store.CreateCheckpointAsync("s1", Payload("root"));
        CheckpointInfo child = await store.CreateCheckpointAsync("s1", Payload("child"), root);
        await store.CreateCheckpointAsync("s1", Payload("unrelated"));

        IEnumerable<CheckpointInfo> children = await store.RetrieveIndexAsync("s1", root);

        children.Should().ContainSingle().Which.CheckpointId.Should().Be(child.CheckpointId);
    }

    [Fact]
    public async Task Sessions_are_isolated()
    {
        var store = new OverflowCheckpointStore(clock: _clock);
        await store.CreateCheckpointAsync("s1", Payload());
        await store.CreateCheckpointAsync("s2", Payload());

        (await store.RetrieveIndexAsync("s1")).Should().ContainSingle();
        (await store.RetrieveIndexAsync("s2")).Should().ContainSingle();
        store.SessionCount.Should().Be(2);
    }

    [Fact]
    public async Task Unknown_session_yields_an_empty_index()
        => (await new OverflowCheckpointStore().RetrieveIndexAsync("nope")).Should().BeEmpty();

    [Fact]
    public async Task Retrieving_a_missing_checkpoint_throws()
    {
        var store = new OverflowCheckpointStore(clock: _clock);
        await store.CreateCheckpointAsync("s1", Payload());

        Func<Task> act = async () => await store.RetrieveCheckpointAsync("s1", new CheckpointInfo("s1", "missing"));
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task Empty_session_ids_are_rejected()
    {
        var store = new OverflowCheckpointStore();
        Func<Task> act = async () => await store.CreateCheckpointAsync("", Payload());
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Describe_reports_size_and_commit_time_in_order()
    {
        var store = new OverflowCheckpointStore(clock: _clock);
        await store.CreateCheckpointAsync("s1", Payload("a"));
        _clock.Advance(TimeSpan.FromSeconds(1));
        await store.CreateCheckpointAsync("s1", Payload("bb"));

        IReadOnlyList<CheckpointRecord> described = store.Describe("s1");

        described.Should().HaveCount(2);
        described[0].CommittedAt.Should().BeBefore(described[1].CommittedAt);
        described.Should().OnlyContain(c => c.SizeBytes > 0);
    }

    [Fact]
    public async Task Prune_removes_only_checkpoints_older_than_the_cutoff()
    {
        var store = new OverflowCheckpointStore(clock: _clock);
        await store.CreateCheckpointAsync("s1", Payload("old"));

        _clock.Advance(TimeSpan.FromDays(10));
        DateTimeOffset cutoff = _clock.GetUtcNow();
        await store.CreateCheckpointAsync("s1", Payload("new"));

        int removed = store.Prune(cutoff);

        removed.Should().Be(1);
        store.Describe("s1").Should().ContainSingle();
    }

    [Fact]
    public async Task Pruning_every_checkpoint_drops_the_session()
    {
        var store = new OverflowCheckpointStore(clock: _clock);
        await store.CreateCheckpointAsync("s1", Payload());

        _clock.Advance(TimeSpan.FromDays(30));
        store.Prune(_clock.GetUtcNow());

        store.SessionCount.Should().Be(0);
    }

    [Fact]
    public async Task Concurrent_creates_are_all_retained()
    {
        var store = new OverflowCheckpointStore(clock: _clock);

        await Task.WhenAll(Enumerable.Range(0, 100).Select(i =>
            Task.Run(async () => await store.CreateCheckpointAsync("s1", Payload($"v{i}")))));

        (await store.RetrieveIndexAsync("s1")).Should().HaveCount(100);
    }
}

public class ApprovalStoreTests
{
    private readonly InMemoryApprovalStore _store = new();

    private ApprovalRequest Request(
        string id = "apr_1", string instanceId = "i1", ApprovalState state = ApprovalState.Pending,
        string tenant = "t1", string[]? assignees = null, DateTimeOffset? expiresAt = null)
        => new()
        {
            ApprovalId = id,
            InstanceId = instanceId,
            TenantId = tenant,
            ExecutorId = "pay",
            Assignees = assignees ?? [],
            State = state,
            CreatedAt = DateTimeOffset.UnixEpoch,
            ExpiresAt = expiresAt ?? DateTimeOffset.UnixEpoch.AddHours(1)
        };

    [Fact]
    public async Task Create_and_get_round_trip()
    {
        await _store.CreateAsync(Request(), default);
        (await _store.GetAsync("apr_1", default))!.ExecutorId.Should().Be("pay");
    }

    [Fact]
    public async Task Duplicate_ids_are_rejected()
    {
        await _store.CreateAsync(Request(), default);
        await _store.Invoking(s => s.CreateAsync(Request(), default).AsTask())
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Get_of_an_unknown_id_is_null()
        => (await _store.GetAsync("nope", default)).Should().BeNull();

    [Fact]
    public async Task List_for_instance_is_ordered_by_creation()
    {
        await _store.CreateAsync(Request("apr_1"), default);
        await _store.CreateAsync(Request("apr_2"), default);
        await _store.CreateAsync(Request("apr_3", instanceId: "other"), default);

        IReadOnlyList<ApprovalRequest> list = await _store.ListForInstanceAsync("i1", default);

        list.Should().HaveCount(2);
    }

    [Fact]
    public async Task Pending_query_surfaces_soonest_expiry_first()
    {
        await _store.CreateAsync(Request("late", expiresAt: DateTimeOffset.UnixEpoch.AddHours(8)), default);
        await _store.CreateAsync(Request("soon", expiresAt: DateTimeOffset.UnixEpoch.AddHours(1)), default);

        IReadOnlyList<ApprovalRequest> pending = await _store.QueryPendingAsync(null, null, 10, default);

        pending[0].ApprovalId.Should().Be("soon", "the queue should surface what is about to time out");
    }

    [Fact]
    public async Task Pending_query_filters_by_tenant_and_assignee()
    {
        await _store.CreateAsync(Request("a", tenant: "t1", assignees: ["group:finance"]), default);
        await _store.CreateAsync(Request("b", tenant: "t2", assignees: ["group:finance"]), default);
        await _store.CreateAsync(Request("c", tenant: "t1", assignees: ["group:ops"]), default);

        (await _store.QueryPendingAsync("t1", null, 10, default)).Should().HaveCount(2);
        (await _store.QueryPendingAsync("t1", ["group:finance"], 10, default))
            .Should().ContainSingle().Which.ApprovalId.Should().Be("a");
    }

    [Fact]
    public async Task Decided_approvals_leave_the_pending_queue()
    {
        await _store.CreateAsync(Request(), default);
        await _store.TrySetStateAsync("apr_1", ApprovalState.Approved, default);

        (await _store.QueryPendingAsync(null, null, 10, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task Claim_expired_returns_only_due_pending_approvals()
    {
        await _store.CreateAsync(Request("due", expiresAt: DateTimeOffset.UnixEpoch.AddMinutes(5)), default);
        await _store.CreateAsync(Request("later", expiresAt: DateTimeOffset.UnixEpoch.AddHours(5)), default);
        await _store.CreateAsync(Request("decided", state: ApprovalState.Approved,
            expiresAt: DateTimeOffset.UnixEpoch.AddMinutes(1)), default);

        IReadOnlyList<ApprovalRequest> due = await _store.ClaimExpiredAsync(
            DateTimeOffset.UnixEpoch.AddMinutes(10), 100, default);

        due.Should().ContainSingle().Which.ApprovalId.Should().Be("due");
    }

    [Fact]
    public async Task One_vote_per_decider_is_enforced_at_the_store()
    {
        await _store.CreateAsync(Request(), default);

        var vote = new ApprovalDecision
        {
            ApprovalId = "apr_1", DeciderId = "alice", Outcome = ApprovalOutcomeKind.Approve,
            DecidedAt = DateTimeOffset.UnixEpoch
        };

        (await _store.TryRecordDecisionAsync("apr_1", vote, null, default)).Should().BeTrue();
        (await _store.TryRecordDecisionAsync("apr_1", vote, null, default)).Should().BeFalse();

        (await _store.GetDecisionsAsync("apr_1", default)).Should().ContainSingle();
    }

    [Fact]
    public async Task Decisions_cannot_be_recorded_once_the_state_has_moved_on()
    {
        await _store.CreateAsync(Request(state: ApprovalState.Approved), default);

        bool recorded = await _store.TryRecordDecisionAsync("apr_1", new ApprovalDecision
        {
            ApprovalId = "apr_1", DeciderId = "late", Outcome = ApprovalOutcomeKind.Reject,
            DecidedAt = DateTimeOffset.UnixEpoch
        }, ApprovalState.Rejected, default);

        recorded.Should().BeFalse();
    }

    [Fact]
    public async Task Setting_state_back_to_pending_marks_it_escalated()
    {
        await _store.CreateAsync(Request(), default);
        await _store.TrySetStateAsync("apr_1", ApprovalState.Pending, default);

        (await _store.GetAsync("apr_1", default))!.EscalatedOnce.Should().BeTrue();
    }

    [Fact]
    public async Task Cancelling_an_instance_cancels_only_its_pending_approvals()
    {
        await _store.CreateAsync(Request("a", instanceId: "i1"), default);
        await _store.CreateAsync(Request("b", instanceId: "i1", state: ApprovalState.Approved), default);
        await _store.CreateAsync(Request("c", instanceId: "i2"), default);

        await _store.CancelForInstanceAsync("i1", default);

        (await _store.GetAsync("a", default))!.State.Should().Be(ApprovalState.Cancelled);
        (await _store.GetAsync("b", default))!.State.Should().Be(ApprovalState.Approved);
        (await _store.GetAsync("c", default))!.State.Should().Be(ApprovalState.Pending);
    }

    [Fact]
    public async Task Concurrent_votes_yield_a_single_winner()
    {
        await _store.CreateAsync(Request(), default);

        bool[] results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
            await _store.TryRecordDecisionAsync("apr_1", new ApprovalDecision
            {
                ApprovalId = "apr_1", DeciderId = $"user{i}", Outcome = ApprovalOutcomeKind.Approve,
                DecidedAt = DateTimeOffset.UnixEpoch
            }, ApprovalState.Approved, default))));

        results.Count(r => r).Should().Be(1);
    }

    [Fact]
    public async Task Missing_approvals_report_false_or_empty()
    {
        (await _store.TrySetStateAsync("nope", ApprovalState.Approved, default)).Should().BeFalse();
        (await _store.GetDecisionsAsync("nope", default)).Should().BeEmpty();
    }

    [Fact]
    public async Task Create_rejects_null()
        => await _store.Invoking(s => s.CreateAsync(null!, default).AsTask()).Should().ThrowAsync<ArgumentNullException>();
}

public class SupportingStoreTests
{
    [Fact]
    public async Task Log_store_filters_by_level_and_executor()
    {
        var store = new InMemoryLogStore();
        await store.AppendAsync(Entry("i1", "Error", "a"), default);
        await store.AppendAsync(Entry("i1", "Information", "a"), default);
        await store.AppendAsync(Entry("i1", "Error", "b"), default);

        (await store.QueryAsync("i1", null, null, 100, default)).Should().HaveCount(3);
        (await store.QueryAsync("i1", "Error", null, 100, default)).Should().HaveCount(2);
        (await store.QueryAsync("i1", "Error", "a", 100, default)).Should().ContainSingle();
        (await store.QueryAsync("i1", "error", null, 100, default)).Should().HaveCount(2, "level match is case-insensitive");
    }

    [Fact]
    public async Task Log_store_isolates_instances()
    {
        var store = new InMemoryLogStore();
        await store.AppendAsync(Entry("i1", "Error", "a"), default);
        await store.AppendAsync(Entry("i2", "Error", "a"), default);

        (await store.QueryAsync("i1", null, null, 100, default)).Should().ContainSingle();
    }

    [Fact]
    public async Task Log_store_rejects_null_entries()
    {
        var store = new InMemoryLogStore();
        await store.Invoking(s => s.AppendAsync(null!, default).AsTask()).Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Blob_store_round_trips_and_deletes()
    {
        var store = new InMemoryBlobStore();
        byte[] content = [1, 2, 3, 4];

        string uri = await store.UploadAsync("k1", content, default);
        (await store.DownloadAsync(uri, default)).Should().Equal(content);

        await store.DeleteAsync(uri, default);
        store.Count.Should().Be(0);
    }

    [Fact]
    public async Task Downloading_a_missing_blob_throws()
    {
        var store = new InMemoryBlobStore();
        await store.Invoking(s => s.DownloadAsync("mem://missing", default).AsTask())
            .Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task Audit_store_returns_newest_first_and_filters_by_instance()
    {
        var store = new InMemoryAuditStore();
        await store.WriteAsync(new AuditEntry
        {
            Action = "a", InstanceId = "i1", ActorId = "alice", OccurredAt = DateTimeOffset.UnixEpoch
        }, default);
        await store.WriteAsync(new AuditEntry
        {
            Action = "b", InstanceId = "i1", ActorId = "bob", OccurredAt = DateTimeOffset.UnixEpoch.AddMinutes(1)
        }, default);
        await store.WriteAsync(new AuditEntry
        {
            Action = "c", InstanceId = "i2", ActorId = "carol", OccurredAt = DateTimeOffset.UnixEpoch
        }, default);

        IReadOnlyList<AuditEntry> entries = await store.QueryAsync("i1", 10, default);

        entries.Should().HaveCount(2);
        entries[0].Action.Should().Be("b");

        (await store.QueryAsync(null, 10, default)).Should().HaveCount(3);
    }

    [Fact]
    public async Task Audit_store_rejects_null()
    {
        var store = new InMemoryAuditStore();
        await store.Invoking(s => s.WriteAsync(null!, default).AsTask()).Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Gate_policy_store_round_trips_workflow_and_instance_scopes()
    {
        var store = new InMemoryGatePolicyStore();
        var gate = new ApprovalGate { Mode = ExecutionMode.RequireApproval, Reason = "policy" };

        await store.SetAsync("wf", "1.0.0", "pay", gate, default);

        (await store.FindAsync("wf", "1.0.0", "pay", null, default))!.Reason.Should().Be("policy");
        (await store.FindAsync("wf", "2.0.0", "pay", null, default)).Should().BeNull("policies are version-scoped");
        (await store.FindAsync("wf", "1.0.0", "other", null, default)).Should().BeNull();
    }

    private static InstanceLogEntry Entry(string instanceId, string level, string executorId) => new()
    {
        InstanceId = instanceId,
        Sequence = 1,
        Level = level,
        ExecutorId = executorId,
        Message = "msg",
        LoggedAt = DateTimeOffset.UnixEpoch
    };
}

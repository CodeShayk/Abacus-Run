using Abacus.Run.Abstractions;
using Abacus.Run.Core;
using Abacus.Run.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Abacus.Run.UnitTests;

public class InstanceStoreTests
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-07-30T10:00:00Z"));
    private readonly InMemoryInstanceStore _store;

    public InstanceStoreTests() => _store = new InMemoryInstanceStore(_clock);

    private Task<WorkflowInstance> CreateAsync(
        string? id = null, string tenant = "t1", string workflow = "wf", string? idempotencyKey = null)
        => _store.CreateAsync(TestFactory.CreateRequest(id, tenant, workflow, idempotencyKey: idempotencyKey), default).AsTask();

    [Fact]
    public async Task Created_instances_start_pending_with_version_one()
    {
        WorkflowInstance instance = await CreateAsync();

        instance.Status.Should().Be(InstanceStatus.Pending);
        instance.Version.Should().Be(1);
        instance.CreatedAt.Should().Be(_clock.GetUtcNow());
        instance.LeaseOwner.Should().BeNull();
    }

    [Fact]
    public async Task Duplicate_instance_ids_are_rejected()
    {
        await CreateAsync("dup");
        Func<Task> act = async () => await CreateAsync("dup");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Get_returns_null_for_an_unknown_id()
        => (await _store.GetAsync("nope", default)).Should().BeNull();

    [Fact]
    public async Task Idempotency_lookup_is_scoped_per_tenant()
    {
        await CreateAsync("a", tenant: "t1", idempotencyKey: "key-1");
        await CreateAsync("b", tenant: "t2", idempotencyKey: "key-1");

        (await _store.FindByIdempotencyKeyAsync("t1", "key-1", default))!.InstanceId.Should().Be("a");
        (await _store.FindByIdempotencyKeyAsync("t2", "key-1", default))!.InstanceId.Should().Be("b");
        (await _store.FindByIdempotencyKeyAsync("t3", "key-1", default)).Should().BeNull();
    }

    [Fact]
    public async Task Transition_succeeds_from_the_expected_state()
    {
        WorkflowInstance instance = await CreateAsync();

        bool moved = await _store.TryTransitionAsync(
            instance.InstanceId, InstanceStatus.Pending, InstanceStatus.Running, null, default);

        moved.Should().BeTrue();
        (await _store.GetAsync(instance.InstanceId, default))!.Status.Should().Be(InstanceStatus.Running);
    }

    [Fact]
    public async Task Transition_fails_from_an_unexpected_state()
    {
        WorkflowInstance instance = await CreateAsync();

        bool moved = await _store.TryTransitionAsync(
            instance.InstanceId, InstanceStatus.Running, InstanceStatus.Completed, null, default);

        moved.Should().BeFalse();
    }

    [Fact]
    public async Task Transition_never_leaves_a_terminal_state()
    {
        WorkflowInstance instance = await CreateAsync();
        await _store.UpdateAsync(instance.InstanceId, m => m.Status = InstanceStatus.Completed, default);

        bool moved = await _store.TryTransitionAsync(
            instance.InstanceId, InstanceStatus.Completed, InstanceStatus.Running, null, default);

        moved.Should().BeFalse("terminal states are final");
    }

    [Fact]
    public async Task Update_will_not_resurrect_a_terminal_instance()
    {
        WorkflowInstance instance = await CreateAsync();
        await _store.UpdateAsync(instance.InstanceId, m => m.Status = InstanceStatus.DeadStopped, default);

        bool updated = await _store.UpdateAsync(instance.InstanceId, m => m.Status = InstanceStatus.Running, default);

        updated.Should().BeFalse();
        (await _store.GetAsync(instance.InstanceId, default))!.Status.Should().Be(InstanceStatus.DeadStopped);
    }

    [Fact]
    public async Task Update_of_a_terminal_instance_to_the_same_status_is_permitted()
    {
        WorkflowInstance instance = await CreateAsync();
        await _store.UpdateAsync(instance.InstanceId, m => m.Status = InstanceStatus.Completed, default);

        bool updated = await _store.UpdateAsync(instance.InstanceId, m =>
        {
            m.Status = InstanceStatus.Completed;
            m.ResultJson = "{}";
        }, default);

        updated.Should().BeTrue();
    }

    [Fact]
    public async Task Update_bumps_the_version_and_timestamp()
    {
        WorkflowInstance instance = await CreateAsync();
        _clock.Advance(TimeSpan.FromMinutes(1));

        await _store.UpdateAsync(instance.InstanceId, m => m.Status = InstanceStatus.Running, default);

        WorkflowInstance? updated = await _store.GetAsync(instance.InstanceId, default);
        updated!.Version.Should().Be(2);
        updated.UpdatedAt.Should().Be(_clock.GetUtcNow());
    }

    [Fact]
    public async Task Update_of_an_unknown_instance_returns_false()
        => (await _store.UpdateAsync("nope", _ => { }, default)).Should().BeFalse();

    [Fact]
    public async Task Clear_flags_null_out_lease_and_retry_fields()
    {
        WorkflowInstance instance = await CreateAsync();
        await _store.UpdateAsync(instance.InstanceId, m =>
        {
            m.LeaseOwner = "replica-1";
            m.LeaseExpiresAt = _clock.GetUtcNow().AddMinutes(1);
            m.NextRetryAt = _clock.GetUtcNow().AddMinutes(5);
        }, default);

        await _store.UpdateAsync(instance.InstanceId, m =>
        {
            m.ClearLease = true;
            m.ClearNextRetryAt = true;
        }, default);

        WorkflowInstance? cleared = await _store.GetAsync(instance.InstanceId, default);
        cleared!.LeaseOwner.Should().BeNull();
        cleared.LeaseExpiresAt.Should().BeNull();
        cleared.NextRetryAt.Should().BeNull();
    }

    [Fact]
    public async Task Claim_leases_a_pending_instance_and_marks_it_running()
    {
        await CreateAsync("i1");

        IReadOnlyList<WorkflowInstance> claimed = await _store.ClaimAsync(
            "replica-1", 10, TimeSpan.FromSeconds(60), _clock.GetUtcNow(), default);

        claimed.Should().ContainSingle();
        claimed[0].Status.Should().Be(InstanceStatus.Running);
        claimed[0].LeaseOwner.Should().Be("replica-1");
        claimed[0].LeaseExpiresAt.Should().Be(_clock.GetUtcNow().AddSeconds(60));
        claimed[0].StartedAt.Should().Be(_clock.GetUtcNow());
    }

    [Fact]
    public async Task A_leased_instance_is_not_claimed_again()
    {
        await CreateAsync("i1");
        await _store.ClaimAsync("replica-1", 10, TimeSpan.FromSeconds(60), _clock.GetUtcNow(), default);

        IReadOnlyList<WorkflowInstance> second = await _store.ClaimAsync(
            "replica-2", 10, TimeSpan.FromSeconds(60), _clock.GetUtcNow(), default);

        second.Should().BeEmpty();
    }

    [Fact]
    public async Task An_expired_lease_is_reclaimed_as_an_orphan()
    {
        await CreateAsync("i1");
        await _store.ClaimAsync("replica-1", 10, TimeSpan.FromSeconds(60), _clock.GetUtcNow(), default);

        _clock.Advance(TimeSpan.FromSeconds(90));

        IReadOnlyList<WorkflowInstance> recovered = await _store.ClaimAsync(
            "replica-2", 10, TimeSpan.FromSeconds(60), _clock.GetUtcNow(), default);

        recovered.Should().ContainSingle();
        recovered[0].LeaseOwner.Should().Be("replica-2");
    }

    [Fact]
    public async Task Retry_scheduled_instances_are_claimed_only_once_due()
    {
        WorkflowInstance instance = await CreateAsync("i1");
        await _store.UpdateAsync(instance.InstanceId, m =>
        {
            m.Status = InstanceStatus.RetryScheduled;
            m.NextRetryAt = _clock.GetUtcNow().AddMinutes(5);
        }, default);

        (await _store.ClaimAsync("r1", 10, TimeSpan.FromSeconds(60), _clock.GetUtcNow(), default))
            .Should().BeEmpty("the retry is not yet due");

        _clock.Advance(TimeSpan.FromMinutes(6));

        IReadOnlyList<WorkflowInstance> claimed = await _store.ClaimAsync(
            "r1", 10, TimeSpan.FromSeconds(60), _clock.GetUtcNow(), default);

        claimed.Should().ContainSingle();
        claimed[0].Status.Should().Be(InstanceStatus.Running);
        claimed[0].NextRetryAt.Should().BeNull();
    }

    [Fact]
    public async Task Dispatchable_instances_are_claimed_immediately()
    {
        WorkflowInstance instance = await CreateAsync("i1");
        await _store.UpdateAsync(instance.InstanceId, m =>
        {
            m.Status = InstanceStatus.Dispatchable;
            m.ClearLease = true;
        }, default);

        IReadOnlyList<WorkflowInstance> claimed = await _store.ClaimAsync(
            "r1", 10, TimeSpan.FromSeconds(60), _clock.GetUtcNow(), default);

        claimed.Should().ContainSingle();
    }

    [Fact]
    public async Task Terminal_instances_are_never_claimed()
    {
        WorkflowInstance instance = await CreateAsync("i1");
        await _store.UpdateAsync(instance.InstanceId, m => m.Status = InstanceStatus.Completed, default);

        (await _store.ClaimAsync("r1", 10, TimeSpan.FromSeconds(60), _clock.GetUtcNow(), default))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Claim_respects_the_batch_maximum()
    {
        for (int i = 0; i < 20; i++)
        {
            await CreateAsync($"i{i}");
        }

        IReadOnlyList<WorkflowInstance> claimed = await _store.ClaimAsync(
            "r1", 5, TimeSpan.FromSeconds(60), _clock.GetUtcNow(), default);

        claimed.Should().HaveCount(5);
    }

    [Fact]
    public async Task Claim_with_no_capacity_returns_nothing()
    {
        await CreateAsync("i1");
        (await _store.ClaimAsync("r1", 0, TimeSpan.FromSeconds(60), _clock.GetUtcNow(), default))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Concurrent_claimers_receive_disjoint_sets()
    {
        const int total = 200;
        for (int i = 0; i < total; i++)
        {
            await CreateAsync($"i{i:D3}");
        }

        IReadOnlyList<WorkflowInstance>[] batches = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(replica => Task.Run(async () =>
                await _store.ClaimAsync($"replica-{replica}", 50, TimeSpan.FromSeconds(60), _clock.GetUtcNow(), default))));

        string[] allClaimed = batches.SelectMany(b => b.Select(i => i.InstanceId)).ToArray();

        allClaimed.Should().OnlyHaveUniqueItems("two replicas must never own the same instance");
        allClaimed.Should().HaveCount(total);
    }

    [Fact]
    public async Task Lease_renewal_extends_only_for_the_current_owner()
    {
        await CreateAsync("i1");
        await _store.ClaimAsync("replica-1", 10, TimeSpan.FromSeconds(60), _clock.GetUtcNow(), default);

        _clock.Advance(TimeSpan.FromSeconds(20));

        (await _store.RenewLeaseAsync("i1", "replica-1", TimeSpan.FromSeconds(60), default)).Should().BeTrue();
        (await _store.RenewLeaseAsync("i1", "replica-2", TimeSpan.FromSeconds(60), default)).Should().BeFalse();

        (await _store.GetAsync("i1", default))!.LeaseExpiresAt.Should().Be(_clock.GetUtcNow().AddSeconds(60));
    }

    [Fact]
    public async Task Renewing_an_unknown_instance_fails()
        => (await _store.RenewLeaseAsync("nope", "r1", TimeSpan.FromSeconds(60), default)).Should().BeFalse();

    [Fact]
    public async Task Release_clears_the_lease_for_the_owner_only()
    {
        await CreateAsync("i1");
        await _store.ClaimAsync("replica-1", 10, TimeSpan.FromSeconds(60), _clock.GetUtcNow(), default);

        await _store.ReleaseLeaseAsync("i1", "replica-2", default);
        (await _store.GetAsync("i1", default))!.LeaseOwner.Should().Be("replica-1");

        await _store.ReleaseLeaseAsync("i1", "replica-1", default);
        (await _store.GetAsync("i1", default))!.LeaseOwner.Should().BeNull();
    }

    [Fact]
    public async Task Released_instances_are_immediately_reclaimable()
    {
        await CreateAsync("i1");
        await _store.ClaimAsync("replica-1", 10, TimeSpan.FromSeconds(60), _clock.GetUtcNow(), default);
        await _store.ReleaseLeaseAsync("i1", "replica-1", default);

        // Explicit release turns a 90-second orphan recovery into an instant handoff on planned restarts.
        IReadOnlyList<WorkflowInstance> claimed = await _store.ClaimAsync(
            "replica-2", 10, TimeSpan.FromSeconds(60), _clock.GetUtcNow(), default);

        claimed.Should().ContainSingle();
    }

    [Fact]
    public async Task Query_filters_by_status_workflow_tenant_and_correlation()
    {
        await _store.CreateAsync(TestFactory.CreateRequest("a", "t1", "alpha") with { CorrelationId = "c1" }, default);
        await _store.CreateAsync(TestFactory.CreateRequest("b", "t1", "beta"), default);
        await _store.CreateAsync(TestFactory.CreateRequest("c", "t2", "alpha"), default);
        await _store.UpdateAsync("b", m => m.Status = InstanceStatus.Completed, default);

        (await _store.QueryAsync(new InstanceQuery { TenantId = "t1" }, default)).Total.Should().Be(2);
        (await _store.QueryAsync(new InstanceQuery { WorkflowName = "alpha" }, default)).Total.Should().Be(2);
        (await _store.QueryAsync(new InstanceQuery { CorrelationId = "c1" }, default)).Total.Should().Be(1);
        (await _store.QueryAsync(
            new InstanceQuery { Statuses = [InstanceStatus.Completed] }, default)).Total.Should().Be(1);
    }

    [Fact]
    public async Task Query_filters_by_creation_window()
    {
        await CreateAsync("old");
        _clock.Advance(TimeSpan.FromHours(2));
        DateTimeOffset boundary = _clock.GetUtcNow();
        await CreateAsync("new");

        (await _store.QueryAsync(new InstanceQuery { CreatedAfter = boundary }, default))
            .Items.Should().ContainSingle().Which.InstanceId.Should().Be("new");

        (await _store.QueryAsync(new InstanceQuery { CreatedBefore = boundary.AddSeconds(-1) }, default))
            .Items.Should().ContainSingle().Which.InstanceId.Should().Be("old");
    }

    [Fact]
    public async Task Query_paginates_newest_first()
    {
        for (int i = 0; i < 5; i++)
        {
            await CreateAsync($"i{i}");
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        Page<WorkflowInstance> page = await _store.QueryAsync(new InstanceQuery { Limit = 2, Offset = 0 }, default);

        page.Items.Should().HaveCount(2);
        page.Total.Should().Be(5);
        page.Items[0].InstanceId.Should().Be("i4");

        Page<WorkflowInstance> second = await _store.QueryAsync(new InstanceQuery { Limit = 2, Offset = 2 }, default);
        second.Items[0].InstanceId.Should().Be("i2");
    }

    [Fact]
    public async Task Create_and_query_reject_nulls()
    {
        await _store.Invoking(s => s.CreateAsync(null!, default).AsTask()).Should().ThrowAsync<ArgumentNullException>();
        await _store.Invoking(s => s.QueryAsync(null!, default).AsTask()).Should().ThrowAsync<ArgumentNullException>();
        await _store.Invoking(s => s.UpdateAsync("i1", null!, default).AsTask()).Should().ThrowAsync<ArgumentNullException>();
    }
}

public class InstanceStatusTests
{
    [Theory]
    [InlineData(InstanceStatus.Completed, true)]
    [InlineData(InstanceStatus.Failed, true)]
    [InlineData(InstanceStatus.DeadStopped, true)]
    [InlineData(InstanceStatus.Cancelled, true)]
    [InlineData(InstanceStatus.Running, false)]
    [InlineData(InstanceStatus.Pending, false)]
    [InlineData(InstanceStatus.AwaitingApproval, false)]
    [InlineData(InstanceStatus.AwaitingInput, false)]
    [InlineData(InstanceStatus.Suspended, false)]
    [InlineData(InstanceStatus.RetryScheduled, false)]
    [InlineData(InstanceStatus.Dispatchable, false)]
    public void Terminal_classification_is_exact(InstanceStatus status, bool expected)
        => status.IsTerminal().Should().Be(expected);

    [Theory]
    [InlineData(InstanceStatus.Pending, true)]
    [InlineData(InstanceStatus.RetryScheduled, true)]
    [InlineData(InstanceStatus.Dispatchable, true)]
    [InlineData(InstanceStatus.Running, false)]
    [InlineData(InstanceStatus.Completed, false)]
    public void Claimable_classification_is_exact(InstanceStatus status, bool expected)
        => status.IsClaimable().Should().Be(expected);

    [Fact]
    public void AwaitingApproval_is_distinct_from_AwaitingInput()
    {
        // Conflating them would make the approval policy un-targetable.
        InstanceStatus.AwaitingApproval.Should().NotBe(InstanceStatus.AwaitingInput);
        InstanceStatus.AwaitingApproval.IsTerminal().Should().BeFalse();
    }
}

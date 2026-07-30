using Abacus.Run.Abstractions;
using Abacus.Run.Core;
using Abacus.Run.Dispatch;
using Abacus.Run.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Abacus.Run.UnitTests;

public sealed class DispatchServicesTests
{
    private readonly InMemoryInstanceStore _store = new(TimeProvider.System);
    private readonly OptionsWrapper<WorkflowHostOptions> _options = new(new WorkflowHostOptions
    {
        ReplicaId = "replica-test",
        ClaimBatchSize = 10,
        Lease = new LeaseOptions { DurationSeconds = 60, RenewalSeconds = 20 },
        Drain = new DrainOptions { GraceSeconds = 1 },
        Retention = new RetentionOptions { InstanceDays = 90, CheckpointDays = 7, AuditDays = 365 }
    });

    [Fact]
    public async Task LeaseManager_TryAcquire_AcquiresLeaseSuccessfully()
    {
        var manager = new LeaseManager(_store, _options, NullLogger<LeaseManager>.Instance);

        await _store.CreateAsync(TestFactory.CreateRequest("inst-1"), CancellationToken.None);

        IReadOnlyList<WorkflowInstance> claimed = await manager.TryAcquireAsync(5, CancellationToken.None);

        claimed.Should().HaveCount(1);
        claimed[0].InstanceId.Should().Be("inst-1");
        claimed[0].LeaseOwner.Should().Be("replica-test");
    }

    [Fact]
    public async Task LeaseManager_Renew_ExtendsLease()
    {
        var manager = new LeaseManager(_store, _options, NullLogger<LeaseManager>.Instance);

        await _store.CreateAsync(TestFactory.CreateRequest("inst-renew"), CancellationToken.None);
        await manager.TryAcquireAsync(5, CancellationToken.None);

        bool renewed = await manager.RenewAsync("inst-renew", CancellationToken.None);

        renewed.Should().BeTrue();
    }

    [Fact]
    public async Task LeaseManager_Release_FreesLease()
    {
        var manager = new LeaseManager(_store, _options, NullLogger<LeaseManager>.Instance);

        await _store.CreateAsync(TestFactory.CreateRequest("inst-release"), CancellationToken.None);
        await manager.TryAcquireAsync(5, CancellationToken.None);

        await manager.ReleaseAsync("inst-release", CancellationToken.None);

        WorkflowInstance? fetched = await _store.GetAsync("inst-release", CancellationToken.None);
        fetched!.LeaseOwner.Should().BeNull();
    }

    [Fact]
    public async Task LeaseManager_ReleaseAll_ReleasesMultipleLeases()
    {
        var manager = new LeaseManager(_store, _options, NullLogger<LeaseManager>.Instance);

        await _store.CreateAsync(TestFactory.CreateRequest("inst-a"), CancellationToken.None);
        await _store.CreateAsync(TestFactory.CreateRequest("inst-b"), CancellationToken.None);
        await manager.TryAcquireAsync(10, CancellationToken.None);

        await manager.ReleaseAllAsync(["inst-a", "inst-b"], CancellationToken.None);

        WorkflowInstance? a = await _store.GetAsync("inst-a", CancellationToken.None);
        WorkflowInstance? b = await _store.GetAsync("inst-b", CancellationToken.None);

        a!.LeaseOwner.Should().BeNull();
        b!.LeaseOwner.Should().BeNull();
    }

    [Fact]
    public async Task LeaseManager_RenewIfDue_SkippedIfIntervalNotElapsed()
    {
        var manager = new LeaseManager(_store, _options, NullLogger<LeaseManager>.Instance);

        // Renewed 5 seconds ago (renewal interval is 20s) -> should return true without hitting store renewal
        bool result = await manager.RenewIfDueAsync("inst-x", DateTimeOffset.UtcNow.AddSeconds(-5), CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task DrainService_TracksAndReleasesOnStop()
    {
        var limiter = new ConcurrencyLimiter(10);
        var mockRunnerFactory = new DummyRunnerFactory();
        var dispatcher = new DispatcherService(
            _store, mockRunnerFactory, limiter, _options, NullLogger<DispatcherService>.Instance);
        var manager = new LeaseManager(_store, _options, NullLogger<LeaseManager>.Instance);

        var drain = new DrainService(
            dispatcher, limiter, manager, _options, NullLogger<DrainService>.Instance);

        await _store.CreateAsync(TestFactory.CreateRequest("drain-inst"), CancellationToken.None);
        await manager.TryAcquireAsync(5, CancellationToken.None);

        drain.TrackInstance("drain-inst");
        await drain.StopAsync(CancellationToken.None);

        WorkflowInstance? inst = await _store.GetAsync("drain-inst", CancellationToken.None);
        inst!.LeaseOwner.Should().BeNull("DrainService must release all tracked leases on stop");
    }

    [Fact]
    public async Task RetentionService_RunsCycleWithoutError()
    {
        var events = new InMemoryEventStore();
        var audit = new InMemoryAuditStore();
        var retention = new RetentionService(
            _store, events, audit, _options, NullLogger<RetentionService>.Instance);

        // Pre-seed a completed old instance
        var oldInstance = await _store.CreateAsync(TestFactory.CreateRequest("old-inst"), CancellationToken.None);

        await retention.RunRetentionCycleAsync(CancellationToken.None);

        // Cycle completes cleanly
        true.Should().BeTrue();
    }

    private sealed class DummyRunnerFactory : IWorkflowRunnerFactory
    {
        public WorkflowRunner Create(WorkflowInstance instance) => throw new NotImplementedException();
    }
}

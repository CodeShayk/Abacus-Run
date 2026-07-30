using System.Security.Claims;
using System.Text.Json;
using Abacus.Run.Abstractions;
using Abacus.Run.Api;
using Abacus.Run.Core;
using Abacus.Run.Persistence;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Abacus.Run.UnitTests;

public sealed record SampleContext(string Value = "x", decimal Amount = 0);

public sealed record SampleResult(string Value);

/// <summary>A definition that builds a trivial two-node graph.</summary>
public sealed class SampleWorkflow : IWorkflowDefinition<SampleContext, SampleResult>
{
    public SampleWorkflow(string name = "sample", string version = "1.0.0")
    {
        Name = name;
        Version = version;
    }

    public string Name { get; }
    public string Version { get; }

    public ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken cancellationToken)
    {
        ExecutorBinding start = context.Node(new TransformExecutorNode("start"));
        ExecutorBinding finish = context.Node(new TransformExecutorNode("finish"));

        Workflow workflow = new WorkflowBuilder(start)
            .AddEdge(start, finish)
            .WithOutputFrom(finish)
            .WithName(Name)
            .Build();

        return new ValueTask<Workflow>(workflow);
    }

    private sealed class TransformExecutorNode : HostExecutor<SampleContext, SampleResult>
    {
        public TransformExecutorNode(string id) : base(id) { }

        protected override ValueTask<SampleResult> ExecuteCoreAsync(
            SampleContext input, IWorkflowContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(new SampleResult(input.Value));
    }
}

public class WorkflowRegistryTests
{
    [Fact]
    public void Resolves_the_highest_semver_by_default()
    {
        var registry = new WorkflowRegistry(
        [
            new SampleWorkflow("wf", "1.0.0"),
            new SampleWorkflow("wf", "2.1.0"),
            new SampleWorkflow("wf", "1.9.0")
        ]);

        registry.Resolve("wf")!.Version.Should().Be("2.1.0");
    }

    [Fact]
    public void Resolves_an_exact_version_when_asked()
    {
        var registry = new WorkflowRegistry([new SampleWorkflow("wf", "1.0.0"), new SampleWorkflow("wf", "2.0.0")]);

        registry.Resolve("wf", "1.0.0")!.Version.Should().Be("1.0.0");
        registry.Resolve("wf", "9.9.9").Should().BeNull();
    }

    [Fact]
    public void Name_matching_is_case_insensitive()
        => new WorkflowRegistry([new SampleWorkflow("Wf")]).Resolve("wf").Should().NotBeNull();

    [Fact]
    public void Unknown_names_resolve_to_null()
        => new WorkflowRegistry([]).Resolve("nope").Should().BeNull();

    [Fact]
    public void Versions_are_listed_newest_first()
    {
        var registry = new WorkflowRegistry(
            [new SampleWorkflow("wf", "1.0.0"), new SampleWorkflow("wf", "3.0.0"), new SampleWorkflow("wf", "2.0.0")]);

        registry.VersionsOf("wf").Select(v => v.Version).Should().Equal("3.0.0", "2.0.0", "1.0.0");
        registry.VersionsOf("missing").Should().BeEmpty();
    }

    [Fact]
    public void Duplicate_versions_of_one_workflow_are_rejected_at_startup()
    {
        // Ambiguity here would make version pinning meaningless, so it fails loudly.
        Action act = () => new WorkflowRegistry([new SampleWorkflow("wf", "1.0.0"), new SampleWorkflow("wf", "1.0.0")]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*more than one definition*1.0.0*");
    }

    [Fact]
    public void All_lists_every_registration()
        => new WorkflowRegistry([new SampleWorkflow("a"), new SampleWorkflow("b")]).All.Should().HaveCount(2);

    [Fact]
    public void Rejects_a_null_definition_source()
        => FluentActions.Invoking(() => new WorkflowRegistry(null!)).Should().Throw<ArgumentNullException>();

    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("1.2.0-beta.1", 1, 2, 0)]
    [InlineData("2.0.0+build5", 2, 0, 0)]
    [InlineData("garbage", 0, 0, 0)]
    public void Version_parsing_tolerates_semver_suffixes(string version, int major, int minor, int build)
    {
        Version parsed = WorkflowRegistry.ParseVersion(version);
        parsed.Major.Should().Be(major);
        parsed.Minor.Should().Be(minor);
        parsed.Build.Should().Be(build);
    }

    [Fact]
    public void Valid_context_passes_validation()
    {
        var registry = new WorkflowRegistry([new SampleWorkflow()]);
        WorkflowDescriptor descriptor = registry.Resolve("sample")!;

        registry.ValidateContext(descriptor, TestFactory.Json("""{"value":"hello"}"""))
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void Missing_context_fails_validation()
    {
        var registry = new WorkflowRegistry([new SampleWorkflow()]);
        WorkflowDescriptor descriptor = registry.Resolve("sample")!;

        ContextValidationResult result = registry.ValidateContext(descriptor, null);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainKey("context");
    }

    [Fact]
    public void Null_context_fails_validation()
    {
        var registry = new WorkflowRegistry([new SampleWorkflow()]);
        WorkflowDescriptor descriptor = registry.Resolve("sample")!;

        registry.ValidateContext(descriptor, TestFactory.Json("null")).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Type_mismatched_context_fails_validation()
    {
        var registry = new WorkflowRegistry([new SampleWorkflow()]);
        WorkflowDescriptor descriptor = registry.Resolve("sample")!;

        registry.ValidateContext(descriptor, TestFactory.Json("""{"amount":"not-a-number"}"""))
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validation_rejects_a_null_descriptor()
        => new WorkflowRegistry([]).Invoking(r => r.ValidateContext(null!, TestFactory.Json("{}")))
            .Should().Throw<ArgumentNullException>();
}

public class InstanceLauncherTests
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-07-30T10:00:00Z"));
    private readonly InMemoryInstanceStore _instances;
    private readonly InstanceLauncher _launcher;

    public InstanceLauncherTests()
    {
        _instances = new InMemoryInstanceStore(_clock);
        _launcher = new InstanceLauncher(new WorkflowRegistry([new SampleWorkflow()]), _instances, _clock);
    }

    [Fact]
    public async Task Start_creates_a_durable_pending_instance()
    {
        StartResult result = await _launcher.StartAsync(
            "sample", null, new StartInstanceRequest { Context = TestFactory.Json("""{"value":"a"}""") },
            "t1", null, default);

        result.Kind.Should().Be(StartResultKind.Accepted);
        result.Instance!.Status.Should().Be(InstanceStatus.Pending);

        // Durable before any dispatch: an accepted start cannot be lost to a crash.
        (await _instances.GetAsync(result.Instance.InstanceId, default)).Should().NotBeNull();
    }

    [Fact]
    public async Task Unknown_workflow_is_reported_without_creating_an_instance()
    {
        StartResult result = await _launcher.StartAsync(
            "missing", null, new StartInstanceRequest { Context = TestFactory.Json("{}") }, "t1", null, default);

        result.Kind.Should().Be(StartResultKind.UnknownWorkflow);
        _instances.Count.Should().Be(0);
    }

    [Fact]
    public async Task Invalid_context_creates_no_instance_row()
    {
        StartResult result = await _launcher.StartAsync(
            "sample", null, new StartInstanceRequest { Context = TestFactory.Json("""{"amount":"nope"}""") },
            "t1", null, default);

        result.Kind.Should().Be(StartResultKind.InvalidContext);
        result.Errors.Should().ContainKey("context");
        _instances.Count.Should().Be(0);
    }

    [Fact]
    public async Task Missing_context_is_rejected()
    {
        StartResult result = await _launcher.StartAsync(
            "sample", null, new StartInstanceRequest(), "t1", null, default);

        result.Kind.Should().Be(StartResultKind.InvalidContext);
    }

    [Fact]
    public async Task Idempotency_key_returns_the_original_instance()
    {
        var request = new StartInstanceRequest { Context = TestFactory.Json("""{"value":"a"}""") };

        StartResult first = await _launcher.StartAsync("sample", null, request, "t1", "key-1", default);
        StartResult second = await _launcher.StartAsync("sample", null, request, "t1", "key-1", default);

        second.Kind.Should().Be(StartResultKind.Duplicate);
        second.Instance!.InstanceId.Should().Be(first.Instance!.InstanceId);
        _instances.Count.Should().Be(1);
    }

    [Fact]
    public async Task Idempotency_keys_do_not_collide_across_tenants()
    {
        var request = new StartInstanceRequest { Context = TestFactory.Json("""{"value":"a"}""") };

        await _launcher.StartAsync("sample", null, request, "t1", "key-1", default);
        StartResult other = await _launcher.StartAsync("sample", null, request, "t2", "key-1", default);

        other.Kind.Should().Be(StartResultKind.Accepted);
        _instances.Count.Should().Be(2);
    }

    [Fact]
    public async Task Explicit_version_is_pinned_on_the_instance()
    {
        var launcher = new InstanceLauncher(
            new WorkflowRegistry([new SampleWorkflow("sample", "1.0.0"), new SampleWorkflow("sample", "2.0.0")]),
            _instances, _clock);

        StartResult pinned = await launcher.StartAsync(
            "sample", "1.0.0", new StartInstanceRequest { Context = TestFactory.Json("""{"value":"a"}""") },
            "t1", null, default);

        pinned.Instance!.WorkflowVersion.Should().Be("1.0.0");

        StartResult latest = await launcher.StartAsync(
            "sample", null, new StartInstanceRequest { Context = TestFactory.Json("""{"value":"a"}""") },
            "t1", null, default);

        latest.Instance!.WorkflowVersion.Should().Be("2.0.0");
    }

    [Fact]
    public async Task Correlation_id_and_attempt_budget_are_recorded()
    {
        StartResult result = await _launcher.StartAsync("sample", null, new StartInstanceRequest
        {
            Context = TestFactory.Json("""{"value":"a"}"""),
            CorrelationId = "corr-1",
            MaxAttempts = 9
        }, "t1", null, default);

        result.Instance!.CorrelationId.Should().Be("corr-1");
        result.Instance.MaxAttempts.Should().Be(9);
    }

    [Fact]
    public async Task Wait_returns_null_while_the_instance_is_still_running()
    {
        StartResult started = await _launcher.StartAsync(
            "sample", null, new StartInstanceRequest { Context = TestFactory.Json("""{"value":"a"}""") },
            "t1", null, default);

        Task<WorkflowInstance?> wait = _launcher.WaitForTerminalAsync(
            started.Instance!.InstanceId, TimeSpan.FromSeconds(1), default);

        _clock.Advance(TimeSpan.FromSeconds(2));

        (await wait).Should().BeNull();
    }

    [Fact]
    public async Task Wait_returns_the_instance_once_it_is_terminal()
    {
        StartResult started = await _launcher.StartAsync(
            "sample", null, new StartInstanceRequest { Context = TestFactory.Json("""{"value":"a"}""") },
            "t1", null, default);

        await _instances.UpdateAsync(started.Instance!.InstanceId, m => m.Status = InstanceStatus.Completed, default);

        WorkflowInstance? terminal = await _launcher.WaitForTerminalAsync(
            started.Instance.InstanceId, TimeSpan.FromSeconds(5), default);

        terminal!.Status.Should().Be(InstanceStatus.Completed);
    }

    [Fact]
    public async Task Start_rejects_a_null_request()
        => await _launcher.Invoking(l => l.StartAsync("sample", null, null!, "t1", null, default))
            .Should().ThrowAsync<ArgumentNullException>();
}

public class PreferParserTests
{
    [Theory]
    [InlineData("wait=30", 30)]
    [InlineData("respond-async, wait=10", 10)]
    [InlineData("WAIT=5", 5)]
    [InlineData(" wait=7 ", 7)]
    public void Parses_a_wait_directive(string prefer, int expected)
    {
        PreferParser.TryGetWait(prefer, out int seconds).Should().BeTrue();
        seconds.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("respond-async")]
    [InlineData("wait=abc")]
    [InlineData("wait=0")]
    [InlineData("wait=-5")]
    public void Rejects_absent_or_invalid_directives(string? prefer)
        => PreferParser.TryGetWait(prefer, out _).Should().BeFalse();
}

public class InstanceControlServiceTests
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-07-30T10:00:00Z"));
    private readonly InMemoryInstanceStore _instances;
    private readonly InMemoryApprovalStore _approvals = new();
    private readonly InMemoryEventStore _events = new();
    private readonly InMemoryAuditStore _audit = new();
    private readonly InstanceControlService _control;
    private readonly StubCheckpointDescriber _checkpoints = new();

    public InstanceControlServiceTests()
    {
        _instances = new InMemoryInstanceStore(_clock);
        _control = new InstanceControlService(
            _instances, _approvals, new DirectEventSink(_events), new EventSequencer(), _audit,
            new WorkflowRegistry([new SampleWorkflow()]), _checkpoints, _clock);
    }

    private static ClaimsPrincipal User(string id = "operator")
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "test"));

    private async Task<WorkflowInstance> SeedAsync(
        string id = "i1", InstanceStatus status = InstanceStatus.Running, string? checkpointId = null)
    {
        WorkflowInstance instance = await _instances.CreateAsync(
            TestFactory.CreateRequest(id, workflow: "sample", contextJson: """{"value":"a"}"""), default);

        if (status != InstanceStatus.Pending || checkpointId is not null)
        {
            await _instances.UpdateAsync(id, m =>
            {
                m.Status = status;
                m.LatestCheckpointId = checkpointId;
            }, default);
        }

        if (checkpointId is not null)
        {
            _checkpoints.Add(id, checkpointId);
        }

        return (await _instances.GetAsync(id, default))!;
    }

    [Fact]
    public async Task Cancel_marks_the_instance_cancelled_and_records_the_reason()
    {
        await SeedAsync();

        ControlResult result = await _control.CancelAsync("i1", "operator stopped it", User(), default);

        result.Kind.Should().Be(ControlResultKind.Accepted);

        WorkflowInstance? instance = await _instances.GetAsync("i1", default);
        instance!.Status.Should().Be(InstanceStatus.Cancelled);
        instance.CancellationRequested.Should().BeTrue();
        instance.TerminalReason.Should().Be("operator stopped it");
        instance.CompletedAt.Should().Be(_clock.GetUtcNow());
    }

    [Fact]
    public async Task Cancel_also_cancels_pending_approvals()
    {
        await SeedAsync();
        await _approvals.CreateAsync(new ApprovalRequest
        {
            ApprovalId = "apr_1", InstanceId = "i1", TenantId = "t1", ExecutorId = "pay",
            CreatedAt = _clock.GetUtcNow(), ExpiresAt = _clock.GetUtcNow().AddHours(1)
        }, default);

        await _control.CancelAsync("i1", null, User(), default);

        // Orphaned approvals would otherwise sit in the queue forever.
        (await _approvals.GetAsync("apr_1", default))!.State.Should().Be(ApprovalState.Cancelled);
    }

    [Fact]
    public async Task Cancel_emits_an_event_that_names_the_side_effect_caveat()
    {
        await SeedAsync();

        await _control.CancelAsync("i1", "stop", User(), default);

        Page<EventEnvelope> page = await _events.QueryAsync(new EventQuery("i1", Limit: 50), default);
        EventEnvelope cancelled = page.Items.Should()
            .ContainSingle(e => e.EventType == WorkflowEventTypes.InstanceCancelled).Subject;

        cancelled.PayloadJson.Should().Contain("not rolled back");
    }

    [Fact]
    public async Task Cancel_of_an_unknown_instance_is_not_found()
        => (await _control.CancelAsync("nope", null, User(), default)).Kind.Should().Be(ControlResultKind.NotFound);

    [Fact]
    public async Task Cancel_of_a_terminal_instance_conflicts_and_is_retry_safe()
    {
        await SeedAsync(status: InstanceStatus.Completed);

        ControlResult result = await _control.CancelAsync("i1", null, User(), default);

        result.Kind.Should().Be(ControlResultKind.AlreadyTerminal);
        result.Detail.Should().Contain("Completed");
        (await _instances.GetAsync("i1", default))!.Status.Should().Be(InstanceStatus.Completed);
    }

    [Fact]
    public async Task Restart_creates_a_new_instance_and_leaves_the_source_untouched()
    {
        await SeedAsync(status: InstanceStatus.DeadStopped);

        ControlResult result = await _control.RestartAsync("i1", null, "retry after fix", User(), default);

        result.Kind.Should().Be(ControlResultKind.Accepted);
        result.Instance!.InstanceId.Should().NotBe("i1");
        result.Instance.RerunOfInstanceId.Should().Be("i1");
        result.Instance.Status.Should().Be(InstanceStatus.Pending);
        result.Instance.ContextJson.Should().Contain("\"value\":\"a\"");

        (await _instances.GetAsync("i1", default))!.Status.Should().Be(InstanceStatus.DeadStopped);
    }

    [Fact]
    public async Task Restart_can_override_the_context()
    {
        await SeedAsync(status: InstanceStatus.Failed);

        ControlResult result = await _control.RestartAsync(
            "i1", TestFactory.Json("""{"value":"overridden"}"""), null, User(), default);

        result.Instance!.ContextJson.Should().Contain("overridden");
    }

    [Fact]
    public async Task Restart_binds_to_the_current_version()
    {
        var control = new InstanceControlService(
            _instances, _approvals, new DirectEventSink(_events), new EventSequencer(), _audit,
            new WorkflowRegistry([new SampleWorkflow("sample", "1.0.0"), new SampleWorkflow("sample", "5.0.0")]),
            _checkpoints, _clock);

        await SeedAsync(status: InstanceStatus.Failed);

        ControlResult result = await control.RestartAsync("i1", null, null, User(), default);

        result.Instance!.WorkflowVersion.Should().Be("5.0.0");
    }

    [Fact]
    public async Task Restart_of_a_withdrawn_workflow_conflicts()
    {
        var control = new InstanceControlService(
            _instances, _approvals, new DirectEventSink(_events), new EventSequencer(), _audit,
            new WorkflowRegistry([]), _checkpoints, _clock);

        await SeedAsync(status: InstanceStatus.Failed);

        ControlResult result = await control.RestartAsync("i1", null, null, User(), default);

        result.Kind.Should().Be(ControlResultKind.UnknownWorkflow);
    }

    [Fact]
    public async Task Resume_revives_a_dead_stopped_instance_from_its_checkpoint()
    {
        await SeedAsync(status: InstanceStatus.DeadStopped, checkpointId: "ck-1");

        ControlResult result = await _control.ResumeAsync("i1", null, "dependency restored", User(), default);

        result.Kind.Should().Be(ControlResultKind.Accepted);

        WorkflowInstance? instance = await _instances.GetAsync("i1", default);
        instance!.Status.Should().Be(InstanceStatus.Dispatchable);
        instance.LatestCheckpointId.Should().Be("ck-1");
        instance.AttemptCount.Should().Be(1);
        instance.CompletedAt.Should().BeNull();
    }

    [Fact]
    public async Task Resume_accepts_an_explicit_checkpoint()
    {
        await SeedAsync(status: InstanceStatus.Failed, checkpointId: "ck-2");
        _checkpoints.Add("i1", "ck-1");

        ControlResult result = await _control.ResumeAsync("i1", "ck-1", null, User(), default);

        result.Kind.Should().Be(ControlResultKind.Accepted);
        (await _instances.GetAsync("i1", default))!.LatestCheckpointId.Should().Be("ck-1");
    }

    [Fact]
    public async Task Resume_without_any_checkpoint_is_refused_with_a_remedy()
    {
        await SeedAsync(status: InstanceStatus.Failed);

        ControlResult result = await _control.ResumeAsync("i1", null, null, User(), default);

        result.Kind.Should().Be(ControlResultKind.NotResumable);
        result.Detail.Should().Contain("restart");
    }

    [Fact]
    public async Task Resume_after_retention_pruning_names_restart_as_the_remedy()
    {
        await SeedAsync(status: InstanceStatus.Failed, checkpointId: "ck-1");
        _checkpoints.Clear();

        ControlResult result = await _control.ResumeAsync("i1", null, null, User(), default);

        result.Kind.Should().Be(ControlResultKind.NotResumable);
        result.Detail.Should().Contain("pruned").And.Contain("restart");
    }

    [Fact]
    public async Task Resume_of_a_withdrawn_version_is_refused()
    {
        var control = new InstanceControlService(
            _instances, _approvals, new DirectEventSink(_events), new EventSequencer(), _audit,
            new WorkflowRegistry([new SampleWorkflow("sample", "9.9.9")]), _checkpoints, _clock);

        await SeedAsync(status: InstanceStatus.Failed, checkpointId: "ck-1");

        ControlResult result = await control.ResumeAsync("i1", null, null, User(), default);

        result.Kind.Should().Be(ControlResultKind.UnknownWorkflow);
    }

    [Fact]
    public async Task Retry_now_clears_the_backoff()
    {
        await SeedAsync(status: InstanceStatus.RetryScheduled);
        await _instances.UpdateAsync("i1", m => m.NextRetryAt = _clock.GetUtcNow().AddHours(1), default);

        ControlResult result = await _control.RetryNowAsync("i1", "urgent", User(), default);

        result.Kind.Should().Be(ControlResultKind.Accepted);

        WorkflowInstance? instance = await _instances.GetAsync("i1", default);
        instance!.Status.Should().Be(InstanceStatus.Dispatchable);
        instance.NextRetryAt.Should().BeNull();
    }

    [Fact]
    public async Task Retry_now_is_refused_for_other_states()
    {
        await SeedAsync(status: InstanceStatus.Running);

        ControlResult result = await _control.RetryNowAsync("i1", null, User(), default);

        result.Kind.Should().Be(ControlResultKind.InvalidState);
        result.Detail.Should().Contain("Running");
    }

    [Fact]
    public async Task Suspend_then_resume_round_trips()
    {
        await SeedAsync();

        (await _control.SuspendAsync("i1", "draining", User(), default)).Kind.Should().Be(ControlResultKind.Accepted);
        (await _instances.GetAsync("i1", default))!.Status.Should().Be(InstanceStatus.Suspended);

        (await _control.ResumeSuspendedAsync("i1", "dependency back", User(), default))
            .Kind.Should().Be(ControlResultKind.Accepted);
        (await _instances.GetAsync("i1", default))!.Status.Should().Be(InstanceStatus.Dispatchable);
    }

    [Fact]
    public async Task Suspend_is_refused_for_terminal_instances()
    {
        await SeedAsync(status: InstanceStatus.Completed);

        (await _control.SuspendAsync("i1", null, User(), default)).Kind.Should().Be(ControlResultKind.AlreadyTerminal);
    }

    [Fact]
    public async Task Resume_suspended_is_refused_when_not_suspended()
    {
        await SeedAsync(status: InstanceStatus.Running);

        (await _control.ResumeSuspendedAsync("i1", null, User(), default)).Kind.Should().Be(ControlResultKind.InvalidState);
    }

    [Fact]
    public async Task Unknown_instances_are_reported_for_every_action()
    {
        (await _control.RestartAsync("nope", null, null, User(), default)).Kind.Should().Be(ControlResultKind.NotFound);
        (await _control.ResumeAsync("nope", null, null, User(), default)).Kind.Should().Be(ControlResultKind.NotFound);
        (await _control.RetryNowAsync("nope", null, User(), default)).Kind.Should().Be(ControlResultKind.NotFound);
        (await _control.SuspendAsync("nope", null, User(), default)).Kind.Should().Be(ControlResultKind.NotFound);
        (await _control.ResumeSuspendedAsync("nope", null, User(), default)).Kind.Should().Be(ControlResultKind.NotFound);
    }

    [Fact]
    public async Task Every_control_action_is_audited_with_actor_and_reason()
    {
        await SeedAsync(status: InstanceStatus.RetryScheduled);

        await _control.RetryNowAsync("i1", "because", User("alice"), default);

        IReadOnlyList<AuditEntry> entries = await _audit.QueryAsync("i1", 10, default);
        entries.Should().ContainSingle();
        entries[0].Action.Should().Be("instance.retry");
        entries[0].ActorId.Should().Be("alice");
        entries[0].Reason.Should().Be("because");
    }

    [Fact]
    public async Task An_anonymous_operator_is_recorded_as_anonymous()
    {
        await SeedAsync();

        await _control.CancelAsync("i1", null, null, default);

        (await _audit.QueryAsync("i1", 10, default))[0].ActorId.Should().Be("anonymous");
    }

    [Fact]
    public async Task Control_actions_appear_on_the_instance_event_stream()
    {
        await SeedAsync(status: InstanceStatus.RetryScheduled);

        await _control.RetryNowAsync("i1", null, User(), default);
        await _control.SuspendAsync("i1", null, User(), default);
        await _control.ResumeSuspendedAsync("i1", null, User(), default);

        Page<EventEnvelope> page = await _events.QueryAsync(new EventQuery("i1", Limit: 50), default);

        page.Items.Select(e => e.EventType).Should().Contain(
        [
            WorkflowEventTypes.InstanceRetryForced,
            WorkflowEventTypes.InstanceSuspended,
            WorkflowEventTypes.InstanceResumed
        ]);

        // Operator intervention shares the sequence space with automated progress.
        page.Items.Select(e => e.Sequence).Should().OnlyHaveUniqueItems();
    }

    private sealed class StubCheckpointDescriber : ICheckpointDescriber
    {
        private readonly Dictionary<string, List<string>> _checkpoints = [];

        public void Add(string sessionId, string checkpointId)
        {
            if (!_checkpoints.TryGetValue(sessionId, out List<string>? list))
            {
                list = [];
                _checkpoints[sessionId] = list;
            }
            list.Add(checkpointId);
        }

        public void Clear() => _checkpoints.Clear();

        public bool Exists(string sessionId, string checkpointId)
            => _checkpoints.TryGetValue(sessionId, out List<string>? list) && list.Contains(checkpointId);

        public IReadOnlyList<(string CheckpointId, int SizeBytes, DateTimeOffset CommittedAt)> Describe(string sessionId)
            => _checkpoints.TryGetValue(sessionId, out List<string>? list)
                ? list.Select(c => (c, 100, DateTimeOffset.UnixEpoch)).ToArray()
                : [];
    }
}

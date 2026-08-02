using Abacus.Run.Abstractions;
using Abacus.Run.Api;
using Abacus.Run.Core;
using Abacus.Run.Persistence;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Xunit;

namespace Abacus.Run.UnitTests;

/// <summary>
/// Three nodes: one plain (autonomous by default), one the author gated but left open to tenants,
/// one the author gated and locked.
/// </summary>
public sealed class ConfigurableWorkflow : IWorkflowDefinition<SampleContext, SampleResult>
{
    public ConfigurableWorkflow(string name = "configurable", string version = "1.0.0")
    {
        Name = name;
        Version = version;
    }

    public string Name { get; }
    public string Version { get; }

    public int Builds { get; private set; }

    public ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken cancellationToken)
    {
        Builds++;

        ExecutorBinding validate = context.Node(new PassThrough("validate"));

        ExecutorBinding notify = context.Node(new PassThrough("notify"), gate => gate
            .Mode(ExecutionMode.RequireApproval)
            .Reason("NotifiesCustomers")
            .AssignTo("group:ops")
            .ExpiresAfter(TimeSpan.FromHours(4)));

        ExecutorBinding pay = context.Node(new Terminal("pay"), gate => gate
            .When<SampleContext>(c => c.Amount > 25_000m)
            .Reason("AmountAboveThreshold")
            .RequireApprovers(2)
            .RequireSegregationOfDuties()
            .Locked());

        return new ValueTask<Workflow>(new WorkflowBuilder(validate)
            .AddEdge(validate, notify)
            .AddEdge(notify, pay)
            .WithOutputFrom(pay)
            .WithName(Name)
            .Build());
    }

    private sealed class PassThrough : HostExecutor<SampleContext, SampleContext>
    {
        public PassThrough(string id) : base(id) { }

        protected override ValueTask<SampleContext> ExecuteCoreAsync(
            SampleContext input, IWorkflowContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(input);
    }

    private sealed class Terminal : HostExecutor<SampleContext, SampleResult>
    {
        public Terminal(string id) : base(id) { }

        protected override ValueTask<SampleResult> ExecuteCoreAsync(
            SampleContext input, IWorkflowContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(new SampleResult(input.Value));
    }
}

public class WorkflowInspectorTests
{
    [Fact]
    public async Task Lists_every_declared_node_with_its_types()
    {
        IReadOnlyList<WorkflowNodeDescriptor> nodes = await Inspect(new ConfigurableWorkflow());

        nodes.Select(n => n.ExecutorId).Should().Equal("validate", "notify", "pay");
        nodes[0].InputType.Should().Be(nameof(SampleContext));
        nodes[2].OutputType.Should().Be(nameof(SampleResult));
        nodes.Should().OnlyContain(n => n.Configurable);
    }

    [Fact]
    public async Task A_node_declared_without_a_gate_is_autonomous()
    {
        IReadOnlyList<WorkflowNodeDescriptor> nodes = await Inspect(new ConfigurableWorkflow());

        nodes.Single(n => n.ExecutorId == "validate").DeclaredGate.Mode
            .Should().Be(ExecutionMode.Autonomous, "autonomous is the host default for an ungated node");
    }

    [Fact]
    public async Task Declared_gates_are_reported_verbatim()
    {
        IReadOnlyList<WorkflowNodeDescriptor> nodes = await Inspect(new ConfigurableWorkflow());

        WorkflowNodeDescriptor notify = nodes.Single(n => n.ExecutorId == "notify");
        notify.DeclaredGate.Mode.Should().Be(ExecutionMode.RequireApproval);
        notify.DeclaredGate.Assignees.Should().Equal("group:ops");
        notify.DeclaredGate.Expiry.Should().Be(TimeSpan.FromHours(4));
        notify.DeclaredGate.Locked.Should().BeFalse();

        WorkflowNodeDescriptor pay = nodes.Single(n => n.ExecutorId == "pay");
        pay.DeclaredGate.Mode.Should().Be(ExecutionMode.Conditional);
        pay.DeclaredGate.Locked.Should().BeTrue();
    }

    [Fact]
    public async Task A_version_is_built_only_once()
    {
        var definition = new ConfigurableWorkflow();
        var inspector = new WorkflowInspector();
        var descriptor = new WorkflowDescriptor(definition);

        await inspector.InspectAsync(descriptor, default);
        await inspector.InspectAsync(descriptor, default);

        definition.Builds.Should().Be(1, "the graph of a registered version cannot change");
    }

    [Fact]
    public async Task Raw_nodes_are_listed_but_not_configurable()
    {
        IReadOnlyList<WorkflowNodeDescriptor> nodes = await Inspect(new RawNodeWorkflow());

        nodes.Single(n => n.ExecutorId == "raw").Configurable
            .Should().BeFalse("a raw binding runs outside the executor pipeline and cannot be gated");
        nodes.Single(n => n.ExecutorId == "start").Configurable.Should().BeTrue();
    }

    private static async Task<IReadOnlyList<WorkflowNodeDescriptor>> Inspect(IWorkflowDefinition definition)
        => await new WorkflowInspector().InspectAsync(new WorkflowDescriptor(definition), default);

    private sealed class RawNodeWorkflow : IWorkflowDefinition<SampleContext, SampleResult>
    {
        public string Name => "raw-workflow";
        public string Version => "1.0.0";

        public ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken cancellationToken)
        {
            ExecutorBinding start = context.Node(new Start("start"));
            ExecutorBinding raw = context.RawNode(new Raw("raw"));

            return new ValueTask<Workflow>(new WorkflowBuilder(start)
                .AddEdge(start, raw)
                .WithOutputFrom(raw)
                .Build());
        }

        private sealed class Start : HostExecutor<SampleContext, SampleContext>
        {
            public Start(string id) : base(id) { }

            protected override ValueTask<SampleContext> ExecuteCoreAsync(
                SampleContext input, IWorkflowContext context, CancellationToken cancellationToken)
                => ValueTask.FromResult(input);
        }

        private sealed class Raw : Executor<SampleContext, SampleResult>
        {
            public Raw(string id) : base(id) { }

            public override ValueTask<SampleResult> HandleAsync(
                SampleContext message, IWorkflowContext context, CancellationToken cancellationToken = default)
                => ValueTask.FromResult(new SampleResult(message.Value));
        }
    }
}

public class GatePolicyRulesTests
{
    [Fact]
    public void An_unlocked_declaration_accepts_anything()
    {
        var declared = new ApprovalGate { Mode = ExecutionMode.RequireApproval, RequiredApprovers = 3 };

        GatePolicyRules.Violations(declared, ApprovalGate.Autonomous).Should().BeEmpty();
        GatePolicyRules.Reconcile(declared, ApprovalGate.Autonomous).Mode.Should().Be(ExecutionMode.Autonomous);
    }

    [Theory]
    [InlineData(ExecutionMode.RequireApproval, ExecutionMode.Autonomous, true)]
    [InlineData(ExecutionMode.Conditional, ExecutionMode.Autonomous, true)]
    [InlineData(ExecutionMode.Autonomous, ExecutionMode.RequireApproval, false)]
    [InlineData(ExecutionMode.Conditional, ExecutionMode.RequireApproval, false)]
    [InlineData(ExecutionMode.RequireApproval, ExecutionMode.RequireApproval, false)]
    public void Mode_changes_against_a_locked_declaration(ExecutionMode declared, ExecutionMode candidate, bool loosens)
    {
        IReadOnlyList<string> violations = GatePolicyRules.Violations(
            new ApprovalGate { Mode = declared, Locked = true }, new ApprovalGate { Mode = candidate });

        violations.Any().Should().Be(loosens);
    }

    [Fact]
    public void Every_weakened_field_of_a_locked_gate_is_reported()
    {
        var declared = new ApprovalGate
        {
            Mode = ExecutionMode.RequireApproval,
            RequiredApprovers = 2,
            AllowModification = false,
            RequireSegregationOfDuties = true,
            OnExpiry = ExpiryAction.DeadStop,
            Locked = true
        };

        var candidate = new ApprovalGate
        {
            Mode = ExecutionMode.Autonomous,
            RequiredApprovers = 1,
            AllowModification = true,
            RequireSegregationOfDuties = false,
            OnExpiry = ExpiryAction.AutoApprove
        };

        GatePolicyRules.Violations(declared, candidate).Should().HaveCount(5);
    }

    [Fact]
    public void Reconcile_pulls_every_weakened_field_back_to_the_locked_declaration()
    {
        var declared = new ApprovalGate
        {
            Mode = ExecutionMode.RequireApproval,
            RequiredApprovers = 2,
            RequireSegregationOfDuties = true,
            OnExpiry = ExpiryAction.DeadStop,
            Locked = true
        };

        ApprovalGate effective = GatePolicyRules.Reconcile(declared, new ApprovalGate
        {
            Mode = ExecutionMode.Autonomous,
            Reason = "tenant reason",
            RequiredApprovers = 1,
            AllowModification = true,
            RequireSegregationOfDuties = false,
            OnExpiry = ExpiryAction.AutoApprove
        });

        effective.Mode.Should().Be(ExecutionMode.RequireApproval);
        effective.RequiredApprovers.Should().Be(2);
        effective.AllowModification.Should().BeFalse();
        effective.RequireSegregationOfDuties.Should().BeTrue();
        effective.OnExpiry.Should().Be(ExpiryAction.DeadStop);
        effective.Reason.Should().Be("tenant reason", "fields that do not weaken the gate still belong to the tenant");
    }

    [Fact]
    public void Reconcile_keeps_a_tightening_change_on_a_locked_gate()
    {
        var declared = new ApprovalGate { Mode = ExecutionMode.RequireApproval, RequiredApprovers = 1, Locked = true };

        ApprovalGate effective = GatePolicyRules.Reconcile(
            declared, new ApprovalGate { Mode = ExecutionMode.RequireApproval, RequiredApprovers = 4 });

        effective.RequiredApprovers.Should().Be(4);
    }
}

public class GateConfigurationServiceTests
{
    private const string Workflow = "configurable";
    private const string Version = "1.0.0";

    [Fact]
    public async Task Nodes_default_to_autonomous_with_no_tenant_configuration()
    {
        (IGateConfigurationService service, _) = Build();

        GateConfigResult result = await service.GetNodesAsync(Workflow, Version, "t1", default);

        result.Kind.Should().Be(GateConfigResultKind.Ok);
        ExecutorNodeDto validate = Node(result, "validate");
        validate.Effective.Mode.Should().Be("autonomous");
        validate.EffectiveSource.Should().Be(GateSources.Definition);
        validate.TenantOverride.Should().BeNull();
        result.Nodes!.Nodes.Should().OnlyContain(n => n.TenantOverride == null);
    }

    [Fact]
    public async Task Unknown_workflow_and_version_are_not_found()
    {
        (IGateConfigurationService service, _) = Build();

        (await service.GetNodesAsync("nope", Version, "t1", default))
            .Kind.Should().Be(GateConfigResultKind.UnknownWorkflow);
        (await service.GetNodesAsync(Workflow, "9.9.9", "t1", default))
            .Kind.Should().Be(GateConfigResultKind.UnknownWorkflow);
    }

    [Fact]
    public async Task Version_latest_resolves_the_highest_registered_version()
    {
        var registry = new WorkflowRegistry([new ConfigurableWorkflow(), new ConfigurableWorkflow(version: "2.0.0")]);
        var service = new GateConfigurationService(registry, new WorkflowInspector(), new InMemoryGatePolicyStore());

        (await service.GetNodesAsync(Workflow, "latest", "t1", default)).Nodes!.WorkflowVersion.Should().Be("2.0.0");
    }

    [Fact]
    public async Task Configuring_a_node_makes_it_require_approval_for_that_tenant_only()
    {
        (IGateConfigurationService service, _) = Build();

        GateConfigResult set = await service.SetNodesAsync(Workflow, Version, "t1",
            Policy("validate", new ExecutionPolicyDto("requireApproval", Reason: "tenant policy")), null, default);

        set.Kind.Should().Be(GateConfigResultKind.Ok);
        ExecutorNodeDto configured = Node(set, "validate");
        configured.Effective.Mode.Should().Be("requireApproval");
        configured.Effective.Reason.Should().Be("tenant policy");
        configured.EffectiveSource.Should().Be(GateSources.Tenant);
        configured.Declared.Mode.Should().Be("autonomous", "the declaration is unchanged");

        GateConfigResult other = await service.GetNodesAsync(Workflow, Version, "t2", default);
        Node(other, "validate").Effective.Mode.Should().Be("autonomous");
        Node(other, "validate").EffectiveSource.Should().Be(GateSources.Definition);
    }

    [Fact]
    public async Task A_tenant_may_turn_off_an_unlocked_declared_gate()
    {
        (IGateConfigurationService service, _) = Build();

        GateConfigResult set = await service.SetNodesAsync(Workflow, Version, "t1",
            Policy("notify", new ExecutionPolicyDto("autonomous")), null, default);

        set.Kind.Should().Be(GateConfigResultKind.Ok);
        Node(set, "notify").Effective.Mode.Should().Be("autonomous");
    }

    [Fact]
    public async Task Unspecified_fields_inherit_the_declared_gate()
    {
        (IGateConfigurationService service, _) = Build();

        GateConfigResult set = await service.SetNodesAsync(Workflow, Version, "t1",
            Policy("notify", new ExecutionPolicyDto("requireApproval", RequiredApprovers: 3)), null, default);

        ExecutionPolicyDto effective = Node(set, "notify").Effective;
        effective.RequiredApprovers.Should().Be(3);
        effective.Assignees.Should().ContainSingle()
            .Which.Should().Be("group:ops", "the tenant did not restate the assignees");
        effective.ExpirySeconds.Should().Be((int)TimeSpan.FromHours(4).TotalSeconds);
        effective.Reason.Should().Be("NotifiesCustomers");
    }

    [Fact]
    public async Task Loosening_a_locked_node_is_rejected_and_persists_nothing()
    {
        (IGateConfigurationService service, InMemoryGatePolicyStore policies) = Build();

        GateConfigResult result = await service.SetNodesAsync(Workflow, Version, "t1",
            Policy("pay", new ExecutionPolicyDto("autonomous")), null, default);

        result.Kind.Should().Be(GateConfigResultKind.Rejected);
        result.Detail.Should().Contain("locked");
        (await policies.ListAsync("t1", Workflow, Version, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task Tightening_a_locked_node_is_allowed()
    {
        (IGateConfigurationService service, _) = Build();

        GateConfigResult result = await service.SetNodesAsync(Workflow, Version, "t1",
            Policy("pay", new ExecutionPolicyDto("requireApproval", RequiredApprovers: 3)), null, default);

        result.Kind.Should().Be(GateConfigResultKind.Ok);
        ExecutionPolicyDto effective = Node(result, "pay").Effective;
        effective.Mode.Should().Be("requireApproval");
        effective.RequiredApprovers.Should().Be(3);
        effective.RequireSegregationOfDuties.Should().BeTrue("the locked declaration required it");
    }

    [Fact]
    public async Task A_locked_node_is_flagged_so_a_ui_can_disable_the_control()
    {
        (IGateConfigurationService service, _) = Build();

        GateConfigResult result = await service.GetNodesAsync(Workflow, Version, "t1", default);

        Node(result, "pay").Locked.Should().BeTrue();
        Node(result, "notify").Locked.Should().BeFalse();
    }

    [Fact]
    public async Task An_unknown_executor_is_reported_without_touching_the_valid_ones()
    {
        (IGateConfigurationService service, InMemoryGatePolicyStore policies) = Build();

        GateConfigResult result = await service.SetNodesAsync(Workflow, Version, "t1", new Dictionary<string, ExecutionPolicyDto>
        {
            ["validate"] = new("requireApproval"),
            ["ghost"] = new("requireApproval")
        }, null, default);

        result.Kind.Should().Be(GateConfigResultKind.UnknownExecutor);
        (await policies.ListAsync("t1", Workflow, Version, default))
            .Should().BeEmpty("a bulk write is all-or-nothing");
    }

    [Fact]
    public async Task A_raw_node_cannot_be_configured()
    {
        var registry = new WorkflowRegistry([new RawOnlyWorkflow()]);
        var service = new GateConfigurationService(registry, new WorkflowInspector(), new InMemoryGatePolicyStore());

        GateConfigResult result = await service.SetNodesAsync("raw-only", "1.0.0", "t1",
            Policy("raw", new ExecutionPolicyDto("requireApproval")), null, default);

        result.Kind.Should().Be(GateConfigResultKind.NotConfigurable);
    }

    [Theory]
    [InlineData("conditional")]
    [InlineData("")]
    [InlineData("whatever")]
    public async Task Only_autonomous_and_require_approval_are_accepted(string mode)
    {
        (IGateConfigurationService service, _) = Build();

        GateConfigResult result = await service.SetNodesAsync(Workflow, Version, "t1",
            Policy("validate", new ExecutionPolicyDto(mode)), null, default);

        result.Kind.Should().Be(GateConfigResultKind.Invalid);
        result.Errors!["validate"].Should().ContainMatch("*autonomous*");
    }

    [Fact]
    public async Task Out_of_range_values_are_reported_together()
    {
        (IGateConfigurationService service, _) = Build();

        GateConfigResult result = await service.SetNodesAsync(Workflow, Version, "t1",
            Policy("validate", new ExecutionPolicyDto(
                "requireApproval", RequiredApprovers: 0, ExpirySeconds: 0, OnExpiry: "sometime")),
            null, default);

        result.Kind.Should().Be(GateConfigResultKind.Invalid);
        result.Errors!["validate"].Should().HaveCount(3);
    }

    [Fact]
    public async Task An_empty_request_is_rejected()
    {
        (IGateConfigurationService service, _) = Build();

        GateConfigResult result = await service.SetNodesAsync(
            Workflow, Version, "t1", new Dictionary<string, ExecutionPolicyDto>(), null, default);

        result.Kind.Should().Be(GateConfigResultKind.Invalid);
        result.Errors.Should().ContainKey("nodes");
    }

    [Fact]
    public async Task Reset_returns_the_node_to_its_declared_gate()
    {
        (IGateConfigurationService service, _) = Build();

        await service.SetNodesAsync(Workflow, Version, "t1",
            Policy("notify", new ExecutionPolicyDto("autonomous")), null, default);

        GateConfigResult reset = await service.ResetNodeAsync(Workflow, Version, "t1", "notify", null, default);

        reset.Kind.Should().Be(GateConfigResultKind.Ok);
        ExecutorNodeDto notify = Node(reset, "notify");
        notify.TenantOverride.Should().BeNull();
        notify.Effective.Mode.Should().Be("requireApproval");
        notify.EffectiveSource.Should().Be(GateSources.Definition);
    }

    [Fact]
    public async Task Reset_of_an_unknown_executor_is_reported()
    {
        (IGateConfigurationService service, _) = Build();

        (await service.ResetNodeAsync(Workflow, Version, "t1", "ghost", null, default))
            .Kind.Should().Be(GateConfigResultKind.UnknownExecutor);
    }

    [Fact]
    public async Task A_host_wide_policy_applies_until_the_tenant_sets_its_own()
    {
        (IGateConfigurationService service, InMemoryGatePolicyStore policies) = Build();

        await policies.SetAsync(null, Workflow, Version, "validate",
            new ApprovalGate { Mode = ExecutionMode.RequireApproval, Reason = "host wide" }, default);

        ExecutorNodeDto beforeTenantConfig = Node(await service.GetNodesAsync(Workflow, Version, "t1", default), "validate");
        beforeTenantConfig.Effective.Mode.Should().Be("requireApproval");
        beforeTenantConfig.EffectiveSource.Should().Be(GateSources.Host);
        beforeTenantConfig.TenantOverride.Should().BeNull();

        GateConfigResult set = await service.SetNodesAsync(Workflow, Version, "t1",
            Policy("validate", new ExecutionPolicyDto("autonomous")), null, default);

        Node(set, "validate").Effective.Mode.Should().Be("autonomous");
        Node(set, "validate").EffectiveSource.Should().Be(GateSources.Tenant);
    }

    [Fact]
    public async Task Configuration_is_scoped_to_one_workflow_version()
    {
        var registry = new WorkflowRegistry([new ConfigurableWorkflow(), new ConfigurableWorkflow(version: "2.0.0")]);
        var service = new GateConfigurationService(registry, new WorkflowInspector(), new InMemoryGatePolicyStore());

        await service.SetNodesAsync(Workflow, "1.0.0", "t1",
            Policy("validate", new ExecutionPolicyDto("requireApproval")), null, default);

        Node(await service.GetNodesAsync(Workflow, "2.0.0", "t1", default), "validate")
            .Effective.Mode.Should().Be("autonomous", "a policy belongs to the version it was written against");
    }

    [Fact]
    public async Task Writes_are_audited()
    {
        var audit = new InMemoryAuditStore();
        var registry = new WorkflowRegistry([new ConfigurableWorkflow()]);
        var service = new GateConfigurationService(
            registry, new WorkflowInspector(), new InMemoryGatePolicyStore(), audit);

        await service.SetNodesAsync(Workflow, Version, "t1",
            Policy("validate", new ExecutionPolicyDto("requireApproval")), null, default);
        await service.ResetNodeAsync(Workflow, Version, "t1", "validate", null, default);

        IReadOnlyList<AuditEntry> entries = await audit.QueryAsync(null, 10, default);
        entries.Select(e => e.Action).Should().Contain(["gate.policy.set", "gate.policy.reset"]);
        entries.Should().OnlyContain(e => e.Detail!.Contains("\"tenantId\":\"t1\""));
    }

    private static (IGateConfigurationService Service, InMemoryGatePolicyStore Policies) Build()
    {
        var policies = new InMemoryGatePolicyStore();
        var registry = new WorkflowRegistry([new ConfigurableWorkflow()]);
        return (new GateConfigurationService(registry, new WorkflowInspector(), policies), policies);
    }

    private static Dictionary<string, ExecutionPolicyDto> Policy(string executorId, ExecutionPolicyDto policy)
        => new() { [executorId] = policy };

    private static ExecutorNodeDto Node(GateConfigResult result, string executorId)
        => result.Nodes!.Nodes.Single(n => n.ExecutorId == executorId);

    private sealed class RawOnlyWorkflow : IWorkflowDefinition<SampleContext, SampleResult>
    {
        public string Name => "raw-only";
        public string Version => "1.0.0";

        public ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken cancellationToken)
        {
            ExecutorBinding raw = context.RawNode(new Raw("raw"));
            return new ValueTask<Workflow>(new WorkflowBuilder(raw).WithOutputFrom(raw).Build());
        }

        private sealed class Raw : Executor<SampleContext, SampleResult>
        {
            public Raw(string id) : base(id) { }

            public override ValueTask<SampleResult> HandleAsync(
                SampleContext message, IWorkflowContext context, CancellationToken cancellationToken = default)
                => ValueTask.FromResult(new SampleResult(message.Value));
        }
    }
}

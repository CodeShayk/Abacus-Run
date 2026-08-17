using Abacus.Run.Abstractions;
using Abacus.Run.Api;
using Abacus.Run.Core;
using Abacus.Run.Persistence;
using FluentAssertions;
using Xunit;

namespace Abacus.Run.UnitTests;

public class TenancyTests
{
    [Theory]
    [InlineData("0")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void The_all_tenants_scope_has_several_spellings(string? tenantId)
        => Tenancy.IsAllTenants(tenantId).Should()
            .BeTrue("null has always meant host-wide in the policy store, and existing rows must keep resolving");

    [Theory]
    [InlineData("acme")]
    [InlineData("default")]
    [InlineData("00")]
    [InlineData("0x")]
    public void A_real_tenant_is_not_the_all_tenants_scope(string tenantId)
        => Tenancy.IsAllTenants(tenantId).Should().BeFalse();

    [Fact]
    public void Normalising_collapses_every_spelling_to_null()
    {
        Tenancy.Normalize("0").Should().BeNull();
        Tenancy.Normalize(null).Should().BeNull();
        Tenancy.Normalize("  ").Should().BeNull();
        Tenancy.Normalize("acme").Should().Be("acme");
    }

    [Fact]
    public void The_all_tenants_scope_cannot_own_an_instance()
    {
        Tenancy.CanOwnInstance(Tenancy.AllTenants).Should().BeFalse();
        Tenancy.CanOwnInstance(null).Should().BeFalse();
        Tenancy.CanOwnInstance("acme").Should().BeTrue();
    }
}

public class TenantScopedInstanceTests
{
    private sealed record Ctx(string Value = "x");

    private sealed record Res(string Value);

    private sealed class Workflow : IWorkflowDefinition<Ctx, Res>
    {
        public string Name => "w";
        public string Version => "1.0.0";

        public ValueTask<Microsoft.Agents.AI.Workflows.Workflow> BuildAsync(
            WorkflowBuildContext context, CancellationToken cancellationToken)
        {
            Microsoft.Agents.AI.Workflows.ExecutorBinding node = context.Node(
                new Abacus.Run.Executors.TransformExecutor<Ctx, Res>("n", c => new Res(c.Value)));

            return new ValueTask<Microsoft.Agents.AI.Workflows.Workflow>(
                new Microsoft.Agents.AI.Workflows.WorkflowBuilder(node)
                    .WithOutputFrom(node).WithName(Name).Build());
        }
    }

    private static InstanceLauncher Launcher(InMemoryInstanceStore instances)
        => new(new WorkflowRegistry([new Workflow()]), instances);

    private static System.Text.Json.JsonElement Context()
        => System.Text.Json.JsonSerializer.SerializeToElement(new Ctx(), JsonOptions.Default);

    [Fact]
    public async Task An_instance_belongs_to_a_real_tenant()
    {
        var instances = new InMemoryInstanceStore();

        StartResult result = await Launcher(instances)
            .StartAsync("w", null, new StartInstanceRequest { Context = Context() }, "acme", null, default);

        result.IsSuccess.Should().BeTrue();
        result.Instance!.TenantId.Should().Be("acme");
    }

    [Fact]
    public async Task Starting_an_instance_for_all_tenants_is_refused()
    {
        var instances = new InMemoryInstanceStore();

        StartResult result = await Launcher(instances).StartAsync(
            "w", null, new StartInstanceRequest { Context = Context() }, Tenancy.AllTenants, null, default);

        result.IsSuccess.Should()
            .BeFalse("an instance is somebody's work, and 'everyone's' is not an owner");

        result.Kind.Should().Be(StartResultKind.InvalidContext);
        result.Errors.Should().ContainKey("tenantId");

        // And nothing was written, so no row exists that tenant-filtered queries would miss.
        Page<WorkflowInstance> all = await instances.QueryAsync(new InstanceQuery(), default);
        all.Items.Should().BeEmpty();
    }

    /// <summary>
    /// Definitions are global: the registry answers the same for every caller, and resolving one
    /// takes no tenant at all. This is what makes catalog responses safe to share.
    /// </summary>
    [Fact]
    public void A_workflow_definition_is_not_tenant_scoped()
    {
        IWorkflowRegistry registry = new WorkflowRegistry([new Workflow()]);

        registry.Resolve("w").Should().NotBeNull();
        registry.All.Should().ContainSingle();
    }
}

public class AllTenantsGatePolicyTests
{
    private static readonly ApprovalGate Gate = new() { Mode = ExecutionMode.RequireApproval };

    [Fact]
    public async Task A_policy_written_for_tenant_zero_lands_in_the_all_tenants_scope()
    {
        var store = new InMemoryGatePolicyStore();

        await store.SetAsync(Tenancy.Normalize("0"), "w", "1.0.0", "n", Gate, default);

        // Read back at the host-wide scope, which is where null lives.
        IReadOnlyDictionary<string, ApprovalGate> hostWide =
            await store.ListAsync(null, "w", "1.0.0", default);

        hostWide.Should().ContainKey("n",
            "'0' is the all-tenants scope, not a tenant named zero");
    }

    [Fact]
    public async Task An_all_tenants_policy_applies_where_a_tenant_has_none_of_its_own()
    {
        var store = new InMemoryGatePolicyStore();
        await store.SetAsync(Tenancy.Normalize(Tenancy.AllTenants), "w", "1.0.0", "n", Gate, default);

        ApprovalGate? resolved = await store.FindAsync("acme", "w", "1.0.0", "n", null, default);

        resolved.Should().NotBeNull("a tenant with no policy of its own inherits the all-tenants one");
        resolved!.Mode.Should().Be(ExecutionMode.RequireApproval);
    }

    [Fact]
    public async Task A_tenants_own_policy_wins_over_the_all_tenants_one()
    {
        var store = new InMemoryGatePolicyStore();

        await store.SetAsync(Tenancy.Normalize("0"), "w", "1.0.0", "n", Gate, default);
        await store.SetAsync("acme", "w", "1.0.0", "n",
            new ApprovalGate { Mode = ExecutionMode.Autonomous }, default);

        ApprovalGate? resolved = await store.FindAsync("acme", "w", "1.0.0", "n", null, default);

        resolved!.Mode.Should().Be(ExecutionMode.Autonomous, "the more specific scope wins");
    }

    [Fact]
    public async Task Another_tenant_is_unaffected_by_a_tenant_specific_policy()
    {
        var store = new InMemoryGatePolicyStore();
        await store.SetAsync("acme", "w", "1.0.0", "n", Gate, default);

        ApprovalGate? other = await store.FindAsync("globex", "w", "1.0.0", "n", null, default);

        other.Should().BeNull("one tenant's configuration is not another's");
    }
}

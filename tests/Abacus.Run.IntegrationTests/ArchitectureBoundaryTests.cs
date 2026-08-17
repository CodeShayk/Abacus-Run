using System.Reflection;
using Abacus.Adapters.Messaging.RabbitMQ;
using Abacus.Adapters.Cache.Redis;
using Abacus.Run.Abstractions;
using Abacus.Run.Api;
using Abacus.Run.EventBus;
using Abacus.Run.Service.ControlPlane;
using Abacus.Run.Core;
using Abacus.Run.Persistence;
using Abacus.Run.Service;
using FluentAssertions;
using Xunit;

namespace Abacus.Run.IntegrationTests;

/// <summary>
/// Locks in the split between the reusable framework and the deployable host: <c>Abacus.Run</c>
/// carries the headless workflow host — runtime, dispatch and HTTP API — while
/// <c>Abacus.Run.Service</c> contributes the user interface, the environment-specific
/// infrastructure, and the wiring that selects it.
/// </summary>
public class ArchitectureBoundaryTests
{
    private static readonly Assembly Library = typeof(WorkflowRunner).Assembly;
    private static readonly Assembly Host = typeof(AbacusServiceCollectionExtensions).Assembly;
    private static readonly Assembly RedisAdapter = typeof(RedisEventBroker).Assembly;
    private static readonly Assembly RabbitMqAdapter = typeof(RabbitMqEventBroker).Assembly;

    [Fact]
    public void The_library_and_the_host_are_separate_assemblies()
    {
        Library.GetName().Name.Should().Be("Abacus.Run");
        Host.GetName().Name.Should().Be("Abacus.Run.Service");
    }

    [Fact]
    public void The_user_interface_belongs_to_the_host()
    {
        // The framework is headless: it serves the API and nothing else. Shipping Razor Pages inside
        // it would force every consumer to take an MVC dependency and a UI they may not want.
        typeof(ControlPlaneExtensions).Assembly.Should().BeSameAs(Host);
        typeof(Abacus.Run.Service.ControlPlane.Services.WorkflowApiClient).Assembly.Should().BeSameAs(Host);

        Library.GetReferencedAssemblies().Select(a => a.Name)
            .Should().NotContain("Microsoft.AspNetCore.Mvc.RazorPages");
    }

    [Fact]
    public void The_library_does_not_reference_the_host()
        => Library.GetReferencedAssemblies().Select(a => a.Name)
            .Should().NotContain("Abacus.Run.Service", "the framework must not depend on any single deployment");

    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("Microsoft.EntityFrameworkCore.SqlServer")]
    [InlineData("StackExchange.Redis")]
    public void The_library_is_free_of_infrastructure_dependencies(string forbidden)
        => Library.GetReferencedAssemblies().Select(a => a.Name)
            .Should().NotContain(forbidden,
                "storage and transport choices belong to the host, not the framework");

    [Fact]
    public void The_framework_surface_lives_in_the_library()
    {
        // API, dispatch, persistence defaults and runtime ship together, so a consumer gets a
        // working headless host from one reference.
        typeof(Endpoints).Assembly.Should().BeSameAs(Library);
        typeof(Sse).Assembly.Should().BeSameAs(Library);
        typeof(InstanceControlService).Assembly.Should().BeSameAs(Library);
        typeof(InstanceLauncher).Assembly.Should().BeSameAs(Library);
        typeof(WorkflowHostBuilder).Assembly.Should().BeSameAs(Library);
        typeof(InMemoryEventBus).Assembly.Should().BeSameAs(Library);
        typeof(InProcessEventBroker).Assembly.Should().BeSameAs(Library);
        typeof(InMemoryInstanceStore).Assembly.Should().BeSameAs(Library);
        typeof(OverflowCheckpointStore).Assembly.Should().BeSameAs(Library);
    }

    [Fact]
    public void Storage_infrastructure_lives_in_the_host()
    {
        string[] hostTypes = Host.GetTypes()
            .Where(t => t.IsClass && !t.IsNested)
            .Select(t => t.Name)
            .ToArray();

        hostTypes.Should().Contain(n => n.StartsWith("SqlServer", StringComparison.Ordinal),
            "SQL Server stores are deployment-specific");
    }

    [Fact]
    public void Transports_live_in_their_own_adapter_assemblies()
    {
        // Not in the host. A transport shipped inside a deployable is only reusable by copying it,
        // and forces every consumer of that host to take its client library.
        RedisAdapter.GetName().Name.Should().Be("Abacus.Adapters.Cache.Redis");
        RabbitMqAdapter.GetName().Name.Should().Be("Abacus.Adapters.Messaging.RabbitMQ");

        typeof(RedisEventBus).Assembly.Should().BeSameAs(RedisAdapter);
        typeof(RedisEventBroker).Assembly.Should().BeSameAs(RedisAdapter);
        typeof(RedisControlChannel).Assembly.Should().BeSameAs(RedisAdapter);
        typeof(RabbitMqEventBroker).Assembly.Should().BeSameAs(RabbitMqAdapter);

        Host.GetTypes().Select(t => t.Name).Should()
            .NotContain(n => n.StartsWith("Redis", StringComparison.Ordinal)
                          || n.StartsWith("RabbitMq", StringComparison.Ordinal),
                "a transport left behind in the host would be the copy nobody updates");
    }

    [Theory]
    [InlineData("Abacus.Adapters.Cache.Redis")]
    [InlineData("Abacus.Adapters.Messaging.RabbitMQ")]
    public void An_adapter_depends_on_the_framework_and_nothing_else_of_ours(string adapterName)
    {
        Assembly adapter = adapterName == "Abacus.Adapters.Cache.Redis" ? RedisAdapter : RabbitMqAdapter;

        string[] references = [.. adapter.GetReferencedAssemblies().Select(a => a.Name!)];

        references.Should().Contain("Abacus.Run", "an adapter exists to implement the framework's contracts");

        references.Should().NotContain("Abacus.Run.Service",
            "an adapter that referenced a host would be tied to one deployment");

        // The two adapters must not know about each other, or choosing one would drag in the
        // other's client library.
        references.Should().NotContain(
            adapterName == "Abacus.Adapters.Cache.Redis" ? "Abacus.Adapters.Messaging.RabbitMQ" : "Abacus.Adapters.Cache.Redis",
            "transports are alternatives, not collaborators");
    }

    [Theory]
    [InlineData("Abacus.Adapters.Cache.Redis", "StackExchange.Redis", "RabbitMQ.Client")]
    [InlineData("Abacus.Adapters.Messaging.RabbitMQ", "RabbitMQ.Client", "StackExchange.Redis")]
    public void An_adapter_carries_only_its_own_client_library(
        string adapterName, string expected, string forbidden)
    {
        Assembly adapter = adapterName == "Abacus.Adapters.Cache.Redis" ? RedisAdapter : RabbitMqAdapter;

        string[] references = [.. adapter.GetReferencedAssemblies().Select(a => a.Name!)];

        references.Should().Contain(expected);
        references.Should().NotContain(forbidden,
            "referencing a transport means taking its client; taking both would defeat the split");
    }

    [Fact]
    public void Every_framework_contract_the_host_implements_is_an_infrastructure_adapter()
    {
        // The host may implement storage and transport contracts — that is its whole job — but each
        // such type must be a named adapter for a specific technology, not framework logic that
        // drifted back into the deployable.
        Type[] contracts =
        [
            typeof(IInstanceStore), typeof(IEventStore), typeof(ILogStore), typeof(IApprovalStore),
            typeof(IGatePolicyStore), typeof(IAuditStore), typeof(IBlobStore), typeof(IEventBus),
            typeof(IEventBroker), typeof(IEventSubscriptionStore)
        ];

        string[] adapters = Host.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => contracts.Any(c => c.IsAssignableFrom(t)))
            .Select(t => t.Name)
            .ToArray();

        adapters.Should().NotBeEmpty("the host exists to supply concrete infrastructure");

        // The prefix is the technology the adapter speaks. Adding one here is a deliberate act;
        // an adapter named for what it does rather than what it talks to is framework logic that
        // has drifted back into the deployable. Transports are not on this list because they no
        // longer live in the host at all.
        string[] technologies = ["SqlServer", "Sqlite"];

        adapters.Should().OnlyContain(
            n => technologies.Any(t => n.StartsWith(t, StringComparison.Ordinal)));
    }

    [Fact]
    public void The_host_defines_no_framework_extension_points_outside_declared_workflows()
    {
        // Framework extension points (workflow definitions, host executors, middleware) belong to
        // consumers of Abacus.Run — not to the shell that wires infrastructure. The rule carves out
        // workflows that ship inside the Service assembly by convention: anything under the
        // "…Workflows.<Name>" namespace is a documented consumer of the framework, not shell code
        // that has drifted.
        Type[] extensionPoints =
        [
            typeof(Abacus.Run.Abstractions.IWorkflowDefinition),
            typeof(Abacus.Run.Abstractions.IHostExecutor),
            typeof(Abacus.Run.Abstractions.Middleware.IWorkflowMiddleware),
            typeof(Abacus.Run.Abstractions.Middleware.IExecutorMiddleware)
        ];

        string[] offenders = Host.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => extensionPoints.Any(e => e.IsAssignableFrom(t)))
            .Where(t => t.Namespace is null
                || !t.Namespace.StartsWith("Abacus.Run.Service.Workflows.", StringComparison.Ordinal))
            .Select(t => t.FullName!)
            .ToArray();

        offenders.Should().BeEmpty(
            "extension points outside a declared workflow namespace would mean the shell had grown behaviour of its own");
    }

    [Fact]
    public void The_host_runs_on_the_library_defaults_when_no_infrastructure_is_configured()
    {
        // The integration fixture configures neither SQL Server nor Redis, so the framework's
        // in-memory implementations are what served every other test in this project.
        using var fixture = new HostFixture();
        using HttpClient client = fixture.CreateClient();

        fixture.Resolve<IInstanceStore>().Should().BeOfType<InMemoryInstanceStore>();
        fixture.Resolve<IEventStore>().Should().BeOfType<InMemoryEventStore>();
    }
}

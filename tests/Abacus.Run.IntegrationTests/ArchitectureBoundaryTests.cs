using System.Reflection;
using System.Runtime.CompilerServices;
using Abacus.Adapters.Messaging.RabbitMQ;
using Abacus.Adapters.Cache.Redis;
using Abacus.Run.Abstractions;
using Abacus.Run.Api;
using Abacus.Run.Messaging;
using Abacus.Run.Notifications;
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
    private static readonly Assembly RedisAdapter = typeof(RedisNotificationBus).Assembly;
    private static readonly Assembly RabbitMqAdapter = typeof(RabbitMqDomainEventBroker).Assembly;
    private static readonly Assembly Dsl = typeof(Abacus.Run.Dsl.Interpretation.DslMessage).Assembly;

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
        typeof(InMemoryNotificationBus).Assembly.Should().BeSameAs(Library);
        typeof(InProcessDomainEventBroker).Assembly.Should().BeSameAs(Library);
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
    public void Infrastructure_lives_in_its_own_adapter_assemblies()
    {
        // Not in the host. Infrastructure shipped inside a deployable is only reusable by copying
        // it, and forces every consumer of that host to take its client library.
        RedisAdapter.GetName().Name.Should().Be("Abacus.Adapters.Cache.Redis");
        RabbitMqAdapter.GetName().Name.Should().Be("Abacus.Adapters.Messaging.RabbitMQ");

        typeof(RedisNotificationBus).Assembly.Should().BeSameAs(RedisAdapter);
        typeof(RabbitMqDomainEventBroker).Assembly.Should().BeSameAs(RabbitMqAdapter);

        Host.GetTypes().Select(t => t.Name).Should()
            .NotContain(n => n.StartsWith("Redis", StringComparison.Ordinal)
                          || n.StartsWith("RabbitMq", StringComparison.Ordinal),
                "an adapter left behind in the host would be the copy nobody updates");
    }

    /// <summary>
    /// The two adapter families answer different questions, and keeping them apart is what lets a
    /// deployment take one without the other. Cache is the SSE backplane — how a subscriber reaches
    /// a run it is watching. Messaging is domain events — how work reaches another service.
    /// </summary>
    [Fact]
    public void A_cache_adapter_carries_no_domain_messaging()
    {
        Type[] cacheTypes = [.. RedisAdapter.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false })];

        cacheTypes.Should().Contain(t => typeof(INotificationBus).IsAssignableFrom(t),
            "the cache adapter exists to be the SSE backplane");

        cacheTypes.Should().NotContain(t => typeof(IDomainEventBroker).IsAssignableFrom(t),
            "domain messaging belongs to an Abacus.Adapters.Messaging.* adapter, not to the cache");
    }

    /// <summary>
    /// Every cache adapter supplies the whole cache surface. A future
    /// <c>Abacus.Adapters.Cache.Memcached</c> satisfies this by implementing one interface, and
    /// substituting it moves general caching and the SSE backplane in one step.
    /// </summary>
    [Fact]
    public void A_cache_adapter_supplies_every_cache_capability()
    {
        Type[] adapters =
        [
            .. RedisAdapter.GetTypes().Where(t =>
                t is { IsClass: true, IsAbstract: false } && typeof(ICacheAdapter).IsAssignableFrom(t))
        ];

        adapters.Should().ContainSingle(
            "a cache adapter assembly offers one substitution point, not a menu of half-choices");

        Type[] builtIn =
        [
            .. Library.GetTypes().Where(t =>
                t is { IsClass: true, IsAbstract: false } && typeof(ICacheAdapter).IsAssignableFrom(t))
        ];

        builtIn.Should().NotBeEmpty(
            "the framework ships an in-memory adapter so a host needs no cache infrastructure at all");
    }

    [Fact]
    public void A_messaging_adapter_carries_no_cache()
    {
        Type[] messagingTypes = [.. RabbitMqAdapter.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false })];

        messagingTypes.Should().Contain(t => typeof(IDomainEventBroker).IsAssignableFrom(t),
            "the messaging adapter exists to carry domain events");

        messagingTypes.Should().NotContain(t => typeof(INotificationBus).IsAssignableFrom(t),
            "the SSE backplane belongs to an Abacus.Adapters.Cache.* adapter");
    }

    /// <summary>
    /// Both defaults ship with the framework, so a single-service deployment with no infrastructure
    /// at all still streams SSE and still routes domain events.
    /// </summary>
    [Fact]
    public void The_framework_defaults_need_no_adapter()
    {
        typeof(InMemoryNotificationBus).Assembly.Should().BeSameAs(Library);
        typeof(InProcessDomainEventBroker).Assembly.Should().BeSameAs(Library);
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
            typeof(IGatePolicyStore), typeof(IAuditStore), typeof(IBlobStore), typeof(INotificationBus),
            typeof(IDomainEventBroker), typeof(IDomainEventSubscriptionStore)
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

    /// <summary>
    /// The DSL is a second front end onto the framework, not a layer inside it. It sits where an
    /// adapter sits: above <c>Abacus.Run</c>, below any deployment, and knowing about neither the
    /// host nor infrastructure.
    /// </summary>
    [Fact]
    public void The_dsl_is_a_front_end_on_the_framework_and_nothing_else_of_ours()
    {
        Dsl.GetName().Name.Should().Be("Abacus.Run.Dsl");

        string[] references = [.. Dsl.GetReferencedAssemblies().Select(a => a.Name!)];

        references.Should().Contain("Abacus.Run",
            "the DSL exists to interpret documents onto the framework's runtime");

        references.Should().NotContain("Abacus.Run.Service",
            "a front end that referenced a host would be tied to one deployment");

        foreach (string forbidden in new[]
                 {
                     "Abacus.Adapters.Cache.Redis", "Abacus.Adapters.Messaging.RabbitMQ",
                     "Microsoft.EntityFrameworkCore", "StackExchange.Redis", "RabbitMQ.Client"
                 })
        {
            references.Should().NotContain(forbidden,
                "the DSL declares what runs, never where it is stored or how it is transported");
        }
    }

    /// <summary>
    /// The DSL reaches the framework through the same surface any consumer has. If it needed
    /// internals, the seams the design leans on — <c>IContextValidatingWorkflow</c>,
    /// <c>ITemplateBindingSource</c>, <c>IDocumentAuthoredWorkflow</c> — would be missing something,
    /// and the next front end would have to be written inside <c>Abacus.Run</c> to work at all.
    /// </summary>
    [Fact]
    public void The_dsl_uses_only_the_frameworks_public_surface()
    {
        string[] granted = Library.GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(a => a.AssemblyName.Split(',')[0])
            .ToArray();

        granted.Should().NotContain("Abacus.Run.Dsl",
            "a front end with internals access is a front end whose seams are not really public");

        // The seams themselves, named: each is public, so a third front end has the same reach.
        typeof(IContextValidatingWorkflow).IsPublic.Should().BeTrue();
        typeof(IDocumentAuthoredWorkflow).IsPublic.Should().BeTrue();
        typeof(Abacus.Run.Executors.ITemplateBindingSource).IsPublic.Should().BeTrue();
        typeof(IContextValidatingWorkflow).Assembly.Should().BeSameAs(Library);
        typeof(IDocumentAuthoredWorkflow).Assembly.Should().BeSameAs(Library);
    }

    /// <summary>
    /// The framework must not know the DSL exists. It reports provenance through an interface it
    /// declares and the DSL implements, so <c>Abacus.Run</c> names no front end and a host that
    /// authors every workflow in C# carries neither the schema validator nor the expression parser.
    /// </summary>
    [Fact]
    public void The_framework_does_not_reference_the_dsl()
    {
        Library.GetReferencedAssemblies().Select(a => a.Name)
            .Should().NotContain("Abacus.Run.Dsl");

        Library.GetReferencedAssemblies().Select(a => a.Name)
            .Should().NotContain("JsonSchema.Net",
                "the schema validator is the DSL's cost to carry, not every consumer's");
    }

    /// <summary>
    /// The schema ships inside the DSL assembly as one embedded resource. Byte equality with the
    /// published file is asserted in the DSL suite; what belongs here is that there is exactly one
    /// copy and it travels with the code that enforces it.
    /// </summary>
    [Fact]
    public void The_dsl_carries_the_schema_as_an_embedded_resource()
    {
        string[] schemaResources = [.. Dsl.GetManifestResourceNames()
            .Where(n => n.Contains("workflow-dsl", StringComparison.Ordinal))];

        schemaResources.Should().ContainSingle("one copy, or the published and enforced schemas can drift")
            .Which.Should().EndWith(".json");
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

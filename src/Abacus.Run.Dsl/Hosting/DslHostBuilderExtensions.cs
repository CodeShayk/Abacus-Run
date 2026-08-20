using System.Text.Json.Nodes;
using Abacus.Run.Abstractions;
using Abacus.Run.Api;
using Abacus.Run.Dsl.Interpretation;
using Abacus.Run.Dsl.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Abacus.Run.Dsl.Hosting;

/// <summary>Registers DSL documents and custom nodes alongside compiled workflows.</summary>
public static class DslHostBuilderExtensions
{
    /// <summary>
    /// Adds a document from disk. The path is read at startup, not at build time, so a document can
    /// be edited and the host restarted without a rebuild.
    /// </summary>
    public static WorkflowHostBuilder AddDslWorkflow(this WorkflowHostBuilder builder, string path)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string full = Path.GetFullPath(path);
        return builder.AddDslSource(new DslSource(full, () => File.ReadAllText(full)));
    }

    /// <summary>Adds a document already in hand — an embedded resource, or a test fixture.</summary>
    public static WorkflowHostBuilder AddDslWorkflowText(
        this WorkflowHostBuilder builder, string text, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(text);

        return builder.AddDslSource(new DslSource(description ?? "(inline document)", () => text));
    }

    /// <summary>
    /// Adds every matching document in a directory, in a stable order.
    /// </summary>
    /// <remarks>
    /// Ordered rather than left to the file system: a hash conflict between two documents claiming
    /// one <c>(name, version)</c> should name the same one every time, or the failure would look
    /// intermittent.
    /// </remarks>
    public static WorkflowHostBuilder AddDslWorkflowsFromDirectory(
        this WorkflowHostBuilder builder,
        string path,
        string searchPattern = "*.workflow.json",
        bool recursive = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string full = Path.GetFullPath(path);

        if (!Directory.Exists(full))
        {
            // A missing directory is a composition mistake and would otherwise register nothing at
            // all, which looks exactly like a host with no workflows.
            throw new DirectoryNotFoundException(
                $"No DSL workflow directory at '{full}'. Check the path, or remove the registration.");
        }

        IEnumerable<string> files = Directory
            .EnumerateFiles(full, searchPattern,
                recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.Ordinal);

        foreach (string file in files)
        {
            builder.AddDslWorkflow(file);
        }

        return builder;
    }

    /// <summary>Registers a custom node, making its name available to every document.</summary>
    public static WorkflowHostBuilder AddDslNode(this WorkflowHostBuilder builder, IDslNodeFactory factory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(factory);

        Registry(builder.Services).AddNode(factory);
        return builder;
    }

    /// <summary>Registers a custom node from a delegate, for a node whose construction is a one-liner.</summary>
    public static WorkflowHostBuilder AddDslNode(
        this WorkflowHostBuilder builder,
        string name,
        Func<DslNodeContext, IHostExecutor> create,
        JsonNode? parameterSchema = null)
        => builder.AddDslNode(new DelegateDslNodeFactory(name, create, parameterSchema));

    /// <summary>
    /// Makes the DSL available without registering any document: the node catalog, the schema route
    /// and the validate route all work on a host that has not yet been given one.
    /// </summary>
    /// <remarks>
    /// Needed because <c>MapDslApi</c> resolves the registry, and a host that maps the routes before
    /// anyone has called <c>AddDslWorkflow</c> would otherwise fail to start. Which is exactly the
    /// host an authoring tool talks to while a first document is being written.
    /// </remarks>
    public static WorkflowHostBuilder UseDsl(this WorkflowHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        Registry(builder.Services);
        return builder;
    }

    /// <summary>Configures the limits and egress policy documents are validated against.</summary>
    public static WorkflowHostBuilder ConfigureDsl(
        this WorkflowHostBuilder builder, Action<DslRegistry> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        configure(Registry(builder.Services));
        return builder;
    }

    private static WorkflowHostBuilder AddDslSource(this WorkflowHostBuilder builder, DslSource source)
    {
        DslRegistry registry = Registry(builder.Services);
        int index = registry.AddSource(source);

        // One IWorkflowDefinition registration per document, all resolving from the same shared
        // list. Registering the list as a single service would hide the documents from the registry,
        // which enumerates IWorkflowDefinition to build the catalog.
        //
        // The factory runs on first enumeration — after every AddDslNode call has completed, which
        // is what lets composition be written in any order.
        builder.Services.AddSingleton<IWorkflowDefinition>(_ =>
        {
            IReadOnlyList<DslWorkflowDefinition> definitions = registry.Resolve();

            return index < definitions.Count
                ? definitions[index]
                : throw new InvalidOperationException(
                    $"The DSL document '{source.Description}' did not resolve to a definition.");
        });

        return builder;
    }

    /// <summary>
    /// One registry per service collection, held as a singleton instance so both the composition
    /// calls and the container see the same object.
    /// </summary>
    private static DslRegistry Registry(IServiceCollection services)
    {
        ServiceDescriptor? existing = services.FirstOrDefault(
            d => d.ServiceType == typeof(DslRegistry) && d.ImplementationInstance is DslRegistry);

        if (existing?.ImplementationInstance is DslRegistry registry)
        {
            return registry;
        }

        var created = new DslRegistry();
        services.AddSingleton(created);
        services.TryAddSingleton(created.Catalog);
        return created;
    }
}

# Abacus Run Wiki

Abacus Run is a .NET 9 workflow runtime and HTTP host built on Microsoft Agent Framework workflows. It turns a graph-based workflow definition into a managed workflow instance with a lifecycle, ownership, retries, checkpoints, approvals, events, logs, and operator controls.

This page is the repository-level technical wiki. It documents the implementation in this repository. The product and technical design documents in this directory describe the broader target architecture; where the target and current implementation differ, this page calls out the difference.

## Contents

- [At a glance](#at-a-glance)
- [Current implementation status](#current-implementation-status)
- [Architecture](#architecture)
- [Project structure](#project-structure)
- [Getting started](#getting-started)
- [Authoring a workflow](#authoring-a-workflow)
  - [What a definition can declare](#what-a-definition-can-declare)
  - [Nodes](#nodes) · [Built-in executors](#built-in-executors) · [Custom executors](#custom-executors)
  - [Edges](#edges) · [Approval gates on a node](#approval-gates-on-a-node) · [Events on a node](#events-on-a-node)
  - [Failure classification](#failure-classification) · [Engine context](#engine-context-inside-an-executor) · [Middleware](#middleware)
  - [A definition using all of it](#a-definition-using-all-of-it) · [Versioning rules](#versioning-rules-that-bite)
- [Workflow audit records](#workflow-audit-records)
- [Registering workflows and middleware](#registering-workflows-and-middleware)
- [Instance lifecycle](#instance-lifecycle)
- [Retries and failure classification](#retries-and-failure-classification)
- [Checkpoints and resumption](#checkpoints-and-resumption)
- [Human approval gates](#human-approval-gates)
- [Tenant executor configuration](#tenant-executor-configuration)
- [Events, history, and SSE](#events-history-and-sse)
- [Event broker and event-driven workflows](#event-broker-and-event-driven-workflows)
- [HTTP API](#http-api)
- [Configuration](#configuration)
- [Security and data handling](#security-and-data-handling)
- [Operations](#operations)
- [Containers and GHCR](#containers-and-ghcr)
- [Testing](#testing)
- [Extension points](#extension-points)
- [Design constraints](#design-constraints)
- [Troubleshooting](#troubleshooting)
- [Appendix: authoring variations](#appendix-authoring-variations)
- [Related documents](#related-documents)

## At a glance

| Concern | Current behavior |
| --- | --- |
| Runtime | .NET 9 / ASP.NET Core minimal API |
| Workflow engine | `Microsoft.Agents.AI.Workflows` |
| Workflow registration | Dependency injection through `AddWorkflow<T>()` or `AddWorkflow(IWorkflowDefinition)` |
| Persistence | In-memory stores by default |
| Checkpoint cadence | Superstep by default; `None`, `SuperStep`, and `Manual` are supported |
| Ownership | Dispatcher and lease abstractions are used by the host; the default stores are process-local |
| Events | Sequenced per-instance event store plus optional event bus and SSE relay; nodes emit their own via `Runtime.Notify`, and a definition sets its emission policy |
| Event broker | Topic pub/sub for event-driven pipelines: workflows start on a trigger or park on a wait. Local in-process by default; Redis Streams or RabbitMQ for cross-service |
| Approvals | Durable approval contracts and in-memory coordinator/store, with decision and expiry handling |
| Middleware | Workflow-level and host-executor-level pipelines |
| Audit records | A workflow declares the shape of its own audit record; the runtime hands every node a recorder and stores entries generically |
| Library | `src/Abacus.Run` — headless framework: runtime, dispatch, executors, middleware, in-memory stores, HTTP API |
| Transport adapters | `src/Abacus.Adapters.Cache.Redis` and `src/Abacus.Adapters.Messaging.RabbitMQ` — each implements the framework's transport contracts and depends on nothing but the framework and its own client |
| Service host | `src/Abacus.Run.Service` — control-plane UI, SQL Server stores, startup wiring that selects the adapters |
| Container | Multi-stage .NET 9 image listening on port 8080 |
| Image publishing | GitHub Actions publishes `ghcr.io/codeshayk/abacus-run` |

## Current implementation status

The repository contains a working runtime, API host, control plane, built-in executors, middleware, persistence abstractions, and test suites. The default host wiring in `Program.cs` uses in-memory infrastructure:

- `InMemoryInstanceStore`
- `InMemoryEventStore`
- `InMemoryLogStore`
- `InMemoryApprovalStore`
- `InMemoryGatePolicyStore`
- `InMemoryAuditStore`
- `InMemoryAuditRecordStore`
- `InMemoryBlobStore`
- `OverflowCheckpointStore` over the blob abstraction
- `InMemoryNotificationBus`
- `InMemoryDomainEventSubscriptionStore`
- `InProcessDomainEventBroker`

The store interfaces are the substitution boundary for durable infrastructure. A production deployment must provide shared, durable implementations before relying on process loss recovery or multiple replicas.

The repository also contains `docs/PRD-Abacus-Run.md` and `docs/TDD-Abacus-Run.md`. Those documents describe the intended multi-replica SQL/blob/event-bus architecture and include design decisions that go beyond the current in-memory default.

## Architecture

```mermaid
flowchart TD
    Client[HTTP client] --> Api[ASP.NET Core API]
    Operator[Operator] --> Control[Control plane]
    Control --> Api
    Api --> Launcher[Instance launcher]
    Launcher --> InstanceStore[Instance store]
    Launcher --> Registry[Workflow registry]
    Dispatcher[Dispatcher service] --> InstanceStore
    Dispatcher --> Runner[Workflow runner]
    Registry --> Runner
    Runner --> Workflow[Agent Framework workflow graph]
    Runner --> Checkpoints[Checkpoint store]
    Runner --> Events[Event sink and event store]
    Runner --> Logs[Log store]
    Runner --> Approvals[Approval coordinator]
    Workflow --> Executors[Host executors]
    Executors --> Middleware[Executor middleware]
    Events --> SSE[SSE stream]
```

### Request path

1. A caller posts a typed JSON context to `/workflows/{name}/instances`.
2. The launcher resolves the requested workflow version and validates the context against its CLR type.
3. The launcher creates an instance record and returns `202 Accepted` with the instance location.
4. The dispatcher claims a claimable instance and invokes `WorkflowRunner`.
5. The runner rebuilds the workflow graph from the pinned workflow definition and starts or resumes an Agent Framework streaming run.
6. Framework events are translated into sequenced `EventEnvelope` records.
7. The runner updates instance state, checkpoints, logs, approvals, and terminal outcome as the run progresses.
8. Clients poll the instance, read event history, or subscribe to its SSE stream.

### Start and execution sequence

```mermaid
sequenceDiagram
  participant C as Client
  participant A as Workflow API
  participant L as Instance Launcher
  participant D as Dispatcher
  participant R as Workflow Runner
  participant S as Stores
  participant E as Event Stream

  C->>A: POST /workflows/{name}/instances
  A->>L: Resolve version and validate context
  L->>S: Create instance
  S-->>L: Instance id
  L-->>A: Accepted location
  A-->>C: 202 Accepted
  D->>S: Claim pending instance
  S-->>D: Lease granted
  D->>R: Run or resume instance
  R->>S: Read checkpoint and state
  R->>E: Publish sequenced progress events
  R->>S: Write state, logs, and checkpoint
  E-->>C: SSE event stream
  R->>S: Mark terminal status
```

### Ownership model

An instance is the unit of execution and is identified by a stable `InstanceId`. Its workflow name and version are recorded when it is created. A dispatcher should execute only instances it has successfully claimed. The instance store and lease implementation are responsible for preventing two host replicas from actively driving the same instance.

The default in-memory stores are suitable for development and tests. They do not provide cross-process coordination or durable recovery by themselves.

## Project structure

| Project | Responsibility |
| --- | --- |
| `src/Abacus.Run` | Headless framework: workflow contracts, runtime, dispatch, executors, middleware, in-memory store defaults, and the HTTP API endpoints |
| `src/Abacus.Run.Service` | Deployable host: control-plane UI, concrete persistence and event-bus integrations, service registration, and the workflow definitions this deployment runs |
| `tests/Abacus.Run.UnitTests` | Focused runtime and store tests; references the library only |
| `tests/Abacus.Run.IntegrationTests` | Real host, HTTP endpoint, control-plane, and architecture-boundary tests |
| `tests/Abacus.Run.ChaosTests` | Failure and lifecycle resilience tests |
| `tests/Abacus.Run.LoadTests` | Load-oriented test project |

Folders inside each project:

```
src/Abacus.Run/               src/Abacus.Run.Service/
  Abstractions/                 ControlPlane/      Razor Pages backing services
  Api/                          Infrastructure/    SQL Server stores
  Core/                           Auditing/        audit-record store and migrations
  Dispatch/                     Pages/             control-plane Razor Pages
  EventBus/                     Workflows/         workflow definitions hosted here
  Executors/                      <Name>/          one self-contained folder per workflow
  Middlewares/                  wwwroot/           control-plane CSS and JS
  Persistence/                  Program.cs
                                AbacusServiceCollectionExtensions.cs

src/Abacus.Adapters.Cache.Redis/     src/Abacus.Adapters.Messaging.RabbitMQ/
  RedisNotificationBus.cs               RabbitMqDomainEventBroker.cs
  RedisEventBroker.cs            RabbitMqServiceCollectionExtensions.cs
  RedisServiceCollectionExtensions.cs
```

### Where the line falls

Three kinds of project, and the rule for each is different.

The **library** is headless. It serves the API and nothing else, so it takes no dependency on Razor,
MVC, Entity Framework, or any transport client, and a consumer that references it gets a working host
without inheriting a UI or a storage choice.

An **adapter** implements the framework's contracts over one technology. It references
`Abacus.Run` and its own client library, and nothing else of ours. In particular it does not
reference a host — a transport that did would be tied to one deployment and reusable only by copying
it — and the adapters do not reference each other, so choosing Redis never drags in an AMQP client.

The **service** supplies what is specific to one deployment: the operator UI, the concrete stores,
and the startup code that selects an adapter. It references both adapters so an operator can switch
transport by configuration rather than by rebuild; neither connects unless its connection string is
set.

`AddAbacus` reads as two steps for this reason — register the framework with its in-memory defaults,
then displace those defaults when a connection string is configured. With neither
`Abacus:SqlServer:ConnectionString` nor `Abacus:Redis:ConnectionString` set, the host runs entirely
in memory, which is what keeps local development and the integration suite free of external
dependencies.

`ArchitectureBoundaryTests` in the integration suite enforces the split: the library must not
reference the host, EF Core, Redis, or Razor Pages; every framework contract the host implements must
be a named `SqlServer*` or `Redis*` adapter; and the host must define no framework extension points —
workflow definitions, host executors, middleware — outside a declared
`Abacus.Run.Service.Workflows.<Name>` namespace. That carve-out is what lets a workflow ship inside
the host assembly without the rule reading as "the shell may grow behaviour of its own".

Workflow authors should normally depend on `Abacus.Run` and its `Abacus.Run.Abstractions` namespace,
then register their definitions in the application host.

## Getting started

### Prerequisites

- .NET 9 SDK
- Access to the package feeds configured in `nuget.config`
- Docker Desktop for container work

### Build and test

```bash
dotnet restore Abacus.Run.slnx
dotnet build Abacus.Run.slnx
dotnet test Abacus.Run.slnx
```

For the CI-equivalent Release coverage run:

```bash
dotnet build --configuration Release
dotnet test --configuration Release --no-build --collect:"XPlat Code Coverage"
```

### Run the API host

```bash
dotnet run --project src/Abacus.Run.Service/Abacus.Run.Service.csproj
```

The host exposes:

```bash
curl http://localhost:5000/health/live
curl http://localhost:5000/health/ready
```

The actual port can be changed with standard ASP.NET Core configuration, for example `ASPNETCORE_HTTP_PORTS=8080`.

## Authoring a workflow

A workflow definition supplies a stable name, a semantic version, a typed context/result contract, and a method that builds an Agent Framework workflow graph.

The generic contract is:

```csharp
public interface IWorkflowDefinition<TContext, TResult> : IWorkflowDefinition
    where TContext : notnull
{
    string Name { get; }
    string Version { get; }

    ValueTask<Workflow> BuildAsync(
        WorkflowBuildContext context,
        CancellationToken cancellationToken);

    FailureDisposition Classify(WorkflowFailure failure);
}
```

A minimal definition looks like this:

```csharp
using Abacus.Run.Abstractions;
using Microsoft.Agents.AI.Workflows;

public sealed record GreetingContext(string Name);
public sealed record GreetingResult(string Message);

public sealed class GreetingWorkflow : IWorkflowDefinition<GreetingContext, GreetingResult>
{
    public string Name => "greeting";
    public string Version => "1.0.0";

    public ValueTask<Workflow> BuildAsync(
        WorkflowBuildContext context,
        CancellationToken cancellationToken)
    {
        var executor = context.Node(new GreetingExecutor("greet"));

        Workflow workflow = new WorkflowBuilder(executor)
            .WithName(Name)
            .WithOutputFrom(executor)
            .Build();

        return ValueTask.FromResult(workflow);
    }

    public FailureDisposition Classify(WorkflowFailure failure)
        => DefaultFailureClassifier.Instance.Classify(failure);
}
```

### What a definition can declare

`IWorkflowDefinition<TContext, TResult>` is the only required interface. Everything else is opt-in,
so a workflow pays for nothing it does not ask for.

| Interface | Declares | Section |
| --- | --- | --- |
| `IWorkflowDefinition<TContext, TResult>` | Name, version, context/result types, the graph | this section |
| `IAuditedWorkflowDefinition` | The shape of the workflow's own audit record | [Audit records](#workflow-audit-records) |
| `IDomainEventTriggeredWorkflow` | Topics that start an instance of this workflow | [Event broker](#event-broker-and-event-driven-workflows) |
| `INotifyingWorkflow` | Emission level, per-node overrides, SSE on/off, custom event names | [Events](#events-history-and-sse) |

Approval gates are not an interface — they are declared per node, inline in `BuildAsync`.

`WorkflowBuildContext` is what `BuildAsync` receives, and carries the run's identity as well as the
attachment methods:

| Member | Purpose |
| --- | --- |
| `InstanceId`, `TenantId` | This run's identity — useful for closures the executors capture |
| `WorkflowName`, `WorkflowVersion` | What the registry resolved |
| `Attempt` | 1 on the first run, higher after a retry |
| `Services` | The host's `IServiceProvider`; resolve brokers, clients, stores from it |
| `Audit` | The recorder, when the definition declares an audit record |
| `Node(executor, gate?)` | Attach a host executor, optionally gated |
| `RawNode(binding)` | Attach a raw framework or agent binding |
| `Gates`, `Nodes` | What this build declared; read by the runtime and the catalog API |

`BuildAsync` is called **once per run attempt**, not once at startup, so it is free to resolve
per-instance services or vary the graph by context. Keep it cheap and deterministic: the same
instance rebuilding a different graph on resume will not match its own checkpoint.

### Nodes

`WorkflowBuildContext.Node(...)` attaches a host executor: it wires the middleware pipeline, the
approval gate, the audit recorder and the per-instance runtime, and returns the `ExecutorBinding` the
graph is built from.

```csharp
ExecutorBinding validate = context.Node(new Validate("validate"));
```

The **executor id** is the identity everything else hangs off: gate policies are keyed by it, node
state is projected by it, per-node notification overrides name it, and the graph endpoint reports it.
Renaming a node in a published version silently orphans any tenant policy written against the old id
— change the workflow version instead.

`RawNode(...)` is the escape hatch for bindings the host did not create. Raw nodes participate in the
graph but run outside the executor middleware pipeline and **cannot be approval-gated** — passing a
gate block to `RawNode` throws rather than silently ignoring it, because a gate that quietly did
nothing would be worse than one that was refused.

The framework supplies several ways to make a binding, and `ExecutorBinding` has implicit conversions
from `Executor`, `AIAgent`, `RequestPort` and `string`:

| Binding | From |
| --- | --- |
| `executor.BindExecutor()` | A raw framework `Executor` |
| `agent.BindAsExecutor(id)` | An `AIAgent` — the agent becomes a node |
| `workflow.BindAsExecutor(id)` | Another `Workflow`, as a **sub-workflow** node |
| `handler.BindAsExecutor<TIn>(id)` | A bare `Func<TIn, IWorkflowContext, CancellationToken, ValueTask>` |

Prefer `Node(...)` with a `HostExecutor<TIn, TOut>` whenever middleware, gates, audit or notifications
are wanted — a raw node gets none of them.

### Built-in executors

| Executor | Shape | Purpose |
| --- | --- | --- |
| `TransformExecutor<TIn, TOut>` | `(id, Func<TIn, TOut>)` | Pure mapping |
| `DelegateExecutor<TIn, TOut>` | `(id, handler)` | General-purpose async work |
| `ApiCallExecutor` | `(id, ApiCallOptions, clientFactory)` | Templated HTTP call with egress control and idempotency key |
| `LlmExecutor` | `(id, LlmOptions, clientResolver, pricing?)` | Chat model call with structured output, streaming and cost |
| `DelayExecutor` | `(id, TimeSpan, ITimerService)` | Durable delay — checkpoints and halts rather than blocking |
| `HumanApprovalExecutor<T>` | `(id)` | Approval as an explicit node rather than node configuration |
| `FanInExecutor<TItem, TOut>` | `(id, aggregate)` | Aggregates a fan-in barrier's inputs |
| `PublishDomainEventExecutor<T>` | `(id, broker, topic, …)` | Publishes a domain message, passing input through |
| `WaitForDomainEventExecutor<TIn, TPayload>` | `(id, subscriptions, topicFilter, …)` | Parks until a matching message arrives |

Three of them behave in ways worth knowing before you reach for them:

- **`DelayExecutor` does not sleep.** It writes a timer row, checkpoints and halts, so the instance
  releases its lease. A 24-hour delay costs no execution capacity, and survives a restart. It needs
  an `ITimerService` from `context.Services`.
- **`PublishDomainEventExecutor<T>` passes its input through unchanged.** Publishing is a side effect on
  the way past, so the node drops into an existing edge without rewiring the graph around it.
- **`WaitForDomainEventExecutor` runs twice.** The first pass registers a durable subscription and parks;
  after delivery the runner resumes from the checkpoint, the executor runs again, finds its payload
  and returns it. Anything it does before parking therefore happens twice — keep it to registering
  the wait.

`ApiCallExecutor` and `LlmExecutor` bind their templates through the shared `TemplateEngine`, so
`{{ context.Field }}` resolves against the message the node received. `ApiCallExecutor` enforces the
egress allow-list, attaches an `Idempotency-Key`, and surfaces a non-success status as a typed
`ApiCallFailureException` rather than a generic HTTP error.

### Custom executors

Derive from `HostExecutor<TIn, TOut>` and implement `ExecuteCoreAsync`. `HandleAsync` is sealed
because gate evaluation and the middleware pipeline live there and must not be overridden away.

```csharp
public sealed class GreetingExecutor(string id) : HostExecutor<GreetingContext, GreetingResult>(id)
{
    protected override ValueTask<GreetingResult> ExecuteCoreAsync(
        GreetingContext input,
        IWorkflowContext context,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(new GreetingResult($"Hello, {input.Name}!"));
}
```

`TOut` is constrained to a reference type because the pause path returns `null` and the engine only
auto-sends non-null handler results — that is exactly what lets a gated or waiting executor park
without emitting a bogus message downstream.

Inside an executor, `Runtime` carries the per-instance context: `Runtime.InstanceId`,
`Runtime.TenantId`, `Runtime.Attempt`, `Runtime.CurrentSuperstep`, and the nullable hooks
`Runtime.Audit`, `Runtime.Notify` and `Runtime.Services`. Override `Metadata` to describe the node
for the graph endpoint.

### Edges

Edges come from the Agent Framework's `WorkflowBuilder`. The constructor takes the start node, and
`WithOutputFrom` names the node whose result becomes the workflow's result.

```csharp
Workflow workflow = new WorkflowBuilder(validate)
    .AddEdge(validate, enrich)                      // sequential
    .AddEdge<Order>(enrich, escalate,               // conditional: only when the predicate holds
        condition: order => order is { Amount: > 10_000m })
    .AddEdge<Order>(enrich, settle,
        condition: order => order is { Amount: <= 10_000m })
    .AddFanOutEdge(settle, [notifyOps, notifyCustomer])          // both targets
    .AddFanInBarrierEdge([notifyOps, notifyCustomer], complete)  // waits for every source
    .WithOutputFrom(complete)
    .WithName(Name)
    .Build();
```

| Method | Behaviour |
| --- | --- |
| `AddEdge(source, target)` | Unconditional |
| `AddEdge<T>(source, target, condition)` | Traversed only when the predicate holds for the message |
| `AddEdge(source, target, label, idempotent)` | Labelled for the graph view; `idempotent` permits re-adding the same edge |
| `AddFanOutEdge(source, targets)` | Sends to every target |
| `AddFanOutEdge<T>(source, targets, targetSelector)` | Sends to the subset the selector picks by index |
| `AddFanInBarrierEdge(sources, target)` | Target runs once every source has delivered |
| `WithOutputFrom(executor, …)` | Binds the workflow result; accepts several nodes |
| `Build(validateOrphans: true)` | Throws on a node no edge reaches — a typo, not a design |

Two conditional edges out of one node is how a branch is expressed; there is no separate switch
construct. Make the predicates exhaustive, or a message matching neither simply stops there and the
run completes with no output.

`FanInExecutor<TItem, TOut>` is the natural target of `AddFanInBarrierEdge`, since the barrier
delivers a list.

### Approval gates on a node

A node attached with no gate block runs autonomously. Pass one to require a human decision:

```csharp
ExecutorBinding settle = context.Node(new Settle("settle"), gate => gate
    .Mode(ExecutionMode.RequireApproval)
    .When<Order>(order => order.Amount > 25_000m)   // Conditional mode
    .Reason("RegulatedSettlement")
    .AssignTo("group:finance", "user:cfo")
    .RequireApprovers(2)
    .ExpiresAfter(TimeSpan.FromHours(8))
    .OnExpiry(ExpiryAction.Escalate, "group:exec")
    .AllowModification()
    .RequireSegregationOfDuties()
    .Locked());                                     // tenants may tighten, never weaken
```

Every gated node is reconfigurable per tenant at run time unless the author calls `.Locked()`. See
[Human approval gates](#human-approval-gates) for the decision flow and
[Tenant executor configuration](#tenant-executor-configuration) for precedence.

### Events on a node

Publishing and waiting are ordinary nodes; notifying is a call inside one. Resolve the broker and
subscription store from `context.Services`:

```csharp
var broker = context.Services!.GetRequiredService<IDomainEventBroker>();
var subscriptions = context.Services!.GetRequiredService<IDomainEventSubscriptionStore>();

ExecutorBinding publish = context.Node(new PublishDomainEventExecutor<OrderPlaced>(
    "publish-order-placed", broker, topic: "orders.placed", correlationKey: o => o.OrderId));

ExecutorBinding wait = context.Node(new WaitForDomainEventExecutor<Order, PaymentSettled>(
    "await-settlement", subscriptions, "payment.settled",
    correlationKey: o => o.OrderId, timeout: TimeSpan.FromDays(3)));
```

See [Events](#events-history-and-sse) and
[Event broker](#event-broker-and-event-driven-workflows).

### Failure classification

`Classify` decides what a thrown exception means for the instance.

| Disposition | Effect |
| --- | --- |
| `Retry` | Backoff and try again, until `MaxAttempts` or `MaxLifetimeHours` |
| `DeadStop` | Terminal. Retrying cannot help, so do not burn attempts discovering that |
| `Escalate` | Terminal, and flagged for operator attention |

`WorkflowFailure` carries `ExecutorId`, `Exception`, `AttemptCount`, `Superstep` and the executor's
`Metadata`, so a classifier can decide differently per node without inspecting message text.

The default classifier already handles the common cases — rate limits, overload and 5xx retry;
validation, structured-output and 4xx dead-stop — so a definition overrides it only where its own
domain disagrees, and delegates the rest:

```csharp
public FailureDisposition Classify(WorkflowFailure failure) => failure.Exception switch
{
    InsufficientFundsException  => FailureDisposition.DeadStop,   // retrying cannot help
    ReconciliationBreakException => FailureDisposition.Escalate,  // a human must look
    _ => DefaultFailureClassifier.Instance.Classify(failure)
};
```

Framework exceptions a classifier can match on: `WorkflowDeadStopException`,
`ApprovalRejectedException`, `WorkflowValidationException`, `StructuredOutputException`,
`ApiCallFailureException` (carries `StatusCode`, body excerpt and `Retry-After`),
`LlmRateLimitException`, `LlmOverloadedException`.

Throwing `WorkflowDeadStopException` from inside an executor is the direct way to say "this run is
over" without routing it through the classifier.

See [Retries and failure classification](#retries-and-failure-classification).

### Engine context inside an executor

`ExecuteCoreAsync` receives the Agent Framework's `IWorkflowContext`, which is separate from
`Runtime`: `Runtime` is what the host adds, `IWorkflowContext` is what the engine offers.

| Member | Purpose |
| --- | --- |
| `QueueStateUpdateAsync(key, value)` | Writes state that survives into the next checkpoint |
| `ReadStateAsync<T>(key)` / `ReadOrInitStateAsync<T>` | Reads it back after a resume |
| `RequestHaltAsync()` | Parks the run — the mechanism behind gates and event waits |
| `YieldOutputAsync(output)` | Emits a workflow output without being the terminal node |
| `SendMessageAsync(message, targetId)` | Sends to a specific node, bypassing edge routing |
| `AddEventAsync(workflowEvent)` | Raises an engine event, ordered with executor events |

Use `QueueStateUpdateAsync` rather than executor fields for anything that must survive a restart: an
executor instance is rebuilt on resume, and a field is gone with it.

### Middleware

Two seams, both registered at composition rather than declared by a workflow. Lower `Order` runs
earlier in the outer pipeline.

```csharp
public sealed class TimingMiddleware : IExecutorMiddleware
{
    public int Order => 10;

    // Narrow the scope; the default applies it to every node.
    public bool AppliesTo(ExecutorDescriptor descriptor) => descriptor.ExecutorId != "noisy";

    public async ValueTask InvokeAsync(
        ExecutorInvocationContext context, ExecutorDelegate next, CancellationToken ct)
    {
        long start = Stopwatch.GetTimestamp();
        await next(context, ct);
        // context.Output, context.Exception and context.Succeeded are all readable here.
        Record(context.Descriptor.ExecutorId, Stopwatch.GetElapsedTime(start), context.Succeeded);
    }
}
```

`IWorkflowMiddleware` wraps a whole run and sees `WorkflowInvocationContext` instead.
`ExecutorInvocationContext.Exception` is settable, so middleware can observe, replace or swallow a
failure as the pipeline unwinds — which is how retry-shaping and drift detection work without the
workflow knowing.

Built-in middleware comes from `AddBuiltInMiddleware()`: OpenTelemetry spans for runs and executors,
request/response logging, and LLM drift. See
[Registering workflows and middleware](#registering-workflows-and-middleware).

### A definition using all of it

```csharp
public sealed class OrderWorkflow
    : IWorkflowDefinition<OrderContext, OrderResult>,
      IAuditedWorkflowDefinition,
      IDomainEventTriggeredWorkflow,
      INotifyingWorkflow
{
    public string Name => "order";
    public string Version => "1.2.0";

    // Started by a domain message as well as by POST /workflows/order/instances.
    public IReadOnlyList<DomainEventTrigger> Triggers =>
        [new DomainEventTrigger { TopicFilter = "orders.placed" }];

    // Quiet by default; the interesting node stays loud. Streaming stays on.
    public NotificationPolicy Notifications { get; } = new()
    {
        Level = NotificationLevel.Lifecycle,
        ByNode = new Dictionary<string, NotificationLevel>(StringComparer.Ordinal)
        {
            ["settle"] = NotificationLevel.Standard
        },
        Emits = ["order.repriced"]
    };

    public AuditRecordDefinition AuditRecord { get; } = new(
        "order", "One order, as processed.",
        [
            new AuditSectionDefinition("submission", "What was submitted.", Multiple: false),
            new AuditSectionDefinition("step", "One processing step."),
            new AuditSectionDefinition("outcome", "How the run settled.", Multiple: false)
        ]);

    public ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken ct)
    {
        var subscriptions = context.Services!.GetRequiredService<IDomainEventSubscriptionStore>();

        ExecutorBinding validate = context.Node(new Validate("validate"));

        ExecutorBinding settle = context.Node(new Settle("settle"), gate => gate
            .When<OrderContext>(order => order.Amount > 25_000m)
            .Reason("AmountAboveThreshold")
            .AssignTo("group:finance")
            .RequireApprovers(2)
            .Locked());

        ExecutorBinding awaitPayment = context.Node(
            new WaitForDomainEventExecutor<OrderContext, PaymentSettled>(
                "await-settlement", subscriptions, "payment.settled",
                correlationKey: o => o.OrderId, timeout: TimeSpan.FromDays(3)));

        ExecutorBinding complete = context.Node(new Complete("complete"));

        return new ValueTask<Workflow>(new WorkflowBuilder(validate)
            .AddEdge(validate, settle)
            .AddEdge(settle, awaitPayment)
            .AddEdge(awaitPayment, complete)
            .WithOutputFrom(complete)
            .WithName(Name)
            .Build());
    }

    public FailureDisposition Classify(WorkflowFailure failure)
        => DefaultFailureClassifier.Instance.Classify(failure);
}
```

### Versioning rules that bite

- **Executor ids are the key for tenant gate policies**, and policies are stored per workflow
  *version*. A tenant's configuration does not carry forward to a new version, so a version bump
  starts from the author's declared gates again.
- **An in-flight instance keeps the version it started on.** The registry resolves by the instance's
  recorded version, so redeploying a new version never changes the shape of a run already underway.
- **`POST /instances/{id}/rerun` in restart mode creates the new instance at the *current* version**,
  which is the one case where a rerun can behave differently from the original.
- Two definitions registered with the same name and version fail startup rather than one silently
  winning.

## Workflow audit records

Events answer "what did the runtime do". An audit record answers "why is this result defensible" —
the plan a node formed, the input it worked from, the output it produced, and what it published.
Those are workflow-specific questions, so the framework supplies the hook and the storage but not the
schema.

The mechanism has three stages, and the separation between them is the point.

**1. The definition declares the shape.** A definition that keeps a record implements
`IAuditedWorkflowDefinition` and returns an `AuditRecordDefinition`: a root aggregate kind plus the
child sections that may hang off it.

```csharp
public sealed class ExampleWorkflowDefinition
    : IWorkflowDefinition<ExampleContext, ExampleResult>, IAuditedWorkflowDefinition
{
    public AuditRecordDefinition AuditRecord => ExampleAuditRecord.Definition;
}

public static class ExampleAuditRecord
{
    public const string RootKind = "example-workflow";

    // Section kinds are written into storage, so they are part of the workflow's contract —
    // name them as constants rather than repeating string literals at each call site.
    public const string Submission = "submission";
    public const string Plan = "plan";
    public const string Output = "output";

    public static readonly AuditRecordDefinition Definition = new(
        RootKind,
        "A workflow-specific audit record for the important steps in a run.",
        [
            new AuditSectionDefinition(Submission, "The input values and context used at start.", Multiple: false),
            new AuditSectionDefinition(Plan, "The plan the workflow formed before acting."),
            new AuditSectionDefinition(Output, "The resulting decision or artifact.")
        ]);
}
```

**2. The runtime hands every node a recorder.** `WorkflowRunner` reads the interface at build time
and, when an `IAuditRecordStore` is registered, constructs a `WorkflowAuditRecorder` bound to the
definition and the instance. Executors reach it through `HostExecutorRuntime.Audit`; a definition
wiring its own nodes reads `WorkflowBuildContext.Audit`. Both are nullable — a workflow that declares
no record gets none, and no executor needs to know which is the case.

```csharp
if (Runtime.Audit is { } audit)
{
    await audit.OpenAsync(input.RunId, attributes: null, cancellationToken);
    await audit.RecordAsync(ExampleAuditRecord.Output, item.Id, result, cancellationToken);
    await audit.CloseAsync(AuditRecordStatus.Completed, cancellationToken);
}
```

**3. Storage stays generic.** `IAuditRecordStore` holds a root row and a stream of entries whose
section kind is a string and whose payload is opaque JSON. A new workflow with a completely different
record needs no schema change. The framework default is `InMemoryAuditRecordStore`;
`Abacus.Run.Service` displaces it with a SQLite-backed store under `Abacus:AuditRecords`.

Guarantees the recorder makes:

| Rule | Reason |
| --- | --- |
| An undeclared section kind is logged and dropped | The declared shape is the contract, not a suggestion |
| Re-recording the same `(section, key)` replaces the entry | A retried executor corrects its record rather than contradicting it |
| Entries carry a monotonic sequence | Order of work survives storage that does not preserve insertion order |
| A store failure is swallowed and logged, never rethrown | An audit write explains work that already happened; failing the work because its explanation could not be filed trades a correct result for a missing one |
| `CloseAsync` before any `OpenAsync` writes nothing | A root with no identity is worse than no root |

Because a failed write is invisible to the workflow, order matters at the edges: record a publication
failure *before* letting it propagate, so the record explains the failure it caused.

The record is readable through the framework's own route — see
[Instances and diagnostics](#instances-and-diagnostics) — which projects the stored entries back
through the declared sections. Every declared section appears whether or not anything has been
recorded into it yet, so a caller reading a run in progress sees what is still outstanding as readily
as what is done.

### The worked example

`src/Abacus.Run.Service/Workflows/ExampleOrder` is a runnable version of everything above, registered
by the host as workflow `example-order`. The work it does is deliberately dull — plan, price each
line, total — because the point is the auditing around it.

| File | What it shows |
| --- | --- |
| `ExampleOrderAuditRecord.cs` | The declaration in one place, section kinds as constants because they are part of the workflow's contract |
| `ExampleOrderWorkflow.cs` | `OpenAsync` with attributes, a single-entry plan, per-line entries keyed by SKU, and both terminal paths |

Two details in it are worth copying rather than the shape of the record itself.

Per-line entries are keyed by SKU. Because re-recording the same `(section, key)` replaces, a retried
attempt corrects the record instead of appending a second, contradictory line. Keying by something
stable about the work — rather than leaving the key null or generating one per attempt — is what buys
that.

The failure path records the outcome *before* throwing:

```csharp
var failure = new WorkflowDeadStopException($"Line '{line.Sku}' cannot be priced.");

if (audit is not null)
{
    await audit.RecordAsync(ExampleOrderAuditRecord.Outcome, null,
        new { status = "Failed", failedSku = line.Sku, reason = failure.Message, total }, cancellationToken);
    await audit.CloseAsync(AuditRecordStatus.Failed, cancellationToken);
}

throw failure;
```

Start it, then read the record back:

```bash
curl -X POST http://localhost:5000/workflows/example-order/instances \
  -H 'Content-Type: application/json' \
  -d '{"context":{"orderId":"ORD-1","lines":[{"sku":"SKU-A","quantity":2,"unitPrice":10.50}]}}'

curl http://localhost:5000/workflows/example-order/instances/{id}/state
```

Adding `"failOnSku": "SKU-A"` to the context exercises the failure path. `ExampleOrderWorkflowTests`
in the integration suite covers both, along with the empty-section case and the fact that the host's
SQLite store — not the framework default — is what holds the result.

## Registering workflows and middleware

The API host uses a fluent registration builder:

```csharp
builder.Services
  .AddAbacus(builder.Configuration)
    .AddWorkflow<GreetingWorkflow>()
  .AddWorkflowMiddleware<CustomWorkflowMiddleware>()
  .AddExecutorMiddleware<CustomExecutorMiddleware>();
```

A prebuilt definition instance can also be registered:

```csharp
builder.Services
  .AddAbacus(builder.Configuration)
    .AddWorkflow(new GreetingWorkflow());
```

`AddAbacus(...)` registers the workflow host, built-in middleware, background services, control-plane UI, and the default in-memory stores. Set `Abacus:SqlServer:ConnectionString` to replace the in-memory stores with EF Core SQL Server implementations, and set `Abacus:Redis:ConnectionString` to enable Redis Streams, cross-replica control messages, and the cross-service event broker.

### Middleware ordering

Both middleware interfaces expose an `Order` property. Lower values run earlier in the outer pipeline. Workflow middleware wraps an entire run; executor middleware wraps a single host executor invocation.

```csharp
public sealed class CustomExecutorMiddleware : IExecutorMiddleware
{
    public int Order => 100;

    public bool AppliesTo(ExecutorDescriptor descriptor)
        => descriptor.ExecutorId.StartsWith("payment-", StringComparison.Ordinal);

    public async ValueTask InvokeAsync(
        ExecutorInvocationContext context,
        ExecutorDelegate next,
        CancellationToken cancellationToken)
    {
        await next(context, cancellationToken);
        // Inspect or transform context.Output and context.Exception here.
    }
}
```

Middleware can use `ExecutorInvocationContext.Items` and the shared `MiddlewareContextKeys` values for per-invocation data such as outbound call capture, LLM usage, and prompt version.

## Instance lifecycle

The control-plane status is a superset of the Agent Framework run status.

| Status | Meaning | Claimable | Terminal |
| --- | --- | --- | --- |
| `Pending` | Accepted but not yet claimed | Yes | No |
| `Running` | Actively executing | No | No |
| `AwaitingInput` | Waiting for an external workflow request | No | No |
| `AwaitingApproval` | Waiting for an approval decision | No | No |
| `Suspended` | Parked until explicitly resumed | No | No |
| `RetryScheduled` | Retryable failure is waiting for its next time | Yes | No |
| `Dispatchable` | Explicitly woken and ready to claim | Yes | No |
| `Completed` | Finished with a result | No | Yes |
| `Failed` | Failed after retry handling | No | Yes |
| `DeadStopped` | Business or policy failure is terminal | No | Yes |
| `Cancelled` | Cancellation completed | No | Yes |

A normal run follows this shape:

```text
Pending -> Running -> Completed
                   -> AwaitingApproval -> Dispatchable -> Running
                   -> AwaitingInput
                   -> RetryScheduled -> Running
                   -> Suspended -> Dispatchable -> Running
                   -> Failed / DeadStopped / Cancelled
```

```mermaid
stateDiagram-v2
    [*] --> Pending
    Pending --> Running: dispatcher claims
    RetryScheduled --> Running: retry due
    Dispatchable --> Running: dispatcher claims
    Suspended --> Dispatchable: resume
    AwaitingApproval --> Dispatchable: decision accepted
    Running --> Completed: workflow output
    Running --> AwaitingApproval: approval gate trips
    Running --> AwaitingInput: external request
    Running --> RetryScheduled: retryable failure
    Running --> Suspended: operator suspend
    Running --> Failed: retries exhausted
    Running --> DeadStopped: terminal failure
    Running --> Cancelled: cancellation
    AwaitingInput --> Dispatchable: response supplied
    Completed --> [*]
    Failed --> [*]
    DeadStopped --> [*]
    Cancelled --> [*]
```

Instances pin the workflow version used at creation. New starts resolve the highest registered version unless the caller supplies `?version=`. A resume attempts to resolve the exact pinned version; if it is unavailable, the instance is dead-stopped with `WorkflowVersionUnavailable`.

## Retries and failure classification

The workflow owns failure classification through `Classify(WorkflowFailure)`. The host applies the result after the engine reports an executor or workflow failure.

| Disposition | Host behavior |
| --- | --- |
| `Retry` | Schedule a retry with exponential backoff and configured jitter, unless attempts or lifetime are exhausted |
| `DeadStop` | Mark the instance `DeadStopped` and record the terminal reason |
| `Escalate` | Move the instance to `AwaitingInput` for external handling |

The default classifier treats transient transport, timeout, rate-limit, and server errors as retryable and validation, approval rejection, and ordinary client errors as terminal. A workflow can override this policy for business-specific cases such as duplicate payments.

Default retry settings:

- Maximum attempts: `5`
- Backoff base: `2` seconds
- Backoff cap: `300` seconds
- Jitter: `Full`
- Maximum lifetime: `24` hours

Retries must be designed for at-least-once execution. Side-effecting executors should use idempotency keys or an application-level deduplication strategy.

## Checkpoints and resumption

The host uses `ICheckpointStore<JsonElement>` through `CheckpointManager.CreateJson(...)`. The default cadence is `SuperStep`, which records a checkpoint after each completed superstep. Other modes are:

- `None`: no checkpoints; the workflow is not resumable from a process-loss point.
- `SuperStep`: checkpoint each superstep.
- `Manual`: the workflow chooses checkpoint boundaries through the framework context.

The overflow checkpoint store keeps small payloads inline and sends larger payloads through the blob store abstraction. The default inline threshold is 256 KiB.

Resumption rebuilds the workflow graph from the pinned definition and calls the framework resume path with the latest checkpoint. A crash after an external side effect but before checkpoint commit can cause that executor to run again. This is intentional at-least-once behavior, not exactly-once execution.

## Human approval gates

Approval gates are declared beside the executor binding:

```csharp
var payment = context.Node(
    new PaymentExecutor("submit-payment"),
    gate => gate
        .Mode(ExecutionMode.Conditional)
        .When<PaymentRequest>(request => request.Amount > 25_000m)
        .Reason("Payment exceeds approval threshold")
        .AssignTo("group:finance")
        .RequireApprovers(2)
        .ExpiresAfter(TimeSpan.FromHours(8))
        .OnExpiry(ExpiryAction.DeadStop)
        .AllowModification());
```

Gate modes:

- `Autonomous`: always proceed.
- `RequireApproval`: always create an approval before execution.
- `Conditional`: evaluate the configured predicate; only a tripped predicate pauses the executor.

When a gate trips:

1. The approval request is persisted.
2. An `approval.requested` event is emitted into the same instance event sequence as progress.
3. The executor does not run and the instance transitions to `AwaitingApproval`.
4. An authorized decision changes the approval state and wakes the instance.
5. The instance resumes from its checkpoint and the approved executor can run.

```mermaid
sequenceDiagram
  participant W as Workflow runner
  participant X as Host executor
  participant G as Gate evaluator
  participant P as Approval store
  participant B as Event sink
  participant H as Approver
  participant D as Dispatcher

  W->>X: Invoke executor
  X->>G: Evaluate gate
  G-->>X: Pause
  X->>P: Create pending approval
  X->>B: approval.requested
  X-->>W: Halt current run
  W-->>W: AwaitingApproval
  H->>P: POST decision
  P->>B: approval.decided
  P->>D: Mark instance dispatchable
  D->>W: Resume from checkpoint
  W->>X: Invoke approved executor
```

Decision outcomes are `Approve`, `Reject`, and `ApproveWithModification`. Modification is accepted only when `AllowModification` is enabled. Multiple approvers are supported through `RequireApprovers(...)`; the decision is accepted when quorum is reached.

Expiry actions are `DeadStop`, `Reject`, `AutoApprove`, and `Escalate`. Approval decisions enforce assignees, quorum, and optional segregation of duties.

A gate may also be declared with `.Locked()`, which prevents tenant configuration from weakening it. See [Tenant executor configuration](#tenant-executor-configuration).

## Tenant executor configuration

The gate declared in code is a default, not a fixed setting. Each tenant decides whether an executor runs autonomously or waits for approval, through the API and without a redeploy. An executor attached with `context.Node(executor)` and no gate block is autonomous for every tenant until someone changes it.

### Resolving the effective gate

The runner resolves each executor's gate for the instance's own tenant, highest precedence first:

| Precedence | Scope | Written by |
| --- | --- | --- |
| 1 | Per-instance override | `IGatePolicyStore.SetInstanceOverrideAsync` |
| 2 | Tenant policy | `PUT /workflows/{name}/versions/{version}/nodes` with a tenant |
| 3 | Host-wide policy | `IGatePolicyStore.SetAsync` with a null tenant |
| 4 | Workflow definition | The gate block in `BuildAsync` |
| 5 | Host default | `ExecutionMode.Autonomous` |

A policy-store failure falls back to the definition's gate, never to autonomous: a lookup error must not un-gate a protected executor. `GET` responses report which scope applied as `effectiveSource` (`tenant`, `host`, or `definition`).

### Locked gates

`.Locked()` marks a declared gate as the author's floor. Tenant configuration may still tighten it, but any of the following is refused with `409` and persists nothing:

- Downgrading `Mode` (`RequireApproval` or `Conditional` to `Autonomous`, or `RequireApproval` to `Conditional`).
- Lowering `RequiredApprovers` below the declared quorum.
- Enabling `AllowModification` where the declaration disabled it.
- Disabling `RequireSegregationOfDuties` where the declaration required it.
- Setting `OnExpiry` to `AutoApprove` where the declaration did not.

The same rules are re-applied when the gate is read at execution time, so a policy that reached the store before the gate was locked — or through a store client rather than the API — still cannot un-gate the executor. A locked `Conditional` gate also keeps its predicate, because a predicate is code and no stored policy can supply one.

### Scope and lifetime

Policies are keyed by tenant, workflow name, and workflow **version**, because executor ids and gates change between versions. A tenant's configuration does not carry forward when a new version is registered; instances of the new version run under its declared defaults until configured. `POST /instances/{id}/rerun` in restart mode creates the new instance at the current version, so a restart after a version bump uses that version's configuration.

Configuration writes are recorded in the audit store as `gate.policy.set` and `gate.policy.reset`, with the actor, tenant, workflow, version, and executor.

### Discovering nodes

`GET /workflows/{name}/versions/{version}/nodes` builds the definition once against an inspection context — attaching nothing to a runtime and running no executor — and caches the result per version. Each node reports its executor id, implementation type, input and output types, whether it is configurable, whether it is locked, and its declared, tenant, and effective gates. `RawNode(...)` bindings are listed with `configurable: false`; they run outside the executor middleware pipeline and cannot be approval-gated.

## Events, history, and SSE

The system carries two kinds of events that share a word and almost nothing else.

A **notification** describes what a run is doing. It is keyed by instance, ordered by a gapless sequence, and delivered to whoever happens to be watching. It never affects execution — lose one and a dashboard is briefly out of date. That is this section.

A **domain event** describes what happened in the business. It is keyed by topic, routed to whoever declared interest, and it *causes* execution — lose one and work that should have happened never does. That is [Event broker and event-driven workflows](#event-broker-and-event-driven-workflows).

The asymmetry in cost is why the two are built differently: notifications are best-effort fan-out over a durable log, while broker delivery is a durable state transition. A domain event may cause a notification; a notification may never cause work.

Every durable instance event has a monotonically increasing per-instance `Sequence`. The same sequence is used as the SSE event ID, which lets clients reconnect with `Last-Event-ID` and request replay from the same cursor.

Important event types include:

- `workflow.started`
- `executor.invoked`
- `executor.completed`
- `executor.failed`
- `superstep.completed`
- `approval.requested`
- `approval.decided`
- `approval.expired`
- `request.pending`
- `llm.completed`
- `llm.delta` (transient — see below)
- `custom.<name>` (workflow-defined — see below)
- `event.triggered`, `event.delivered`, `event.wait_expired`
- `workflow.output`
- `workflow.terminated`
- `instance.cancelled`
- `instance.rerun_requested`
- `instance.suspended`
- `instance.resumed`
- `heartbeat`

Use event history for polling, audit views, and recovery. Use SSE for live progress. Approval events use the same event stream as workflow progress; consumers do not need a separate subscription to observe a gate trip.

### Emitting your own notifications

A node reports something the framework cannot describe on its behalf through `Runtime.Notify`, which is nullable in the same way `Runtime.Audit` is — an executor exercised outside a host gets null and costs nothing:

```csharp
if (Runtime.Notify is { } notify)
{
    await notify.NotifyAsync("documents.scanned", new { count = 3 }, cancellationToken);
}
// → event: custom.documents.scanned
```

The `custom.` prefix is applied by the runtime and cannot be opted out of, so a workflow can never shadow a framework event however it names its own, and a consumer can filter the whole class on the prefix without knowing any workflow's vocabulary. A name that is empty, contains whitespace, or has an empty segment throws at the call site — a malformed event type is indistinguishable from an event that was never sent.

Payloads pass through the same redaction as every other event.

### Controlling what a run emits

A workflow that emits `executor.invoked` and `executor.completed` for every node of a wide fan-out can be the dominant write volume in a deployment. A definition states its own policy by implementing `INotifyingWorkflow`; one that says nothing keeps today's behaviour and pays nothing for the feature.

```csharp
public sealed class BulkWorkflow : IWorkflowDefinition<Ctx, Result>, INotifyingWorkflow
{
    public NotificationPolicy Notifications { get; } = new()
    {
        Level = NotificationLevel.Lifecycle,        // supersteps, but no per-node chatter
        ByNode = new Dictionary<string, NotificationLevel>(StringComparer.Ordinal)
        {
            ["reconcile"] = NotificationLevel.Standard   // except this one, which stays loud
        },
        Emits = ["documents.scanned"]               // advertised by the catalog API
    };
}
```

| Level | Emits |
| --- | --- |
| `Minimal` | Start, output and terminal only |
| `Lifecycle` | Adds superstep boundaries |
| `Standard` | Adds `executor.*`, `llm.*` and `custom.*`. The default |

`ByNode` works in both directions: it can quiet one node in a `Standard` workflow or keep one node loud in a `Minimal` one.

### Turning SSE off for a workflow

`Level` controls *what* is emitted. `StreamEvents` controls whether it is also streamed live.

**The event log is not optional.** Every event a workflow emits is written to the durable log, and no
setting turns that off. The only delivery choice a workflow has is whether those same events are
*also* pushed to SSE subscribers as they happen:

```csharp
public NotificationPolicy Notifications { get; } = new()
{
    StreamEvents = false   // still logged in full; simply not streamed
};
```

| `StreamEvents` | Durable log | Live SSE |
| --- | --- | --- |
| `true` *(default)* | Always | Yes |
| `false` | Always | No |

Turning it off suits a run nobody watches as it happens — a nightly batch, or work whose events are
read afterwards for reconciliation. Observability is not reduced, only its timeliness: events are
still sequenced, still redacted, still carry the workflow name, and are read in full at
`GET /v2/workflows/{name}/instances/{id}/events`.

Two consequences worth knowing:

- **The SSE endpoint refuses.** `GET /instances/{id}/events` returns `409` with a problem detail
  pointing at the v2 route, rather than holding a stream open that will never produce anything. An
  empty stream is indistinguishable from a stalled run, and a client waiting on one has no way to
  tell.
- **Streamed tokens disappear entirely.** `llm.delta` is the one kind of event with no durable record
  by design — a rendered token has no replay value — so with streaming off it has nowhere left to go.
  That is the correct reading, a workflow that has opted out of streaming has opted out of streamed
  tokens too, but it does make `StreamDeltas = true` alongside `StreamEvents = false` a combination
  that produces no deltas anywhere.

`EventDeliveryMode` appears on `EventEnvelope` as the runtime's own record of where a given event
went. It is not a menu a workflow picks from — a workflow sets `StreamEvents`, and the runtime
derives the rest.

Two rules keep the policy safe. **Terminal events are never suppressible** — a subscriber's stream closes on `workflow.terminated`, and `approval.*`, `instance.*` and `event.*` are control-plane and broker facts rather than run chatter. And **filtering happens before the sequence number is taken**: a suppressed event that had consumed one would leave a hole in the gapless sequence, and `Last-Event-ID` catch-up would wait forever for an event that is never coming.

Declared `Emits` names are validated at startup and surfaced on `GET /workflows/{name}`, so a consumer discovers the vocabulary rather than reverse-engineering it.

### Transient events

`llm.delta` is fanned out to live subscribers and **never appended to the durable store**. A token already rendered has no replay value, and the complete text is in the executor's output either way.

A transient event takes no sequence number and is written to SSE without an `id:` field. That keeps the durable sequence gapless, and it leaves a reconnecting client's `Last-Event-ID` pinned to the last durable event — so a reconnect delivers the stored history plus whatever is streaming now, and never waits for a chunk that no longer exists. A token stream is not resumable, and the transport says so.

Streaming is opt-in per node via `LlmOptions.StreamDeltas`, which defaults to `false`.

### LLM telemetry

An `LlmExecutor` emits one `llm.completed` per invocation — a model call is one fact, not a stream of them:

```
event: llm.completed
data: {"executorId":"classify","model":"claude-sonnet-5","promptVersion":"v3",
       "inputTokens":1840,"outputTokens":212,"costUsd":0.0084,
       "elapsedMs":1240,"finishReason":"Stop","attempt":1,"streamed":false}
```

Turn it off for a node with `LlmOptions.EmitCompletion = false`. The prompt and the response text are deliberately absent: they already travel the executor's input/output path where redaction applies, and repeating them here would put model output on a stream a UI reads.

The same numbers go to `ILogStore` as a per-instance usage entry and to OpenTelemetry as metrics, where `LlmDriftMiddleware` compares them against a rolling baseline. Cost is one of the drift signals, and it catches what token counts alone miss — a provider routing to a pricier model, or a prompt that has quietly grown.

Cost needs a price table (see [Configuration](#configuration)). An unpriced model reports `null`, never zero, and unpriced samples are excluded from the cost baseline rather than counted: a zero would average in as a real observation and make a genuine rise afterwards look smaller than it is.

## Event broker and event-driven workflows

The broker is the other channel: a workflow publishes a message to a topic, and another workflow either **starts** because of it or **wakes up** because of it. The publisher does not know who is listening, and the listener does not know who published.

**Delivery is a durable state transition, not a message.** A trigger match creates an instance row; a wait match writes the payload to a subscription row and marks the instance `Dispatchable`, which the ordinary dispatcher then claims exactly as it claims work released by an approval. Nothing is held in memory waiting to be acted on, so an event-driven pipeline survives a restart. The transport only makes that transition fast; it is never what makes it happen.

### Publishing

```csharp
ExecutorBinding publish = context.Node(new PublishDomainEventExecutor<OrderPlaced>(
    "publish-order-placed",
    context.Services!.GetRequiredService<IDomainEventBroker>(),
    topic: "orders.placed",
    correlationKey: o => o.OrderId));
```

The node passes its input through unchanged, so it drops into an existing edge without rewiring the graph around it. Messages carry the publishing instance's tenant and id for provenance.

### Subscribing

A workflow subscribes in one of two ways.

**Trigger** — a matching message starts a new instance, with the payload as its context:

```csharp
public sealed class ShipOrderWorkflow : IWorkflowDefinition<OrderPlaced, ShipmentResult>, IDomainEventTriggeredWorkflow
{
    public IReadOnlyList<DomainEventTrigger> Triggers =>
        [new DomainEventTrigger { TopicFilter = "orders.placed" }];
}
```

**Wait** — the instance parks mid-run until a matching message arrives, then resumes with the payload:

```csharp
ExecutorBinding wait = context.Node(new WaitForDomainEventExecutor<PaymentContext, PaymentSettled>(
    "await-settlement",
    context.Services!.GetRequiredService<IDomainEventSubscriptionStore>(),
    topicFilter: "payment.settled",
    correlationKey: c => c.OrderId,
    timeout: TimeSpan.FromDays(3),
    onExpiry: WaitExpiryAction.DeadStop));
```

A wait costs nothing while it waits. The executor registers a durable subscription and halts, and the instance checkpoints and releases its lease — the same park mechanism an approval gate uses. A workflow can wait days on a settlement without holding an execution slot.

### Topics

Topics are dot-delimited. Subscriber filters may use `*` for exactly one segment and `#` for the trailing remainder; published topics may not use either.

| Filter | Matches | Does not match |
| --- | --- | --- |
| `orders.placed` | `orders.placed` | `orders.shipped` |
| `orders.*` | `orders.placed` | `orders`, `orders.eu.placed` |
| `orders.#` | `orders`, `orders.eu.west.placed` | `payments.placed` |

Wildcards must be whole segments — `order*` is rejected rather than quietly treated as a literal, because a filter that matches nothing looks identical to an upstream that published nothing. Matching is ordinal and case-sensitive.

### Local by default, global by declaration

Scope travels on the message, not on the call site and not on whichever transport happens to be registered.

| Scope | Reach |
| --- | --- |
| `Local` *(default)* | Stays inside the publishing service; a private implementation detail of it |
| `Distributed` | Crosses the service boundary, for pub/sub between separately deployed services |

The same publishing code is therefore correct in a single service and in a fleet, and registering a distributed transport widens what a publisher *may* do without changing what any existing publisher does.

| Transport | Reach | Competing consumers | Replay | Dead letter |
| --- | --- | --- | --- | --- |
| `InProcessDomainEventBroker` *(default)* | This service | Yes | No | No |
| `RedisEventBroker` *(`AddRedisEventBroker`)* | Every service | Yes | Yes | Yes |
| `RabbitMqDomainEventBroker` *(`AddRabbitMqDomainEventBroker`)* | Every service | Yes | No | Yes |

Each distributed broker is the in-process broker *plus a wire*, not a second implementation. Local messages never leave the process. Distributed ones go onto the transport and come back to every service through its own consumer, including the publisher's own — publishing does not also deliver locally, because that would deliver twice. Consumer groups carry the distinction between routing work and observing it: a named `ConsumerGroup` means exactly one member of the fleet handles each message, an unnamed one gets a private group and sees its own copy.

The two differ in where filtering happens and in what they can honestly claim:

- **Redis** publishes to one stream and filters client-side, so a subscriber is woken for traffic it then discards. It can replay, because a stream retains.
- **RabbitMQ** publishes to a topic exchange and filters server-side by routing key, so a subscriber is only woken for what it asked for. It cannot replay — a queue holds what arrives after it is bound — and `SupportsReplay` says so rather than quietly behaving as `Now`. Dead-lettering is native.

The topic vocabularies happen to agree: AMQP's `*` is one word and `#` is the remainder, which is exactly what `TopicPattern` means, so filters pass through unchanged.

Register one or the other, not both — the second registration replaces the first.

Publishing `Distributed` against an in-process broker fails invisibly — the message still reaches every local subscriber and simply never leaves the host. So a broker publishes a `DomainEventBrokerCapabilities` record and an impossible combination is rejected at composition time: a mismatched executor throws in its constructor, and `POST /events` returns `400` rather than `202`.

### Operating it

Broker activity is reported onto the instance's own event stream as `event.triggered`, `event.delivered` and `event.wait_expired`, so an operator watching a run sees the domain activity that moved it inline with everything else. The instance detail page shows a **Waiting on** panel naming the topic, the blocked node, the correlation key and the expiry — `AwaitingInput` with no stated cause is the worst version of this feature.

`GET /subscriptions` lists what is listening and what is waiting.

## HTTP API

The API is implemented in `src/Abacus.Run/Api/Endpoints.cs`. Error responses use ASP.NET Core problem details and validation problem details where applicable.

### Health

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/health/live` | Process liveness |
| `GET` | `/health/ready` | Host readiness |

### Workflow catalog and start

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/workflows` | List registered workflow names and versions |
| `GET` | `/workflows/{name}` | List versions for one workflow |
| `POST` | `/workflows/{name}/instances` | Create a workflow instance |

A start request body contains a workflow-specific context object. Optional request controls include:

- `version` query parameter for an exact workflow version.
- `Idempotency-Key` header to replay a prior start without creating a second instance.
- `Prefer: wait=<seconds>` header to wait for a terminal result, capped by the host.
- Tenant and correlation values supplied through the host's request metadata conventions.

A successful asynchronous start returns `202 Accepted` and a location such as `/instances/{id}`. Unknown workflows return `404`; invalid context returns `400` with field errors.

### Executor nodes and tenant policy

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/workflows/{name}/versions/{version}/nodes` | List executor nodes with declared, tenant, and effective gates |
| `PUT` | `/workflows/{name}/versions/{version}/nodes` | Configure several nodes in one all-or-nothing write |
| `PUT` | `/workflows/{name}/versions/{version}/nodes/{executorId}` | Configure one node |
| `DELETE` | `/workflows/{name}/versions/{version}/nodes/{executorId}` | Drop the tenant's override for one node |

The tenant is taken from the `X-Tenant-Id` header or the `tenant_id` claim. `version` accepts a concrete version or `latest`.

A policy body requires `mode` (`autonomous` or `requireApproval`; `conditional` is code-only) and optionally `reason`, `assignees`, `requiredApprovers`, `expirySeconds`, `onExpiry`, `escalationAssignees`, `allowModification`, and `requireSegregationOfDuties`. Omitted fields inherit the declared gate.

```bash
curl -X PUT http://localhost:5000/workflows/order/versions/1.0.0/nodes/submit-payment \
  -H 'X-Tenant-Id: acme' -H 'Content-Type: application/json' \
  -d '{"mode":"requireApproval","assignees":["group:finance"],"requiredApprovers":2}'
```

Unknown workflow, version, or executor returns `404`. A raw node or an unsupported mode returns `400`. A policy that would weaken a locked gate returns `409`. See [Tenant executor configuration](#tenant-executor-configuration) for precedence and locking rules.

### Instances and diagnostics

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/instances/{id}` | Retrieve instance state |
| `GET` | `/instances` | Query instances by status, workflow, correlation ID, limit, and offset |
| `GET` | `/instances/{id}/graph` | Retrieve the workflow graph representation |
| `GET` | `/instances/{id}/logs` | Query instance logs |
| `GET` | `/instances/{id}/checkpoints` | Inspect checkpoint metadata |
| `GET` | `/instances/{id}/events/history` | Read persisted event history |
| `GET` | `/instances/{id}/events` | Subscribe to live SSE events |
| `GET` | `/v2/workflows/{name}/instances/{id}/events` | The event log, scoped and attributed by workflow |
| `GET` | `/workflows/{name}/instances/{id}/state` | Lifecycle status plus the workflow's own audit record |

`/v2/workflows/{name}/instances/{id}/events` is the read path for a workflow configured
[log-only](#turning-sse-off-for-a-workflow), and equally valid for a streaming one — the same rows
either way. Like the instance-state route it is scoped by workflow name, so a caller states which
workflow they believe they are reading and a mismatch returns `404` rather than being silently
accepted. `from`, `to`, `limit` and `types` all apply.

The response carries the instance and workflow context once, and each row repeats `instanceId` and
`workflowName` so events collected across several instances keep their attribution:

```json
{
  "instanceId": "01J...", "workflowName": "nightly-reconcile",
  "workflowVersion": "1.0.0", "tenantId": "acme",
  "total": 12, "nextCursor": null,
  "items": [
    { "instanceId": "01J...", "workflowName": "nightly-reconcile", "sequence": 7,
      "eventType": "custom.batch.processed", "executorId": "reconcile", "superstep": 2,
      "payloadJson": { "rows": 500 }, "occurredAt": "2026-08-17T09:00:00Z" }
  ]
}
```

Payloads are re-emitted as JSON values rather than escaped strings, so a caller reads them directly
instead of parsing twice.

`GET /instances/{id}/events` returns `409` for a log-only workflow, with a problem detail pointing at
the v2 route. Holding a stream open that will never produce anything is worse than refusing: a client
cannot tell it apart from a stalled run.

`/workflows/{name}/instances/{id}/state` is the one instance route scoped by workflow name, because
what it returns is shaped by that workflow's declaration. A mismatched name is a wrong URL rather
than a different resource, so it returns `404` rather than the instance. `audit` is `null` when the
workflow declares no record. `?section=` narrows the response to named sections:

```bash
curl 'http://localhost:5000/workflows/example-workflow/instances/{id}/state?section=plan,output'
```

```json
{
  "instance": { "instanceId": "…", "status": "Completed", "workflowVersion": "1.0.0" },
  "audit": {
    "rootKind": "example-workflow",
    "rootKey": "RUN-4471",
    "status": "Completed",
    "attributes": { "requestId": "…" },
    "sections": [
      {
        "kind": "output",
        "description": "The resulting decision or artifact.",
        "multiple": true,
        "entries": [
          { "key": "OUT-001", "sequence": 12, "recordedUtc": "…", "payload": { "decision": "Approved" } }
        ]
      }
    ]
  }
}
```

Payloads are re-emitted as JSON rather than as escaped strings, and sections the workflow has not yet
recorded into come back with an empty `entries` array rather than being omitted.

### Instance controls

| Method | Path | Purpose |
| --- | --- | --- |
| `POST` | `/instances/{id}/cancel` | Request cancellation |
| `POST` | `/instances/{id}/retry` | Force a retry of a retryable/failed instance |
| `POST` | `/instances/{id}/rerun` | Create a new run based on an existing instance |
| `POST` | `/instances/{id}/suspend` | Park an instance |
| `POST` | `/instances/{id}/resume` | Wake a suspended or waiting instance |

Control operations are instance-state dependent. The API returns conflict responses when an operation cannot be applied to the current state.

### Approvals

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/approvals` | Query approvals |
| `GET` | `/approvals/{approvalId}` | Retrieve one approval |
| `GET` | `/instances/{id}/approvals` | List approvals for an instance |
| `POST` | `/approvals/{approvalId}/decision` | Submit an approval decision |

### Domain events

| Method | Path | Purpose |
| --- | --- | --- |
| `POST` | `/events` | Publish a domain message to a topic |
| `GET` | `/subscriptions` | List triggers and waits |

`POST /events` takes `{ "topic", "payload", "correlationKey", "scope" }`. `scope` defaults to `Local`; `Distributed` is refused with `400` when no distributed broker is registered, because accepting it would mean the other service silently never hears about it. An `Idempotency-Key` header becomes the message id, so a retried publish is the same message rather than a second one.

```bash
curl -X POST http://localhost:5000/events \
  -H 'Content-Type: application/json' -H 'X-Tenant-Id: acme' \
  -d '{"topic":"payment.settled","payload":{"orderId":"ORD-1","amount":250.50},"correlationKey":"ORD-1"}'
```

`GET /subscriptions` filters on `instanceId`, `topic`, `kind` (`Trigger` or `Wait`), `pendingOnly` and `limit`. Delivered payloads are deliberately absent from the response: they are domain data already redacted on the way to the event stream, and repeating them here would undo that.

## Configuration

Configuration is bound from the `WorkflowHost` section. Defaults are defined in `WorkflowHostOptions`.

```json
{
  "WorkflowHost": {
    "ReplicaId": "api-1",
    "MaxConcurrentInstances": 100,
    "ClaimBatchSize": 10,
    "PerWorkflowConcurrency": {
      "payments": 10
    },
    "Lease": {
      "DurationSeconds": 60,
      "RenewalSeconds": 20
    },
    "Drain": {
      "GraceSeconds": 45
    },
    "Checkpoint": {
      "Cadence": "SuperStep",
      "InlineThresholdBytes": 262144
    },
    "Retry": {
      "MaxAttempts": 5,
      "BackoffBaseSeconds": 2,
      "BackoffCapSeconds": 300,
      "Jitter": "Full",
      "MaxLifetimeHours": 24
    },
    "Approvals": {
      "DefaultExpiryHours": 24,
      "SweepIntervalSeconds": 30,
      "PolicyCacheSeconds": 30
    },
    "Events": {
      "BatchSize": 200,
      "ChannelCapacity": 10000,
      "RetentionDays": 90
    },
    "Sse": {
      "HeartbeatSeconds": 15,
      "MaxSubscribersPerInstance": 50
    },
    "Retention": {
      "InstanceDays": 90,
      "CheckpointDays": 7,
      "AuditDays": 365
    },
    "Logging": {
      "BodySampleRate": 0.1,
      "MaxBodyBytes": 32768,
      "BodyFieldAllowList": []
    },
    "Drift": {
      "BaselineWindowDays": 7,
      "MinSamples": 200,
      "EmbeddingSampleRate": 0.05,
      "SustainedWindowMinutes": 15,
      "SigmaThreshold": 3.0
    },
    "Egress": {
      "Enforce": true,
      "AllowedHosts": ["api.example.com"]
    }
  }
}
```

### Host-supplied sections

`WorkflowHost` is the framework's own section. `Abacus.Run.Service` reads further sections that
select the concrete infrastructure it substitutes for the in-memory defaults:

```json
{
  "Abacus": {
    "AuditRecords": { "ConnectionString": "Data Source=./data/abacus-audit.db" },
    "SqlServer": { "ConnectionString": "", "EnsureDatabaseCreated": false },
    "Redis": { "ConnectionString": "", "MaxStreamLength": 10000, "MaxBrokerStreamLength": 100000 },
    "RabbitMq": { "ConnectionString": "" },
    "Llm": {
      "Pricing": {
        "claude-sonnet-5": { "InputPerMillion": 3.00, "OutputPerMillion": 15.00 }
      }
    }
  }
}
```

`Abacus:Redis:MaxBrokerStreamLength` is larger than `MaxStreamLength` because one broker stream
carries every topic for the whole deployment, while the event streams are per instance.

`Abacus:RabbitMq:ConnectionString` is an AMQP URI. Setting it selects RabbitMQ as the event broker in
place of Redis Streams; the Redis event bus, which is a separate concern, is unaffected. Setting both
Redis and RabbitMQ therefore gives you Redis for SSE fan-out and RabbitMQ for domain messages, which
is a legitimate deployment rather than a misconfiguration.

`Abacus:Llm:Pricing` is what turns token counts into cost on `llm.completed` and into a drift signal.
A model with no entry reports `null` rather than zero — "we do not know" and "it was free" are
different facts, and conflating them would drag the cost baseline down and mask a later rise.

`Abacus:AuditRecords:ConnectionString` backs the generic audit-record store with SQLite and defaults
to `Data Source=./data/abacus-audit.db`; the directory is created at startup and the migrations are
applied by a hosted service. The connection string is resolved from `IOptions` inside the context
factory rather than read at registration time, so a test host's configuration override actually
applies — reading it at wire time silently binds every host to the deployed database file. Remove the
`AddSqliteAuditRecords` call to keep the framework's in-memory default.

### Important production settings

- Set a stable `ReplicaId` when running multiple hosts.
- Match lease duration and renewal intervals to expected executor latency.
- Keep checkpoints shorter-lived than terminal instance records unless audit requirements say otherwise.
- Set an explicit egress allowlist for outbound API executors.
- Keep `Egress.Enforce` enabled outside local development.
- Add only safe fields to `Logging.BodyFieldAllowList`.
- Preserve the default header deny list for authorization and cookie material.
- Size event channel capacity and SSE subscriber limits for expected fan-out.

## Security and data handling

### Egress protection

`EgressGuard` rejects non-HTTP(S) schemes, relative URLs, internal address literals, and hosts outside the configured allowlist. It also blocks loopback, private, link-local, metadata, and other internal address ranges. Wildcard allowlist entries such as `*.example.com` match subdomains but not the bare domain.

Keep the guard enabled for API and LLM executors. Disabling enforcement is intended only for controlled local development.

### Redaction and logging

The built-in request/response logging middleware samples bodies and applies an allowlist. Sensitive headers such as `Authorization`, `Cookie`, `Set-Cookie`, `x-api-key`, and `Proxy-Authorization` are denied by default. Treat context, result, approval input, and logs as sensitive data in a real deployment.

### Tenancy and authorization

Instances, approvals, and gate policies carry a tenant identifier. The API and store implementations must enforce tenant isolation at the request boundary and persistence boundary. The in-memory reference stores model the contract but are not a replacement for a production identity and authorization system.

Because gate policies decide whether an executor runs without a human decision, the node configuration endpoints are privileged: authorize them for tenant administrators rather than for anyone who can start an instance. The host derives the tenant from `X-Tenant-Id` when no `tenant_id` claim is present, which is a development convenience — in production, bind the tenant to the authenticated principal so a caller cannot configure another tenant's workflows by setting a header.

### Secrets

Do not put API keys or connection strings in workflow context payloads, Docker layers, or repository files. Use the deployment platform's secret/configuration mechanism and inject services into executors through `WorkflowBuildContext.Services` when needed.

## Operations

### Readiness and liveness

Use `/health/live` for process liveness and `/health/ready` for routing traffic only when the host is ready to accept work. Configure the container or orchestrator to probe port 8080.

### Deployment shape

```mermaid
flowchart LR
  LB[Load balancer] --> Api1[API replica 1]
  LB --> Api2[API replica 2]
  Api1 --> Shared[(Shared durable stores)]
  Api2 --> Shared
  Api1 --> Bus[Event bus]
  Api2 --> Bus
  Bus --> Clients[SSE clients]
  Api1 -. default development .-> Memory1[(In-memory stores)]
  Api2 -. process-local only .-> Memory2[(In-memory stores)]
```

The shared-store branch is the production target. The in-memory branch is useful for local development but cannot coordinate independent processes.

### Graceful drain

The dispatch layer has a drain service. During shutdown it stops new lease claims, waits for active instances up to the configured grace period, and then completes shutdown. Configure `Drain.GraceSeconds` to cover normal in-flight work without making deployments unnecessarily slow.

### Observability

Built-in middleware provides:

- OpenTelemetry workflow and executor instrumentation.
- Request/response logging with body sampling and redaction.
- LLM drift monitoring through a baseline store and alert sink.

Use instance events for business progress and state transitions; use logs and traces for diagnostic detail. The event sequence is the durable consumer cursor, not a replacement for metrics.

### Retention

The options distinguish instance, checkpoint, event, and audit retention. With in-memory stores, retention is process-local. Durable implementations should enforce the same policy with scheduled cleanup jobs and should preserve enough event history for operational replay and audit requirements.

## NuGet package

The framework library ships as `Abacus.Run` on GitHub Packages. The deployable host
(`Abacus.Run.Service`) is a reference implementation and is deliberately not packaged — it exists to
show how to wire concrete infrastructure and to host the control-plane UI.

```bash
dotnet add package Abacus.Run --version 1.0.0
```

Packaging is declared in `src/Abacus.Run/Abacus.Run.csproj`. The root `Directory.Build.props` sets
`IsPackable=false` so a solution-level `dotnet pack` emits exactly one package; the library opts back
in. The package embeds its own README and the MIT licence, ships XML documentation, and produces a
`.snupkg` symbol package alongside.

Two workflows publish the package.

`.github/workflows/ci.yml` publishes a prerelease on every push to `master`. Its `publish` job runs
after `build-test` and is gated to `master` pushes on this repository, so pull requests, feature
branches, and forks build and test without ever pushing a package. The version is the GitVersion
value plus a `-ci.<run_number>` suffix, which keeps a plain version number meaning "tagged release"
and orders every CI build below it. The job packs without `--no-build` so the assembly and the
package carry the same version.

`.github/workflows/publish-package.yml` publishes stable versions on `v*` tags or manual dispatch,
resolving the version from the workflow input, then the tag, then the `<Version>` in the project
file.

Both push with `--skip-duplicate` (GitHub Packages rejects overwriting a published version, so
re-runs stay idempotent) and `--no-symbols` (the feed does not accept `.snupkg`), and both upload the
package as a build artifact before pushing.

To verify a package locally before publishing:

```bash
dotnet pack src/Abacus.Run -c Release -o artifacts
```

## Containers and GHCR

The root `Dockerfile` is a multi-stage build:

1. Restore and publish `src/Abacus.Run.Service/Abacus.Run.Service.csproj` with the .NET 9 SDK image.
2. Copy the publish output into the .NET 9 ASP.NET runtime image.
3. Listen on port 8080 and run `Abacus.Run.Service.dll`.

Build and run locally:

```bash
docker build -t abacus-run:local .
docker run --rm -p 8080:8080 abacus-run:local
curl http://localhost:8080/health/live
```

The workflow `.github/workflows/container.yml` publishes to:

```text
ghcr.io/codeshayk/abacus-run
```

It authenticates with the workflow `GITHUB_TOKEN` and requires `packages: write`. It runs on pushes to `master`, `v*` tags, and manual dispatch. Tags include `latest` on `master`, the Git tag on version-tag pushes, and an immutable `sha-...` tag.

```mermaid
flowchart LR
  Push[Push to master or v* tag] --> Actions[GitHub Actions]
  Manual[Manual dispatch] --> Actions
  Actions --> Buildx[Docker Buildx]
  Buildx --> Login[GHCR login with GITHUB_TOKEN]
  Login --> Image[ghcr.io/codeshayk/abacus-run]
  Image --> Latest[latest on master]
  Image --> Version[version tag]
  Image --> Sha[sha-* immutable tag]
```

To pull the latest main image:

```bash
docker pull ghcr.io/codeshayk/abacus-run:latest
docker run --rm -p 8080:8080 ghcr.io/codeshayk/abacus-run:latest
```

The package visibility and first-publish behavior are governed by GitHub repository/package settings. A repository administrator may need to make the package public or grant access to consumers after the first publish.

## Testing

The solution includes several test layers:

| Suite | Purpose |
| --- | --- |
| Unit tests | Contracts, policies, runner behavior, middleware, executors, stores, audit recorder, and control logic |
| Integration tests | Real ASP.NET Core host, HTTP routes, event streams, approvals, instance controls, the instance state route, and the example workflow end to end |
| Chaos tests | Failure and lifecycle scenarios |
| Broker tests | The distributed brokers against real Redis and RabbitMQ, via Testcontainers |
| Load tests | Throughput-oriented test project |

Every suite except the broker tests runs with no external dependency, which is what keeps a clone
testable on a fresh machine. `Abacus.Run.BrokerTests` is the deliberate exception: a transport claim
that has never touched the wire is not a verified claim, and no in-memory double can tell you whether
a Redis consumer group or an AMQP topic exchange behaves the way the abstraction says it does.

Testcontainers starts and disposes the containers itself, so there is nothing to run beforehand. When
no container runtime is present the tests report as **skipped** rather than failed — a machine or CI
leg without Docker still gets a green suite instead of a red one it cannot fix. Watch for that in the
output: a run reporting skips has verified nothing about the transports.

Common commands:

```bash
# All tests
dotnet test Abacus.Run.slnx

# Release test run with coverage
dotnet test --configuration Release --no-build --collect:"XPlat Code Coverage"

# One project
dotnet test tests/Abacus.Run.UnitTests/Abacus.Run.UnitTests.csproj

# The distributed brokers against real Redis and RabbitMQ (needs Docker)
dotnet test tests/Abacus.Run.BrokerTests/Abacus.Run.BrokerTests.csproj

# One test by name
dotnet test tests/Abacus.Run.UnitTests/Abacus.Run.UnitTests.csproj \
  --filter "FullyQualifiedName~WorkflowRunnerTests"
```

Build before using `--no-build`:

```bash
dotnet build --configuration Release
dotnet test --configuration Release --no-build
```

## Extension points

### Custom persistence

Implement the store interfaces used by `AddWorkflowHost`, including instance, event, log, approval, gate policy, audit, audit record, blob, and checkpoint contracts. Preserve these invariants:

- Instance updates use optimistic concurrency.
- Terminal state is not overwritten by a losing writer.
- Lease ownership is exclusive and renewable.
- Event sequence values are unique and ordered per instance.
- Checkpoint indexes are returned in the order expected by `CheckpointManager`.
- Approval decisions are single-winner and quorum-aware.
- Audit record entries are unique per `(instance, section kind, key)`, so a re-record replaces.

### Custom audit record storage

Implement `IAuditRecordStore` when records must outlive the process or be queried outside the host.
Keep the payload opaque — the value of the contract is that a new workflow with a different record
shape needs no schema change. `Abacus.Run.Service/Infrastructure/Auditing` is a worked example: an EF
Core SQLite store with a unique index on `(InstanceId, SectionKind, Key)`, registered through
`AddSqliteAuditRecords()`, which removes the framework's in-memory registration rather than racing it.

### Custom executors

Derive from `HostExecutor<TIn, TOut>` and implement `ExecuteCoreAsync`. Keep side effects idempotent and expose useful metadata through the executor descriptor. Do not bypass `HandleAsync`.

### Custom middleware

Implement `IWorkflowMiddleware` for run-wide behavior or `IExecutorMiddleware` for node-level behavior. Use `AppliesTo` to limit scope and avoid putting transport-specific behavior into workflow definitions.

### Custom approval policies

Provide an `IGatePolicyStore` implementation when approval requirements depend on tenant, workflow, executor, amount, role, or environment. Keep policy evaluation deterministic and observable.

`FindAsync` resolves one executor's gate for a tenant and must itself apply the scope precedence — per-instance override, then the tenant's policy, then the host-wide policy stored under a null tenant — returning `null` when no policy exists so the definition's gate stands. `ListAsync` returns exactly one scope's entries without merging, which is what lets the API show a tenant's own overrides separately from the host default. The locked-gate floor is enforced by the runtime on top of whatever the store returns, so a custom implementation cannot accidentally weaken a protected executor.

### Custom event sinks and buses

The runner publishes through `INotificationSink`. A sink may persist the event, relay it to an event bus, or do both. Preserve the per-instance sequence when forwarding to SSE or external consumers.

An envelope marked `Transient` must be relayed but **not** persisted, and carries no sequence number. A sink that stores it anyway reintroduces the write amplification transience exists to avoid; one that assigns it a sequence puts a hole in the durable sequence and breaks `Last-Event-ID` catch-up.

### Custom event brokers

Implement `IDomainEventBroker` to carry domain messages over a transport of your choosing — Azure Service Bus, Kafka, NATS. Register it in place of the default `InProcessDomainEventBroker`. `RedisEventBroker` and `RabbitMqDomainEventBroker` are the two worked examples, and they differ enough to be worth reading as a pair: one filters client-side and can replay, the other filters at the exchange and cannot.

Three obligations:

- **Honour `DeliveryScope`.** A `Local` message must never leave the process. A broker that widens local traffic onto the wire leaks what a service declared private.
- **Report `DomainEventBrokerCapabilities` truthfully.** It is what lets composition reject an impossible subscription at startup instead of delivering locally and looking like it worked. Overstating a capability turns a startup error into a silent production gap.
- **Distinguish consumer groups.** A named `ConsumerGroup` means exactly one member of the fleet handles each message; an unnamed one means every subscriber gets its own copy. Collapsing the two turns work routing into duplicated work, or an observer into a thief.

Delivery may be at-least-once. Exactly-once resumption is the subscription store's job, not the transport's: `IDomainEventSubscriptionStore.TryDeliverAsync` is a compare-and-set, so a redelivered message costs a lookup rather than resuming an instance twice.

### Custom subscription storage

Implement `IDomainEventSubscriptionStore` so triggers and waits outlive the process. `TryDeliverAsync` must be a conditional update — a relational implementation writes `UPDATE ... WHERE DeliveredMessageId IS NULL` and reports whether it won. `ClaimExpiredAsync` must mark what it returns under the same lock or transaction that selected it, or two sweepers will expire the same instance twice. Reuse `DomainSubscriptionMatch.Matches` as the final predicate after any database-side pre-filtering, so a custom store cannot disagree with the in-memory one about what a subscription means.

## Design constraints

### At-least-once execution

A checkpoint cannot atomically cover every external side effect. An executor may run again after a crash. Use idempotency keys, transactionally recorded effects, or an application-level deduplication table for side-effecting work.

### Host executor middleware boundary

The Agent Framework does not expose a generic interception seam for arbitrary executors. Abacus Run therefore owns the middleware seam in `HostExecutor<TIn, TOut>`. Raw framework executors can be used, but they do not receive host executor middleware or approval gates.

### Pinned workflow versions

Version selection occurs at start. Resumption uses the exact recorded version so a deployed definition cannot silently change the meaning of an in-flight instance.

### Event sequencing

Approval events and progress events share the instance event sequence. This is necessary for one coherent SSE replay cursor and avoids a client observing a progress event without the approval event that caused the instance to park.

### In-memory defaults

The in-memory stores make local development and deterministic tests straightforward. They intentionally do not claim the durability, failover, or cross-replica guarantees of the target production architecture.

## Troubleshooting

### Docker publish fails with a C# preview feature error

Use the repository's .NET 9 SDK image and build from the root. The API project and its project references must be copied into the Docker build context. Preview-only syntax should not be introduced into code compiled by the .NET 9 image.

### `dotnet test --no-build` cannot find a DLL

Build the requested configuration first. Debug binaries do not satisfy a Release `--no-build` run:

```bash
dotnet build --configuration Release
dotnet test --configuration Release --no-build
```

### A workflow is not listed

Confirm the definition is registered with `.AddWorkflow<T>()` or `.AddWorkflow(definition)`, that the host calls `AddWorkflowHost(...)`, and that the application is running the expected build.

### A start request returns validation errors

The context JSON must be present, non-null, and deserializable to the workflow's declared context type. Check property names, required members, and the selected workflow version.

### An instance remains pending

Check that `AddBackgroundServices()` is registered and that the dispatcher is running. In a multi-replica deployment, inspect lease ownership and the shared instance store. With in-memory stores, a second process cannot see the first process's instances.

### An approval does not resume the instance

Inspect the approval state, decision authorization, quorum, expiry, and instance status. A successful decision wakes the instance by moving it to a claimable state; the dispatcher must be running to execute the resumed run.

### A published event starts or resumes nothing

Check `GET /subscriptions` first — an empty result means nothing was ever listening, which is a different problem from a delivery failure.

The usual cause is that the filter and the topic do not match: matching is ordinal and case-sensitive, `*` covers exactly one segment, and `#` only matches as the final segment. `orders.*` does not match `orders.eu.placed`. A wildcard glued to literal text (`order*`) is rejected at registration rather than treated as a prefix, so check startup logs for a rejected trigger.

Tenant and correlation narrow further: a subscription that names a tenant sees only that tenant, and one that names a correlation key sees only messages carrying the identical value. A message published with no tenant does not match a subscription scoped to one.

`DomainEventDispatcher` counts messages that matched nothing and logs them at debug. If the count is rising, the message is arriving and the filters are wrong; if it is not, the message is not arriving.

Finally, a `Distributed` message needs a distributed broker. Against the in-process default the publish is refused outright, so check for a `NotSupportedException` at the publisher or a `400` from `POST /events` rather than looking for a lost message.

### An instance is stuck in `AwaitingInput`

If the workflow uses `WaitForDomainEventExecutor`, this is the normal parked state, not a fault. The instance detail page shows a **Waiting on** panel naming the topic, the blocked node and the correlation key; `GET /subscriptions?instanceId={id}&pendingOnly=true` is the same information over the API.

A wait with no `timeout` waits forever by design. Set one, with `WaitExpiryAction.DeadStop` to fail the instance or `Resume` to let the workflow take its own timeout branch, and make sure `DomainEventWaitSweeper` is running — it comes with `AddBackgroundServices()`.

### `llm.delta` events are missing from event history

They are not stored, by design. Streamed tokens are stream-only: live fan-out, no sequence number, no durable row. Subscribe to `GET /instances/{id}/events` to see them; `GET /instances/{id}/events/history` will never return them.

If none arrive on the live stream either, check whether the workflow sets `StreamEvents = false`. Deltas are the one kind of event with no durable record by design, so with streaming off they have nowhere left to go — intended, not a bug, but it does make `StreamDeltas = true` alongside `StreamEvents = false` a combination that produces no tokens anywhere.

### The SSE endpoint returns 409

The workflow sets `StreamEvents = false`, so it never streams. Its events are all still recorded — read them at `GET /v2/workflows/{name}/instances/{id}/events`, the URL the problem detail carries. This is deliberate: an empty stream held open is indistinguishable from a stalled run, so the endpoint refuses rather than misleading a client into waiting.

### Can a workflow turn off event logging?

No, and there is no setting that does it. `StreamEvents` switches the live stream only; every event a workflow emits is written to the durable log regardless. `NotificationLevel` can reduce *which* events are emitted at all — a `Minimal` workflow emits fewer — but whatever is emitted is always recorded.

Also confirm `LlmOptions.StreamDeltas` is set on that node — it defaults to `false`, so no deltas are produced at all unless the definition asked for them.

### `llm.completed` reports `costUsd: null`

The model has no entry under `Abacus:Llm:Pricing`. Cost is reported as absent rather than zero on purpose, and unpriced samples are excluded from the cost drift baseline, so an unpriced period cannot make a later rise look smaller than it is. Add the model's per-million rates to start pricing it.

### Expected events are missing from the stream

Check whether the definition implements `INotifyingWorkflow`. A `Minimal` or `Lifecycle` level suppresses `executor.*` and `custom.*`, and `ByNode` can quiet one node while the rest of the workflow stays loud. Suppressed events consume no sequence number, so a gap in the numbering is not the symptom — the events are simply absent.

### An executor pauses for approval although the definition left it autonomous

A tenant or host-wide policy is gating it. Call `GET /workflows/{name}/versions/{version}/nodes` as that tenant and read `effectiveSource`: `tenant` means the tenant configured it, `host` means a host-wide policy applies. `DELETE` the node's override to restore the declared gate. Remember the policy is version-scoped — check the version the instance actually pinned, not the latest.

### A tenant's configuration appears to be ignored

Confirm the tenant used to configure the workflow is the tenant the instance runs under; the runner resolves gates for `instance.TenantId`, not for the caller who last edited the policy. Also check the instance's workflow version against the version the policy was written for, and whether a per-instance override outranks it.
### `audit` is null on the instance state route

The workflow does not implement `IAuditedWorkflowDefinition`, so it declares no record. If it does
implement it and the record is still empty, check that an `IAuditRecordStore` is registered — the
runner logs a warning and returns no recorder when a definition declares a record with no store
behind it — and remember that recorder failures are swallowed by design, so the host log is where a
storage problem surfaces, not the response.

### An outbound call is blocked

Check the URL scheme, whether the target resolves to an internal address, and whether the hostname matches `WorkflowHost:Egress:AllowedHosts`. Keep `Egress:Enforce=true` unless this is a controlled local test.

## Appendix: authoring variations

Each recipe is a complete `BuildAsync` (or the declaration that matters), showing one shape in
isolation. They compose — the [worked definition](#a-definition-using-all-of-it) above combines
several.

### A.1 Linear

The default shape. One node after another, output from the last.

```csharp
public ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken ct)
{
    ExecutorBinding validate = context.Node(new Validate("validate"));
    ExecutorBinding enrich   = context.Node(new Enrich("enrich"));
    ExecutorBinding submit   = context.Node(new Submit("submit"));

    return new ValueTask<Workflow>(new WorkflowBuilder(validate)
        .AddEdge(validate, enrich)
        .AddEdge(enrich, submit)
        .WithOutputFrom(submit)
        .WithName(Name)
        .Build());
}
```

### A.2 Branch

Two conditional edges out of one node. There is no switch construct; this is the branch.

```csharp
ExecutorBinding triage = context.Node(new Triage("triage"));
ExecutorBinding fast   = context.Node(new FastPath("fast-path"));
ExecutorBinding manual = context.Node(new ManualPath("manual-path"));

return new ValueTask<Workflow>(new WorkflowBuilder(triage)
    .AddEdge<Order>(triage, fast,   condition: o => o is { Amount: <= 10_000m })
    .AddEdge<Order>(triage, manual, condition: o => o is { Amount: >  10_000m })
    .WithOutputFrom(fast, manual)          // whichever branch ran supplies the result
    .WithName(Name)
    .Build());
```

The condition's parameter is `T?`, so a pattern (`o is { … }`) reads better than a null-forgiving
dereference and handles the null case explicitly.

Make the predicates exhaustive. A message matching neither edge stops there, and the run completes
with no output rather than failing — which looks like success and is the hardest branch bug to spot.

### A.3 Fan-out and fan-in

```csharp
ExecutorBinding split   = context.Node(new Split("split"));
ExecutorBinding credit  = context.Node(new CheckCredit("check-credit"));
ExecutorBinding stock   = context.Node(new CheckStock("check-stock"));
ExecutorBinding fraud   = context.Node(new CheckFraud("check-fraud"));
ExecutorBinding decide  = context.Node(new FanInExecutor<CheckResult, Decision>(
    "decide", checks => new Decision(checks.All(c => c.Passed))));

return new ValueTask<Workflow>(new WorkflowBuilder(split)
    .AddFanOutEdge(split, [credit, stock, fraud])
    .AddFanInBarrierEdge([credit, stock, fraud], decide)   // waits for all three
    .WithOutputFrom(decide)
    .WithName(Name)
    .Build());
```

Selective fan-out picks targets by index instead of sending to all:

```csharp
.AddFanOutEdge<Order>(split, [credit, stock, fraud],
    targetSelector: (order, count) => order!.SkipFraudCheck ? [0, 1] : [0, 1, 2])
```

A wide fan-out is the usual reason to set `NotificationLevel.Lifecycle` — see [A.10](#a10-quiet-a-chatty-workflow).

### A.4 Approval gates

Three ways to gate, from blunt to conditional:

```csharp
// Always requires a decision.
context.Node(new Publish("publish"), gate => gate
    .Mode(ExecutionMode.RequireApproval)
    .AssignTo("group:ops")
    .ExpiresAfter(TimeSpan.FromHours(4)));

// Only above a threshold. `When` implies Conditional mode.
context.Node(new Settle("settle"), gate => gate
    .When<Order>(order => order.Amount > 25_000m)
    .Reason("AmountAboveThreshold")
    .RequireApprovers(2)
    .AllowModification());

// A floor a tenant may tighten but never weaken.
context.Node(new Payout("payout"), gate => gate
    .Mode(ExecutionMode.RequireApproval)
    .AssignTo("group:finance")
    .RequireSegregationOfDuties()
    .OnExpiry(ExpiryAction.DeadStop)
    .Locked());
```

`HumanApprovalExecutor<T>` does the same job as a node rather than as configuration, when the
approval is part of the workflow's own logic and should be visible in the graph:

```csharp
ExecutorBinding signOff = context.Node(new HumanApprovalExecutor<Order>("sign-off"));
```

### A.5 Durable delay

`DelayExecutor` checkpoints and halts rather than blocking a thread or holding a lease, so a long
delay costs no execution capacity.

```csharp
var timers = context.Services!.GetRequiredService<ITimerService>();

ExecutorBinding cooloff = context.Node(
    new DelayExecutor("cool-off", TimeSpan.FromHours(24), timers));

return new ValueTask<Workflow>(new WorkflowBuilder(submit)
    .AddEdge(submit, cooloff)
    .AddEdge(cooloff, settle)
    .WithOutputFrom(settle)
    .Build());
```

### A.6 HTTP call

```csharp
ExecutorBinding fetch = context.Node(new ApiCallExecutor("fetch-invoice",
    new ApiCallOptions
    {
        Method = HttpMethod.Get,
        UrlTemplate = "https://erp.internal/invoices/{{ context.InvoiceId }}",
        Headers = { ["Accept"] = "application/json" },
        TimeoutSeconds = 15,
        SuccessCodes = [200, 204],
        ResponseAs = typeof(InvoiceDto),
        AllowedHosts = ["erp.internal"],
        EnforceEgress = true,        // refuse anything not on the allow-list
        SendIdempotencyKey = true    // safe to retry
    },
    () => context.Services!.GetRequiredService<IHttpClientFactory>()
        .CreateClient(ApiCallOptions.HttpClientName)));
```

A non-success status arrives as a typed `ApiCallFailure` carrying status, body excerpt and
`Retry-After`, so [`Classify`](#a12-custom-failure-classification) can act on it rather than parsing
a message.

### A.7 LLM node

```csharp
ExecutorBinding classify = context.Node(new LlmExecutor("classify",
    new LlmOptions
    {
        Model = "claude-sonnet-5",
        SystemPrompt = "Classify the invoice.",
        PromptVersion = "v3",                 // tags the drift baseline
        UserTemplate = "{{ context.DocumentText }}",
        StructuredOutput = typeof(Classification),
        Temperature = 0.0f,
        MaxTokens = 2048,
        StreamDeltas = true,                  // llm.delta frames, live only
        EmitCompletion = true                 // one llm.completed per call (default)
    },
    model => context.Services!.GetRequiredService<IChatClient>(),
    context.Services!.GetService<IModelPricing>()));   // enables costUsd and cost drift
```

Pass the pricing service or `costUsd` is `null` — absent, not zero. See
[LLM telemetry](#llm-telemetry).

### A.8 Started by an event

```csharp
public sealed class ShipOrderWorkflow
    : IWorkflowDefinition<OrderPlaced, ShipmentResult>, IDomainEventTriggeredWorkflow
{
    public string Name => "ship-order";
    public string Version => "1.0.0";

    public IReadOnlyList<DomainEventTrigger> Triggers =>
    [
        new DomainEventTrigger { TopicFilter = "orders.placed" },
        new DomainEventTrigger
        {
            TopicFilter = "orders.*.expedited",
            ContextSelector = m => m.PayloadJson      // remap if the payload is not the context
        }
    ];

    // BuildAsync as usual; the message payload arrives as the context.
}
```

The message payload becomes the instance context, and its correlation key becomes the instance's
correlation id. Redelivery is absorbed by the launcher's idempotency key, so a message cannot start
the same workflow twice.

### A.9 Publish and wait

A two-workflow pipeline. The first publishes; the second parks until the reply arrives.

```csharp
// Producer — publishing is a side effect on the way past, so the node drops into an existing edge.
var broker = context.Services!.GetRequiredService<IDomainEventBroker>();

ExecutorBinding publish = context.Node(new PublishDomainEventExecutor<OrderPlaced>(
    "publish-order-placed", broker,
    topic: "orders.placed",
    correlationKey: o => o.OrderId));

// Consumer — parks, releases its lease, and resumes with the payload.
var subscriptions = context.Services!.GetRequiredService<IDomainEventSubscriptionStore>();

ExecutorBinding awaitPayment = context.Node(
    new WaitForDomainEventExecutor<OrderContext, PaymentSettled>(
        "await-settlement", subscriptions,
        topicFilter: "payment.settled",
        correlationKey: o => o.OrderId,
        timeout: TimeSpan.FromDays(3),
        onExpiry: WaitExpiryAction.DeadStop));   // or Resume, to take a timeout branch
```

Publishing across a service boundary is a scope on the message, not a different call — see
[Local by default, global by declaration](#local-by-default-global-by-declaration).

### A.10 Quiet a chatty workflow

```csharp
public NotificationPolicy Notifications { get; } = new()
{
    Level = NotificationLevel.Lifecycle,          // supersteps, no per-node chatter
    ByNode = new Dictionary<string, NotificationLevel>(StringComparer.Ordinal)
    {
        ["reconcile"] = NotificationLevel.Standard  // except this one
    }
};
```

### A.11 Log without streaming

```csharp
public NotificationPolicy Notifications { get; } = new()
{
    StreamEvents = false        // full event log; no SSE
};
```

The log is unconditional either way. `GET /instances/{id}/events` then returns `409` naming
`GET /v2/workflows/{name}/instances/{id}/events`. See
[Turning SSE off for a workflow](#turning-sse-off-for-a-workflow).

### A.12 Custom notifications from a node

```csharp
public NotificationPolicy Notifications { get; } = new()
{
    Emits = ["documents.scanned"]     // advertised on GET /workflows/{name}
};
```

```csharp
protected override async ValueTask<ScanResult> ExecuteCoreAsync(
    ScanContext input, IWorkflowContext context, CancellationToken ct)
{
    if (Runtime.Notify is { } notify)
    {
        await notify.NotifyAsync("documents.scanned", new { count = input.Documents.Count }, ct);
    }
    // → event: custom.documents.scanned
}
```

### A.13 Audit record

```csharp
public AuditRecordDefinition AuditRecord { get; } = new(
    "order", "One order, as processed.",
    [
        new AuditSectionDefinition("submission", "What was submitted.", Multiple: false),
        new AuditSectionDefinition("step", "One processing step."),
        new AuditSectionDefinition("outcome", "How the run settled.", Multiple: false)
    ]);
```

```csharp
if (Runtime.Audit is { } audit)
{
    await audit.OpenAsync(input.OrderId, attributes: null, ct);
    await audit.RecordAsync("step", key: input.LineId, new { accepted = true }, ct);
    await audit.CloseAsync(AuditRecordStatus.Completed, ct);
}
```

Keying an entry means a retried executor corrects its record rather than doubling it. See
[Workflow audit records](#workflow-audit-records).

### A.14 Custom failure classification

```csharp
public FailureDisposition Classify(WorkflowFailure failure) => failure.Exception switch
{
    InsufficientFundsException  => FailureDisposition.DeadStop,   // retrying cannot help
    ThirdPartyThrottleException => FailureDisposition.Retry,
    ReconciliationBreakException => FailureDisposition.Escalate,  // terminal, flag for an operator
    _ => DefaultFailureClassifier.Instance.Classify(failure)
};
```

Classify per node when the same exception means different things in different places — the failure
carries `ExecutorId` and the executor's `Metadata`:

```csharp
public FailureDisposition Classify(WorkflowFailure failure)
    => failure is { ExecutorId: "optional-enrichment", Exception: HttpRequestException }
        ? FailureDisposition.DeadStop        // this node is best-effort; do not burn attempts
        : DefaultFailureClassifier.Instance.Classify(failure);
```

### A.15 Raw nodes, agents and sub-workflows

Raw nodes join the graph but run outside the executor middleware pipeline and cannot be
approval-gated — passing a gate block throws.

```csharp
// An AIAgent as a node.
ExecutorBinding triage = context.RawNode(someAgent.BindAsExecutor("triage-agent"));

// Another workflow as a node.
Workflow enrichment = BuildEnrichmentGraph();
ExecutorBinding enrich = context.RawNode(enrichment.BindAsExecutor("enrich"));

// A bare handler, with no executor class at all.
Func<Order, IWorkflowContext, CancellationToken, ValueTask> logHandler =
    (order, _, _) => { Log(order); return ValueTask.CompletedTask; };
ExecutorBinding log = context.RawNode(logHandler.BindAsExecutor<Order>("log"));

ExecutorBinding record = context.Node(new Record("record"));   // gated, audited, with middleware

return new ValueTask<Workflow>(new WorkflowBuilder(triage)
    .AddEdge(triage, enrich)
    .AddEdge(enrich, log)
    .AddEdge(log, record)
    .WithOutputFrom(record)
    .Build());
```

A sub-workflow node runs the child graph inline. It is not a child *instance* — there is no separate
instance row, lease or event stream for it, and its nodes are not separately gateable or
configurable. Use `SubWorkflow` for composition of graph shape; use an event trigger
([A.8](#a8-started-by-an-event)) when you want a genuinely independent run.

### A.16 Registering what you built

```csharp
builder.Services
    .AddAbacus(builder.Configuration)          // or AddWorkflowHost + AddBuiltInMiddleware + AddBackgroundServices
    .AddWorkflow<OrderWorkflow>()              // resolved from DI
    .AddWorkflow(new ShipOrderWorkflow())      // or supplied directly
    .AddExecutorMiddleware<TimingMiddleware>();
```

Without `AddBackgroundServices()` instances are created and stay `Pending` — nothing executes them.
See [Registering workflows and middleware](#registering-workflows-and-middleware).

## Related documents

- [`README.md`](../README.md) - Short setup and API overview
- [`PRD-Abacus-Run.md`](PRD-Abacus-Run.md) - Product requirements and target architecture
- [`TDD-Abacus-Run.md`](TDD-Abacus-Run.md) - Technical design and framework grounding
- [`Dockerfile`](../Dockerfile) - Container build
- [`container.yml`](../.github/workflows/container.yml) - GHCR publishing workflow
- [`ci.yml`](../.github/workflows/ci.yml) - Build and test workflow
- [`release.yml`](../.github/workflows/release.yml) - Release tagging workflow

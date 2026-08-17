# Abacus Run

Abacus Run is a .NET workflow runtime and HTTP host for durable, observable workflow instances. It provides workflow version resolution, bounded concurrency, retries, checkpoints, approvals, event history, server-sent events, a topic event broker for event-driven pipelines, cancellation, reruns, and redacted audit/logging surfaces.

The runtime is built on Microsoft Agent Framework workflows. Stores are exposed through interfaces so the in-memory implementation can be replaced by durable persistence without changing workflow definitions.

The solution is split in two. `Abacus.Run` is the reusable, headless framework: runtime, dispatch, executors, middleware, in-memory store defaults, and the HTTP API. `Abacus.Run.Service` is the deployable host: the control-plane UI, the SQL Server and Redis implementations, and the startup wiring that selects them. Referencing the library alone gives a working API host with no UI and no infrastructure dependencies.

## Install

`Abacus.Run` — the framework library — is published to GitHub Packages:

```bash
dotnet add package Abacus.Run --version 1.0.0
```

Add the feed once, authenticating with a PAT that has `read:packages`:

```bash
dotnet nuget add source https://nuget.pkg.github.com/CodeShayk/index.json   --name github --username <you> --password <token> --store-password-in-clear-text
```

The package is the headless framework only; `Abacus.Run.Service` is the reference host and is not
published.

Two workflows publish it:

| Workflow | Trigger | Version |
| --- | --- | --- |
| `CI (Build, Test and Publish)` | push to `master` | `<gitversion>-ci.<run>` prerelease |
| `Package (Publish to GitHub Packages)` | `v*` tag or manual dispatch | stable, from the tag or project file |

Pull requests and feature branches build and test but never publish. CI builds carry a `-ci.<run>`
suffix so they sort below the stable release and a plain version number always means "tagged
release".

## Requirements

- .NET 9 SDK
- Access to the configured NuGet feeds in `nuget.config`

## Quick Start

Build and test the repository from its root:

```bash
dotnet restore Abacus.Run.slnx
dotnet build Abacus.Run.slnx
dotnet test Abacus.Run.slnx
```

Start the HTTP host:

```bash
dotnet run --project src/Abacus.Run.Service/Abacus.Run.Service.csproj
```

### Run the container

Build and run the API image locally:

```bash
docker build -t abacus-run .
docker run --rm -p 8080:8080 abacus-run
```

The `Container (Publish to GHCR)` workflow publishes `ghcr.io/codeshayk/abacus-run` on pushes to `master`, `v*` tags, and manual workflow dispatch. It uses the workflow's `GITHUB_TOKEN`; no additional registry secret is required. Pull the published image with:

```bash
docker pull ghcr.io/codeshayk/abacus-run:latest
```

The host exposes liveness and readiness probes:

```bash
curl http://localhost:5000/health/live
curl http://localhost:5000/health/ready
```

The API project is intentionally a host shell. Register one or more workflow definitions in the application's service configuration before starting instances:

```csharp
builder.Services
  .AddAbacus(builder.Configuration)
  .AddWorkflow<OrderWorkflow>();
```

`OrderWorkflow` must implement `IWorkflowDefinition` or `IWorkflowDefinition<TContext, TResult>`. Use `WorkflowBuildContext.Node(...)` to attach host executors and declare approval gates.

### Declaring executor gates

A node attached with no gate block runs autonomously. Pass a gate block to require a human decision, either always or under a predicate:

```csharp
public ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken cancellationToken)
{
    ExecutorBinding validate = context.Node(new Validate("validate"));           // autonomous

    ExecutorBinding notify = context.Node(new Notify("notify"), gate => gate
        .Mode(ExecutionMode.RequireApproval)
        .AssignTo("group:ops")
        .ExpiresAfter(TimeSpan.FromHours(4)));

    ExecutorBinding settle = context.Node(new Settle("settle"), gate => gate
        .When<OrderContext>(order => order.Amount > 25_000m)
        .Reason("RegulatedSettlement")
        .RequireApprovers(2)
        .Locked());                                                              // tenants may not weaken this

    // ...
}
```

Every gated node is configurable per tenant at run time unless the author calls `.Locked()`. A locked gate is a floor, not a freeze: a tenant may still tighten it. `RawNode(...)` bindings run outside the executor pipeline and cannot be gated at all.

## Hosting the framework in your own service

`Abacus.Run` is a library, not an application. It brings the runtime, dispatcher, executors, middleware, in-memory store defaults, and the whole HTTP API — but no UI, no database, and no `Program.cs`. `src/Abacus.Run.Service` is the reference host that wraps it, and is the pattern to copy.

### API only

Reference the package, register the framework, and map the API:

```csharp
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddWorkflowHost(builder.Configuration)   // runtime, stores, API services
    .AddBuiltInMiddleware()                   // OpenTelemetry, request logging, LLM drift
    .AddBackgroundServices()                  // dispatcher, expiry sweeper, retention, drain
    .AddWorkflow<OrderWorkflow>();

WebApplication app = builder.Build();
app.MapWorkflowApi();
app.Run();
```

`AddWorkflowHost` registers every store behind `TryAdd`, so anything you register first wins. `AddBackgroundServices` is what actually executes instances — without it, instances are created and stay `Pending`.

### Adding a control plane UI

A UI is a separate concern layered on top. `Abacus.Run.Service` keeps that separation strictly: the control plane is Razor Pages in the host project — a dashboard, a workflow catalog, instance detail with a live graph and event stream, and an approval queue — and it reaches the runtime **only through the public HTTP API**, never by injecting `IInstanceStore` or the runner. That is what keeps the UI honest: anything the UI can do, an API client can do too.

Give the host a composition root that turns the framework on and then substitutes environment-specific infrastructure:

```csharp
public static WorkflowHostBuilder AddAbacus(this IServiceCollection services, IConfiguration configuration)
{
    // 1. The framework, with its in-memory defaults.
    WorkflowHostBuilder host = services
        .AddWorkflowHost(configuration)
        .AddBuiltInMiddleware()
        .AddBackgroundServices();

    // 2. The UI, which belongs to the deployable rather than the framework.
    services.AddControlPlane();

    // 3. Concrete infrastructure, when configured, displacing the defaults.
    if (configuration["Abacus:SqlServer:ConnectionString"] is { Length: > 0 } sql)
    {
        services.AddSqlServerStores(sql, ensureDatabaseCreated: false);
    }

    if (configuration["Abacus:Redis:ConnectionString"] is { Length: > 0 } redis)
    {
        services.AddRedisNotificationBus(redis, maxStreamLength: 10_000);      // SSE fan-out across replicas
        services.AddRedisEventBroker(redis, maxStreamLength: 100_000);  // cross-service pub/sub
    }

    // Or RabbitMQ instead of the Redis broker — one or the other, not both.
    if (configuration["Abacus:RabbitMq:ConnectionString"] is { Length: > 0 } amqp)
    {
        services.AddRabbitMqDomainEventBroker(amqp);
    }

    return host;
}
```

Then map the API and the UI side by side:

```csharp
builder.Services.AddProblemDetails();
builder.Services.AddAbacus(builder.Configuration).AddWorkflow<OrderWorkflow>();

WebApplication app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseStaticFiles();
app.UseRouting();

app.MapWorkflowApi();          // from the library
app.MapControlPlane("/control");  // your Razor Pages
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" }));

app.Run();
```

`AddControlPlane` registers the Razor Pages plus a typed `WorkflowApiClient` whose base address defaults to the origin of the request currently being served, so the UI keeps working behind a container port mapping or TLS termination instead of being pinned to one developer machine. Override it with `Abacus:ControlPlane:ApiBaseUrl` when the API is hosted separately from the UI.

### Where the line falls

| Concern | Lives in |
| --- | --- |
| Runtime, dispatch, executors, middleware, HTTP API, in-memory defaults, in-process event broker | `Abacus.Run` |
| Redis Streams bus, Redis broker, control channel | `Abacus.Adapters.Cache.Redis` |
| RabbitMQ topic-exchange broker | `Abacus.Adapters.Messaging.RabbitMQ` |
| Razor Pages, SQL Server stores, startup wiring that selects the above | your service (`Abacus.Run.Service`) |

The library carries no Razor, MVC, Entity Framework, or Redis dependency, and an architecture test in the integration suite fails the build if one drifts back in. Splitting a UI host out later is therefore a matter of moving Razor and infrastructure projects, not of untangling the runtime.

With neither connection string set, the whole thing runs on in-memory stores — which is what keeps local development and the integration tests dependency-free, and why that configuration is not suitable for multiple replicas or process-loss recovery. See [Configuration](#configuration).

Note that the reference control plane does not yet expose the per-tenant node configuration described below; that surface is API-only today.

## API Surface

### Workflow catalog

- `GET /workflows`
- `GET /workflows/{name}`
- `POST /workflows/{name}/instances`

The start endpoint accepts an optional `version` query parameter and supports `Idempotency-Key` and `Prefer: wait=<seconds>` headers.

### Per-tenant executor configuration

- `GET /workflows/{name}/versions/{version}/nodes`
- `PUT /workflows/{name}/versions/{version}/nodes`
- `PUT /workflows/{name}/versions/{version}/nodes/{executorId}`
- `DELETE /workflows/{name}/versions/{version}/nodes/{executorId}`

Tenants configure whether each executor of a workflow runs autonomously or waits for approval, without a redeploy. The tenant comes from `X-Tenant-Id` or the `tenant_id` claim; `version` accepts a concrete version or `latest`.

`GET` lists the executor nodes the definition declares, each with its declared gate, the calling tenant's override, and the gate that will actually run:

```json
{
  "workflowName": "order", "workflowVersion": "1.0.0", "tenantId": "acme",
  "nodes": [{
    "executorId": "settle", "executorType": "Settle",
    "inputType": "OrderContext", "outputType": "OrderResult",
    "configurable": true, "locked": false,
    "declared":       { "mode": "autonomous",     "requiredApprovers": 1, "...": "..." },
    "tenantOverride": { "mode": "requireApproval", "assignees": ["group:finance"], "...": "..." },
    "effective":      { "mode": "requireApproval", "assignees": ["group:finance"], "...": "..." },
    "effectiveSource": "tenant"
  }]
}
```

`PUT` writes the calling tenant's policy for one node, or for several at once via `{ "nodes": { "<executorId>": { ... } } }`. Only `mode` is required; every other field inherits the declared gate, so the body below keeps the author's assignees and expiry:

```bash
curl -X PUT http://localhost:5000/workflows/order/versions/1.0.0/nodes/settle \
  -H 'X-Tenant-Id: acme' -H 'Content-Type: application/json' \
  -d '{"mode":"requireApproval","requiredApprovers":2}'
```

| Field | Notes |
| --- | --- |
| `mode` | `autonomous` or `requireApproval`. `conditional` is code-only — its predicate cannot be expressed in JSON |
| `reason` | Shown on the resulting approval request |
| `assignees`, `escalationAssignees` | Principal or group identifiers |
| `requiredApprovers` | Quorum; at least 1 |
| `expirySeconds` | Approval window; greater than zero |
| `onExpiry` | `deadStop`, `reject`, `autoApprove`, or `escalate` |
| `allowModification`, `requireSegregationOfDuties` | Booleans |

`DELETE` drops the override and restores the declared gate. A bulk `PUT` is all-or-nothing: if any node is unknown, not configurable, or refused, nothing is persisted.

At run time the runner resolves gates for the instance's own tenant, in this order: per-instance override, tenant policy, host-wide policy (a policy written with no tenant), then the definition, then autonomous. Responses report which one applied as `effectiveSource`.

A gate the author declared with `.Locked()` is a floor. Configuration may tighten it; an attempt to weaken it — downgrading `mode`, lowering `requiredApprovers`, enabling `allowModification`, disabling segregation of duties, or setting `onExpiry` to `autoApprove` — is refused with `409` and persists nothing. The runtime re-applies the floor on read as well, so a policy that reached the store by another route still cannot un-gate a locked executor.

Policies are keyed by workflow **version**, since executor ids and gates change between versions. A tenant's configuration therefore does not carry forward to a new version, and `POST /instances/{id}/rerun` in restart mode creates the new instance at the *current* version — so a restart after a version bump runs under that version's configuration, or under declared defaults if the tenant has none.

### Instance operations

- `GET /instances/{id}`
- `GET /instances`
- `GET /instances/{id}/graph`
- `GET /instances/{id}/logs`
- `GET /instances/{id}/checkpoints`
- `GET /instances/{id}/events/history`
- `GET /instances/{id}/events`
- `GET /v2/workflows/{name}/instances/{id}/events`
- `POST /instances/{id}/cancel`
- `POST /instances/{id}/retry`
- `POST /instances/{id}/rerun`
- `POST /instances/{id}/suspend`
- `POST /instances/{id}/resume`

`GET /instances/{id}/events` is an SSE stream. Instance queries support status, workflow, correlation ID, limit, and offset filters.

### Instance state and audit records

- `GET /workflows/{name}/instances/{id}/state`

Returns the instance's lifecycle status alongside the audit record its own workflow declared. The
route is scoped by workflow name because the response shape comes from that workflow's declaration; a
mismatched name returns `404`. `?section=plan,output` narrows the response to named sections. See
[Audit records](#audit-records).

### Approvals

- `GET /approvals`
- `GET /approvals/{approvalId}`
- `GET /instances/{id}/approvals`
- `POST /approvals/{approvalId}/decision`

### Domain events

- `POST /events`
- `GET /subscriptions`

Publish a message to a topic, and list what is listening or waiting. See [Events](#events).

## Events

Two kinds of events share the word and almost nothing else.

A **notification** describes what a run is doing — keyed by instance, ordered by a gapless sequence,
delivered to whoever is watching. It never affects execution; lose one and a dashboard is briefly out
of date.

A **domain event** describes what happened in the business — keyed by topic, routed to whoever
declared interest, and it *causes* execution; lose one and work that should have happened never does.

That asymmetry is why they are built differently: notifications are best-effort fan-out over a
durable log, while broker delivery is a durable state transition. A domain event may cause a
notification; a notification may never cause work.

### Notifications from a node

`Runtime.Notify` puts a workflow-defined event on the instance's stream, and is nullable in the same
way `Runtime.Audit` is:

```csharp
if (Runtime.Notify is { } notify)
{
    await notify.NotifyAsync("documents.scanned", new { count = 3 }, cancellationToken);
}
// → event: custom.documents.scanned
```

The `custom.` prefix is applied by the runtime and cannot be opted out of, so a workflow can never
shadow a framework event, and a consumer can filter the whole class on the prefix.

A definition controls what its runs emit by implementing `INotifyingWorkflow` — `Minimal`,
`Lifecycle` or `Standard`, overridable per node in both directions, plus the custom names it declares
for the catalog API. Terminal events are never suppressible, and filtering happens before a sequence
number is taken, so the gapless sequence that `Last-Event-ID` catch-up depends on stays intact.

**The event log is not optional.** Every event a workflow emits is written to the durable log, and no
setting turns that off. The one delivery choice a workflow has is whether those same events are
*also* streamed live to SSE subscribers:

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

Observability is not reduced by turning it off, only its timeliness — events are still sequenced,
redacted, and carry the workflow name, and are read in full at
`GET /v2/workflows/{name}/instances/{id}/events`. The SSE endpoint then returns `409` pointing at
that route rather than holding open a stream that will never produce anything, because an empty
stream is indistinguishable from a stalled run.

An `LlmExecutor` emits one `llm.completed` per call carrying model, prompt version, tokens, cost,
latency and finish reason. Streamed tokens (`llm.delta`, opt-in per node via `StreamDeltas`) are
**transient**: fanned out live, never stored, and written without an SSE `id:`, so a reconnecting
client never waits for a chunk that no longer exists.

### Event-driven workflows

A workflow publishes to a topic, and another workflow either starts because of it or wakes up
because of it:

```csharp
// Publish, as a side effect on the way past
context.Node(new PublishDomainEventExecutor<OrderPlaced>(
    "publish", broker, topic: "orders.placed", correlationKey: o => o.OrderId));

// Start on a message
public IReadOnlyList<DomainEventTrigger> Triggers => [new DomainEventTrigger { TopicFilter = "orders.placed" }];

// Or park mid-run until one arrives
context.Node(new WaitForDomainEventExecutor<PaymentContext, PaymentSettled>(
    "await-settlement", subscriptions, "payment.settled", correlationKey: c => c.OrderId));
```

Delivery is a durable state transition, not a message: a trigger creates an instance row, a wait
writes the payload to a subscription row and marks the instance dispatchable. Nothing waits in
memory, so a pipeline survives a restart. A parked instance holds no execution slot and can wait for
days.

Topic filters use `*` for one segment and `#` for the remainder. Scope travels on the message —
`Local` by default, so the same publishing code is correct in one service and in a fleet.
`InProcessDomainEventBroker` is registered by default; `AddRedisEventBroker` or `AddRabbitMqDomainEventBroker`
replaces it for cross-service pub/sub, and an impossible combination is rejected at composition time
rather than failing silently in production.

| Transport | Reach | Competing consumers | Replay | Dead letter |
| --- | --- | --- | --- | --- |
| `InProcessDomainEventBroker` *(default)* | This service | Yes | No | No |
| `RedisEventBroker` | Every service | Yes | Yes | Yes |
| `RabbitMqDomainEventBroker` | Every service | Yes | No | Yes |

Redis filters client-side and can replay from a stream. RabbitMQ filters server-side at a topic
exchange, so a subscriber is never woken for a message it would discard, and reports
`SupportsReplay: false` rather than quietly behaving as `Now` — a queue holds what arrives after it
is bound. Both are verified against real servers in `tests/Abacus.Run.BrokerTests`.

Full walkthrough: [Events, history, and SSE](docs/wiki.md#events-history-and-sse) and
[Event broker](docs/wiki.md#event-broker-and-event-driven-workflows).

## Audit records

Events record what the runtime did. An audit record answers the separate question of why a run's
result is defensible — the plan a node formed, the input it worked from, the output it produced. That
is workflow-specific, so the framework supplies the hook and the storage, never the schema.

A definition opts in by implementing `IAuditedWorkflowDefinition` and declaring a root kind plus the
sections that may hang off it:

```csharp
public sealed class OrderWorkflow
    : IWorkflowDefinition<OrderContext, OrderResult>, IAuditedWorkflowDefinition
{
    public AuditRecordDefinition AuditRecord { get; } = new(
        "order",
        "One order, as processed.",
        [
            new AuditSectionDefinition("submission", "What was submitted.", Multiple: false),
            new AuditSectionDefinition("step", "One processing step."),
            new AuditSectionDefinition("outcome", "How the run settled.", Multiple: false)
        ]);
}
```

The runtime then hands every node a recorder bound to that declaration. Executors reach it through
`Runtime.Audit`; a definition wiring its own nodes reads `WorkflowBuildContext.Audit`. Both are
nullable, so a workflow that declares no record costs nothing:

```csharp
if (Runtime.Audit is { } audit)
{
    await audit.OpenAsync(input.OrderId, attributes: null, cancellationToken);
    await audit.RecordAsync("step", Id, new { accepted = true }, cancellationToken);
    await audit.CloseAsync(AuditRecordStatus.Completed, cancellationToken);
}
```

Storage stays workflow-agnostic: `IAuditRecordStore` keeps a root row plus entries whose section kind
is a string and whose payload is opaque JSON, so a new workflow needs no schema change. Recording is
best-effort by contract — a store failure is logged and swallowed, because failing work merely
because its explanation could not be filed trades a correct result for a missing one. Undeclared
section kinds are dropped, and re-recording the same `(section, key)` replaces the entry so a retried
executor corrects its record rather than contradicting it.

Read the record back through `GET /workflows/{name}/instances/{id}/state`. Every declared section
appears whether or not anything has been recorded into it, so a run in progress shows what is still
outstanding as readily as what is done.

The framework default is `InMemoryAuditRecordStore`. `Abacus.Run.Service` displaces it with an EF
Core SQLite store via `AddSqliteAuditRecords(configuration)`, configured under
`Abacus:AuditRecords:ConnectionString`.

A runnable example ships in the host at
[`src/Abacus.Run.Service/Workflows/ExampleOrder`](src/Abacus.Run.Service/Workflows/ExampleOrder) —
workflow `example-order`. It declares four sections, keys its per-line entries so a retry corrects
the record rather than doubling it, and records the failure before letting it propagate:

```bash
curl -X POST http://localhost:5000/workflows/example-order/instances \
  -H 'Content-Type: application/json' \
  -d '{"context":{"orderId":"ORD-1","lines":[{"sku":"SKU-A","quantity":2,"unitPrice":10.50}]}}'

curl http://localhost:5000/workflows/example-order/instances/{id}/state
```

Send `"failOnSku": "SKU-A"` in the context to see the failure path and the record it leaves behind.

Full walkthrough: [Workflow audit records](docs/wiki.md#workflow-audit-records).

## Configuration

Options are read from the `WorkflowHost` configuration section. For example:

```json
{
  "WorkflowHost": {
    "MaxConcurrentInstances": 100,
    "ClaimBatchSize": 10,
    "Retry": {
      "MaxAttempts": 5,
      "MaxLifetimeHours": 24
    },
    "Checkpoint": {
      "Cadence": "SuperStep"
    },
    "Sse": {
      "HeartbeatSeconds": 15
    },
    "Egress": {
      "Enforce": true,
      "AllowedHosts": ["api.example.com"]
    }
  }
}
```

The default host uses in-memory instance, event, log, approval, checkpoint, blob, audit, and audit-record stores. Treat this configuration as development-oriented until durable store implementations are supplied.

Set `Abacus:SqlServer:ConnectionString` to enable the EF Core SQL Server stores and `Abacus:Redis:ConnectionString` to enable Redis Streams, control messages, and the cross-service event broker. `AddAbacus` keeps the in-memory stores and the in-process broker when these settings are absent.

`Abacus:Llm:Pricing` turns token counts into cost on `llm.completed` and into a drift signal:

```json
{
  "Abacus": {
    "Llm": {
      "Pricing": {
        "claude-sonnet-5": { "InputPerMillion": 3.00, "OutputPerMillion": 15.00 }
      }
    }
  }
}
```

A model with no entry reports `null` rather than zero, and unpriced samples are excluded from the cost baseline — "we do not know" and "it was free" are different facts, and conflating them would mask a later cost rise.

`Abacus:AuditRecords:ConnectionString` points the SQLite audit-record store at its database file and
defaults to `Data Source=./data/abacus-audit.db`. The directory is created and the migrations applied
at startup.

## Project Layout

| Project | Responsibility |
| --- | --- |
| `src/Abacus.Run` | Headless framework: workflow runtime, dispatch, executors, middleware, in-memory store defaults, and HTTP API endpoints |
| `src/Abacus.Adapters.Cache.Redis` | Redis adapters: Streams event bus, workflow event broker, cross-replica control channel |
| `src/Abacus.Adapters.Messaging.RabbitMQ` | RabbitMQ adapter: topic-exchange workflow event broker |
| `src/Abacus.Run.Service` | Deployable host: control-plane UI, SQL Server stores, the SQLite audit-record store, startup wiring, and the example workflow |
| `tests/Abacus.Run.UnitTests` | Unit coverage for runtime behavior; references the library only |
| `tests/Abacus.Run.IntegrationTests` | HTTP, control-plane, and architecture-boundary coverage against the real host |
| `tests/Abacus.Run.ChaosTests` | Failure and lifecycle resilience coverage |
| `tests/Abacus.Run.BrokerTests` | The distributed brokers against real Redis and RabbitMQ, via Testcontainers |
| `tests/Abacus.Run.LoadTests` | Load-oriented test project |

Folders inside each project:

```
src/Abacus.Run/               src/Abacus.Run.Service/
  Abstractions/                 ControlPlane/      Razor Pages backing services
  Api/                          Infrastructure/    SQL Server stores
  Core/                           Auditing/        audit-record store and migrations
  Dispatch/                     Pages/             control-plane Razor Pages
  EventBus/                     Workflows/         workflow definitions hosted here
  Executors/                      ExampleOrder/    the audit-capable example workflow
  Middlewares/                  wwwroot/           control-plane CSS and JS
  Persistence/                  Program.cs
                                AbacusServiceCollectionExtensions.cs

src/Abacus.Adapters.Cache.Redis/     src/Abacus.Adapters.Messaging.RabbitMQ/
  RedisNotificationBus.cs               RabbitMqDomainEventBroker.cs
  RedisEventBroker.cs            RabbitMqServiceCollectionExtensions.cs
  RedisServiceCollectionExtensions.cs
```

Each adapter references `Abacus.Run` and its own client library — nothing else. It does not reference
the host, and the two adapters do not reference each other, so choosing one never drags in the
other's dependency. Architecture tests in the integration suite enforce all three rules, along with
the library carrying no Razor, MVC, Entity Framework, or transport dependency, and framework
extension points staying out of the host shell.

## Test Coverage

Run the full suite with:

```bash
dotnet test Abacus.Run.slnx --no-restore
```

The integration tests exercise the real ASP.NET Core host and its HTTP endpoints; unit tests cover the runtime components and stores independently.

`Abacus.Run.BrokerTests` is the one suite with an external dependency, and deliberately so — a transport claim that has never touched the wire is not a verified claim. Testcontainers starts Redis and RabbitMQ itself, so there is nothing to run beforehand:

```bash
dotnet test tests/Abacus.Run.BrokerTests/Abacus.Run.BrokerTests.csproj
```

Without a container runtime these tests report as **skipped** rather than failed, so a machine or CI leg without Docker still gets a green suite. Watch for that in the output: a run reporting skips has verified nothing about the transports.

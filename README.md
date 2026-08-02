# Abacus Run

Abacus Run is a .NET workflow runtime and HTTP host for durable, observable workflow instances. It provides workflow version resolution, bounded concurrency, retries, checkpoints, approvals, event history, server-sent events, cancellation, reruns, and redacted audit/logging surfaces.

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
- `POST /instances/{id}/cancel`
- `POST /instances/{id}/retry`
- `POST /instances/{id}/rerun`
- `POST /instances/{id}/suspend`
- `POST /instances/{id}/resume`

`GET /instances/{id}/events` is an SSE stream. Instance queries support status, workflow, correlation ID, limit, and offset filters.

### Approvals

- `GET /approvals`
- `GET /approvals/{approvalId}`
- `GET /instances/{id}/approvals`
- `POST /approvals/{approvalId}/decision`

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

The default host uses in-memory instance, event, log, approval, checkpoint, blob, and audit stores. Treat this configuration as development-oriented until durable store implementations are supplied.

Set `Abacus:SqlServer:ConnectionString` to enable the EF Core SQL Server stores and `Abacus:Redis:ConnectionString` to enable Redis Streams and control messages. `AddAbacus` keeps the in-memory stores when these settings are absent.

## Project Layout

| Project | Responsibility |
| --- | --- |
| `src/Abacus.Run` | Headless framework: workflow runtime, dispatch, executors, middleware, in-memory store defaults, and HTTP API endpoints |
| `src/Abacus.Run.Service` | Deployable host: control-plane UI, SQL Server stores, Redis event bus, and startup wiring |
| `tests/Abacus.Run.UnitTests` | Unit coverage for runtime behavior; references the library only |
| `tests/Abacus.Run.IntegrationTests` | HTTP, control-plane, and architecture-boundary coverage against the real host |
| `tests/Abacus.Run.ChaosTests` | Failure and lifecycle resilience coverage |
| `tests/Abacus.Run.LoadTests` | Load-oriented test project |

Folders inside each project:

```
src/Abacus.Run/               src/Abacus.Run.Service/
  Abstractions/                 ControlPlane/      Razor Pages backing services
  Api/                          Infrastructure/    SQL Server stores, Redis bus
  Core/                         Pages/             control-plane Razor Pages
  Dispatch/                     wwwroot/           control-plane CSS and JS
  EventBus/                     Program.cs
  Executors/                    AbacusServiceCollectionExtensions.cs
  Middlewares/
  Persistence/
```

The library carries no Razor, MVC, Entity Framework, or Redis dependency; an architecture test in the integration suite enforces this.

## Test Coverage

Run the full suite with:

```bash
dotnet test Abacus.Run.slnx --no-restore
```

The integration tests exercise the real ASP.NET Core host and its HTTP endpoints; unit tests cover the runtime components and stores independently.

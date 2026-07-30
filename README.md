# Abacus Run

Abacus Run is a .NET workflow runtime and HTTP host for durable, observable workflow instances. It provides workflow version resolution, bounded concurrency, retries, checkpoints, approvals, event history, server-sent events, cancellation, reruns, and redacted audit/logging surfaces.

The runtime is built on Microsoft Agent Framework workflows. Stores are exposed through interfaces so the in-memory implementation can be replaced by durable persistence without changing workflow definitions.

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
dotnet run --project src/Abacus.Run.Api/Abacus.Run.Api.csproj
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
    .AddWorkflowHost(builder.Configuration)
    .AddWorkflow<OrderWorkflow>()
    .AddBuiltInMiddleware()
    .AddBackgroundServices();
```

`OrderWorkflow` must implement `IWorkflowDefinition` or `IWorkflowDefinition<TContext, TResult>`. Use `WorkflowBuildContext.Node(...)` to attach host executors and declare approval gates.

## API Surface

### Workflow catalog

- `GET /workflows`
- `GET /workflows/{name}`
- `POST /workflows/{name}/instances`

The start endpoint accepts an optional `version` query parameter and supports `Idempotency-Key` and `Prefer: wait=<seconds>` headers.

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

## Project Layout

| Project | Responsibility |
| --- | --- |
| `Abacus.Run.Abstractions` | Workflow definitions, executors, approvals, instances, and middleware contracts |
| `Abacus.Run.Core` | Registry, runner, dispatch, retries, gates, events, redaction, and host options |
| `Abacus.Run.Executors` | API, LLM, template, and supporting executors |
| `Abacus.Run.Middleware` | Built-in workflow and executor middleware, including drift monitoring |
| `Abacus.Run.Persistence` | In-memory stores and checkpoint overflow handling |
| `Abacus.Run.Api` | ASP.NET Core host, endpoints, instance control, and SSE |
| `tests/Abacus.Run.UnitTests` | Unit coverage for runtime behavior |
| `tests/Abacus.Run.IntegrationTests` | HTTP and end-to-end host coverage |

## Test Coverage

Run the full suite with:

```bash
dotnet test Abacus.Run.slnx --no-restore
```

The integration tests exercise the real ASP.NET Core host and its HTTP endpoints; unit tests cover the runtime components and stores independently.

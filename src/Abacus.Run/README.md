# Abacus.Run

A headless, durable workflow host built on [Microsoft Agent Framework](https://github.com/microsoft/agent-framework)
workflows. Register workflow definitions, start instances over HTTP, and let the host own the
durable lifecycle: checkpointing, resumption after process loss, retry-vs-dead-stop policy, human
approval gates, and event streaming.

The package is headless — it serves the HTTP API and nothing else. It takes no dependency on Razor,
MVC, Entity Framework, or Redis, so referencing it does not impose a UI or a storage choice.

## Install

```bash
dotnet add package Abacus.Run
```

Requires .NET 9 and an ASP.NET Core host (the package uses the `Microsoft.AspNetCore.App` shared
framework).

## Quick start

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddWorkflowHost(builder.Configuration)
    .AddBuiltInMiddleware()      // OpenTelemetry, request/response logging, LLM drift
    .AddBackgroundServices()     // dispatcher + approval expiry sweeper
    .AddWorkflow<OrderWorkflow>();

var app = builder.Build();
app.MapWorkflowApi();
app.Run();
```

With no storage configured the host runs entirely on in-memory implementations, so it works
standalone for local development and tests.

## Authoring a workflow

```csharp
public sealed class OrderWorkflow : IWorkflowDefinition<OrderContext, OrderResult>
{
    public string Name => "order";
    public string Version => "1.0.0";

    public ValueTask<Workflow> BuildAsync(WorkflowBuildContext ctx, CancellationToken ct)
    {
        var validate = ctx.Node(new ValidateExecutor("validate"));

        // Gating is configuration, not code: this executor is unchanged by the gate.
        var pay = ctx.Node(new PayExecutor("post-payment"), gate => gate
            .When<OrderContext>(o => o.Amount > 25_000m)
            .Reason("AmountAboveThreshold")
            .AssignTo("group:finance-approvers")
            .ExpiresAfter(TimeSpan.FromHours(8))
            .OnExpiry(ExpiryAction.DeadStop));

        return new ValueTask<Workflow>(new WorkflowBuilder(validate)
            .AddEdge(validate, pay)
            .WithOutputFrom(pay)
            .Build());
    }

    // The workflow decides what a failure means; the host applies the decision.
    public FailureDisposition Classify(WorkflowFailure f) => f.Exception switch
    {
        ApiCallFailureException { StatusCode: 409 } => FailureDisposition.DeadStop,
        InsufficientFundsException => FailureDisposition.Escalate,
        _ => DefaultFailureClassifier.Instance.Classify(f)
    };
}
```

Executors derive from `HostExecutor<TIn, TOut>` and implement `ExecuteCoreAsync`. `HandleAsync` is
sealed so gate evaluation and the middleware pipeline cannot be bypassed.

## What it provides

| Area | Behaviour |
| --- | --- |
| Execution | Versioned registration; instances pin the version they started with |
| Durability | Checkpoint per superstep by default; resume from the last committed checkpoint |
| Multi-instance | Lease-based dispatch; at most one replica executes an instance at a time |
| Failure policy | The workflow's `Classify` chooses retry, dead-stop, or escalate |
| Approvals | Any executor can be gated by configuration; decisions resume the run on any replica |
| Middleware | Run-level and executor-level pipelines with `next()` semantics |
| Events | Gapless per-instance sequence, durable history, and SSE with `Last-Event-ID` resume |
| Executors | `ApiCallExecutor`, `LlmExecutor`, transform, delay, fan-in, human approval |
| Observability | OpenTelemetry spans and metrics, redacted request/response logging, LLM drift detection |

## HTTP API

`POST /workflows/{name}/instances` · `GET /instances/{id}` · `GET /instances/{id}/events` (SSE) ·
`GET /instances/{id}/events/history` · `GET /instances/{id}/graph` ·
`POST /instances/{id}/{cancel,rerun,retry,suspend,resume}` · `POST /approvals/{id}/decision`

## Swapping storage and transport

Every store and the event bus sit behind interfaces (`IInstanceStore`, `IEventStore`,
`IApprovalStore`, `ICheckpointStore<JsonElement>`, `INotificationBus`, and others). Register your own
implementation to displace the in-memory default:

```csharp
services.RemoveAll<IInstanceStore>();
services.AddSingleton<IInstanceStore, SqlServerInstanceStore>();
```

Implementations must preserve a few invariants: optimistic concurrency on instance updates, terminal
state never overwritten by a losing writer, exclusive renewable leases, unique ordered event
sequences per instance, and checkpoint indexes returned oldest-first.

## Links

- [Repository](https://github.com/CodeShayk/Abacus-Run)
- [Wiki](https://github.com/CodeShayk/Abacus-Run/blob/master/docs/wiki.md)

MIT licensed.

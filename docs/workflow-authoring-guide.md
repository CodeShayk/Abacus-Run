# Authoring workflows in C#

A complete reference for building a workflow definition in code, covering every capability the
framework offers a definition and how to reach it.

Companion to the [wiki](wiki.md): that is the orientation and the operational manual, this is the
authoring reference. Its mirror is
[Authoring workflows with the Abacus DSL](dsl-authoring-guide.md) — the same runtime, reached from
JSON instead of C#. If you are deciding between the two, read
[§19](#19-choosing-between-c-and-the-dsl) first.

---

## Contents

- [1. The model](#1-the-model)
- [2. Definition anatomy](#2-definition-anatomy)
- [3. Context and result contracts](#3-context-and-result-contracts)
- [4. Nodes and bindings](#4-nodes-and-bindings)
- [5. Built-in executors](#5-built-in-executors)
- [6. Custom executors](#6-custom-executors)
- [7. Templates](#7-templates)
- [8. Edges](#8-edges)
- [9. Approval gates](#9-approval-gates)
- [10. Notifications and events](#10-notifications-and-events)
- [11. Domain events: publishing, waiting, triggering](#11-domain-events-publishing-waiting-triggering)
- [12. Failure, retry and limits](#12-failure-retry-and-limits)
- [13. Audit records](#13-audit-records)
- [14. Engine context inside an executor](#14-engine-context-inside-an-executor)
- [15. Middleware](#15-middleware)
- [16. Registration and hosting](#16-registration-and-hosting)
- [17. Versions, identity and drift](#17-versions-identity-and-drift)
- [18. What a definition gets for free](#18-what-a-definition-gets-for-free)
- [19. Choosing between C# and the DSL](#19-choosing-between-c-and-the-dsl)
- [20. Sharp edges](#20-sharp-edges)
- [Appendix A — worked variations](#appendix-a--worked-variations)
- [Appendix B — options reference](#appendix-b--options-reference)

---

## 1. The model

> **A definition declares a graph; the framework runs it durably.**
>
> An author supplies nodes, the edges between them, and the policy around them — gates, failure
> dispositions, what the run emits, what it audits. Everything about *making that survive* — leases,
> checkpoints, retries, resumption, tenancy — belongs to the host and is not the definition's
> business.

Three consequences follow, and they explain most of what the rest of this document describes:

1. **A definition is data plus behaviour, and the behaviour is ordinary C#.** Unlike a DSL document
   there is no ceiling: a node can do anything a method can do. What you give up is the safety that
   comes from not being able to.
2. **The graph is built per attempt, not per process.** `BuildAsync` runs on every run and every
   resume, so it must be cheap and deterministic — the same instance rebuilding a *different* graph
   on resume will not match its own checkpoint.
3. **Nothing is required except the graph.** Audit records, event triggers, notification policy and
   context validation are separate opt-in interfaces. A definition that says nothing about them pays
   nothing for them.

A definition is registered once at startup, keyed by `(Name, Version)`, and is immutable thereafter.

---

## 2. Definition anatomy

```csharp
public interface IWorkflowDefinition<TContext, TResult> : IWorkflowDefinition
    where TContext : notnull
{
    string Name { get; }
    string Version { get; }

    ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken cancellationToken);

    FailureDisposition Classify(WorkflowFailure failure);
}
```

`ContextType`, `ResultType` and `Classify` all have default implementations on the generic interface,
so the smallest useful definition is a name, a version and a `BuildAsync`:

```csharp
public sealed record GreetingContext(string Name);
public sealed record GreetingResult(string Message);

public sealed class GreetingWorkflow : IWorkflowDefinition<GreetingContext, GreetingResult>
{
    public string Name => "greeting";
    public string Version => "1.0.0";

    public ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken ct)
    {
        ExecutorBinding greet = context.Node(new GreetingExecutor("greet"));

        return new ValueTask<Workflow>(new WorkflowBuilder(greet)
            .WithOutputFrom(greet)
            .WithName(Name)
            .Build());
    }
}
```

### The opt-in interfaces

| Interface | Declares | Section |
| --- | --- | --- |
| `IWorkflowDefinition<TContext, TResult>` | Name, version, context/result types, the graph | this section |
| `IAuditedWorkflowDefinition` | The shape of the workflow's own audit record | [§13](#13-audit-records) |
| `IDomainEventTriggeredWorkflow` | Topics that start an instance | [§11](#11-domain-events-publishing-waiting-triggering) |
| `INotifyingWorkflow` | Emission level, per-node overrides, SSE on/off, custom event names | [§10](#10-notifications-and-events) |
| `IContextValidatingWorkflow` | Validation of the start payload beyond the type bind | [§3](#3-context-and-result-contracts) |
| `IDocumentAuthoredWorkflow` | Provenance for the catalog — implemented by the DSL, rarely by hand | [§17](#17-versions-identity-and-drift) |

The runtime probes for each with `is`, so implementing one you do not need means declaring an empty
policy — which is not the same as declaring nothing. Approval gates are deliberately **not** an
interface: they are per node, declared inline where the node is attached.

### `WorkflowBuildContext`

What `BuildAsync` receives. It carries the run's identity as well as the attachment methods, so a
node can close over either.

| Member | Purpose |
| --- | --- |
| `InstanceId`, `TenantId` | This run's identity |
| `WorkflowName`, `WorkflowVersion` | What the registry resolved |
| `Attempt` | 1 on the first run, higher after a retry |
| `Services` | The host's `IServiceProvider` — resolve brokers, clients and stores from it |
| `Audit` | The recorder, present when the definition declares an audit record |
| `Node(executor, gate?)` | Attach a host executor, optionally gated |
| `RawNode(binding, gate?)` | Attach a raw framework or agent binding; a gate here throws |
| `Gates`, `Nodes` | What this build declared; read by the runtime and the catalog API |
| `ForInspection(...)` | A build context with no runtime attachment, for rendering a graph |

`Services` is nullable because a definition can be built outside a host — that is what
`ForInspection` is for, and what makes a definition unit-testable without DI. Inside a real run it is
always present, which is why `context.Services!` is the idiom in the examples here.

---

## 3. Context and result contracts

`TContext` is what a caller posts to start a run; `TResult` is what the terminal node yields.

```
POST /workflows/{name}/instances   { "context": { … } }
```

The registry binds the posted JSON to `TContext` before anything runs, and a bind failure is a `400`
with field errors rather than a failed instance. `TContext` must be non-null; a parameterless
constructor lets a workflow start with no payload at all.

Serialization is `System.Text.Json` with the host's options (`JsonOptions.Default`), so records with
positional parameters bind as you would expect and casing is web-standard.

### Validating beyond the type

A type bind establishes shape, not sense. `IContextValidatingWorkflow` runs **after** the bind
succeeds, and its errors surface the same way:

```csharp
public sealed class TransferWorkflow
    : IWorkflowDefinition<TransferContext, TransferResult>, IContextValidatingWorkflow
{
    public ContextValidationResult ValidateContext(JsonElement context)
        => context.TryGetProperty("amount", out JsonElement amount) && amount.GetDecimal() > 0
            ? ContextValidationResult.Valid
            : ContextValidationResult.Invalid("amount", "Must be greater than zero.");
}
```

Errors are keyed by field so a form can put each message beside the input it is about. Throwing
`WorkflowValidationException` from inside an executor is the other half of this: it dead-stops by
default rather than retrying, because a payload that failed validation will fail it again.

### When the context is not a POCO

`TContext` may be `JsonElement`, which is what the DSL uses: the workflow accepts arbitrary JSON and
answers for its own validation. Worth knowing because it changes routing — the engine dispatches the
start message **by type**, so the first node must accept exactly the declared context type or nothing
runs and the workflow completes having done nothing.

---

## 4. Nodes and bindings

`context.Node(...)` is the attachment point that makes a node a *host* node: it wires the middleware
pipeline, the approval gate, the audit recorder and the per-instance runtime, then returns the
`ExecutorBinding` the graph is built from.

```csharp
ExecutorBinding validate = context.Node(new Validate("validate"));
```

The **executor id** is the identity everything else hangs off:

- gate policies are stored per `(workflow, version, executorId)`;
- node state is projected by it;
- per-node notification overrides name it;
- the graph and node endpoints report it.

Renaming a node in a published version silently orphans any tenant policy written against the old id.
Change the version instead.

### Raw bindings

`RawNode(...)` attaches something the host did not create. Raw nodes join the graph but run **outside
the executor middleware pipeline** and cannot be approval-gated — passing a gate throws rather than
ignoring it, because a gate that quietly did nothing would be worse than one that was refused.

`ExecutorBinding` has implicit conversions from `Executor`, `AIAgent`, `RequestPort` and `string`,
and the framework supplies several ways to make one:

| Binding | From |
| --- | --- |
| `executor.BindExecutor()` | A raw framework `Executor` |
| `agent.BindAsExecutor(id)` | An `AIAgent` — the agent becomes a node |
| `workflow.BindAsExecutor(id)` | Another `Workflow`, as a **sub-workflow** node |
| `handler.BindAsExecutor<TIn>(id)` | A bare `Func<TIn, IWorkflowContext, CancellationToken, ValueTask>` |

A sub-workflow node runs the child graph **inline**. There is no separate instance row, lease or event
stream for it, and its nodes are not separately gateable or configurable. Use a sub-workflow to
compose graph *shape*; use an event trigger ([§11](#11-domain-events-publishing-waiting-triggering))
when you want a genuinely independent run.

Prefer `Node(...)` with a `HostExecutor<TIn, TOut>` whenever middleware, gates, audit or notifications
are wanted. A raw node gets none of them.

---

## 5. Built-in executors

| Executor | Shape | Purpose |
| --- | --- | --- |
| `TransformExecutor<TIn, TOut>` | `(id, Func<TIn, TOut>)` | Pure mapping |
| `DelegateExecutor<TIn, TOut>` | `(id, handler)` | General-purpose async work |
| `ApiCallExecutor` | `(id, ApiCallOptions, Func<HttpClient>)` | Templated HTTP call with egress control and idempotency key |
| `LlmExecutor` | `(id, LlmOptions, Func<string, IChatClient>, IModelPricing?)` | Chat model call with structured output, streaming and cost |
| `DelayExecutor` | `(id, TimeSpan, ITimerService)` | Durable wait — checkpoints and halts |
| `HumanApprovalExecutor<T>` | `(id)` | Marks the place a human decides |
| `FanInExecutor<TItem, TOut>` | `(id, aggregate)` | Aggregates a list of items into one message |
| `PublishDomainEventExecutor<T>` | `(id, broker, topic, …)` | Publishes a domain message, passing input through |
| `WaitForDomainEventExecutor<TIn, TPayload>` | `(id, subscriptions, topicFilter, …)` | Parks until a matching message arrives |

Each sets a `node.kind` in its `Metadata`, which is what the catalog and graph endpoints report.

### `TransformExecutor` and `DelegateExecutor`

The two general-purpose nodes. `TransformExecutor` takes a pure function; `DelegateExecutor` takes an
async handler with the engine context, and is the right answer for most one-off work that does not
deserve a class:

```csharp
ExecutorBinding total = context.Node(new TransformExecutor<Order, Priced>(
    "total", order => new Priced(order.Id, order.Lines.Sum(l => l.Quantity * l.UnitPrice))));

ExecutorBinding load = context.Node(new DelegateExecutor<Priced, Enriched>(
    "load", async (priced, ctx, ct) => new Enriched(priced, await _customers.GetAsync(priced.Id, ct))));
```

### `ApiCallExecutor`

Declarative outbound HTTP. Templates resolve against the message the node received
([§7](#7-templates)), the URL is checked against the allow-list before the request is made, and a
non-success status arrives as a typed `ApiCallFailureException` carrying the status, a body excerpt
and `Retry-After` — so the classifier can act on it without parsing a message.

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
        EnforceEgress = true,
        SendIdempotencyKey = true
    },
    () => context.Services!.GetRequiredService<IHttpClientFactory>()
        .CreateClient(ApiCallOptions.HttpClientName)));
```

It returns `ApiCallResult(StatusCode, Body, RawBody)` — `Body` is deserialized as `ResponseAs` when
set, and `RawBody` is always the text, so a non-JSON response is still readable.

The `Idempotency-Key` is `{instanceId}:{executorId}:{attempt}`: deterministic within an attempt so a
replayed superstep re-sends the same key, distinct across attempts so a retry is a new request. Use
the named client (`ApiCallOptions.HttpClientName`) and outbound logging, redaction and the egress
guard all apply without the node knowing.

### `LlmExecutor`

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
        StreamDeltas = true,
        EmitCompletion = true,
        MaxReparseAttempts = 2
    },
    model => context.Services!.GetRequiredService<IChatClient>(),
    context.Services!.GetService<IModelPricing>()));
```

Returns `LlmResult(Value, Text, InputTokens, OutputTokens, ModelId, FinishReason)` with `CostUsd` and
`Elapsed`. Three things are easy to get wrong:

- **`StructuredOutput` is enforced.** The model's output is parsed into the declared type, retried up
  to `MaxReparseAttempts`, and then raised as `StructuredOutputException` — which dead-stops, because
  a model that cannot produce the shape twice will not produce it on a third attempt either.
- **`CostUsd` is null without pricing.** Pass `IModelPricing` or cost is *absent*, not zero. Prices
  bind from `Abacus:Llm:Pricing:<model>`.
- **`StreamDeltas` is live-only.** `llm.delta` frames reach SSE subscribers and are not written to the
  durable log; `llm.completed` is.

The client resolver is `Func<string, IChatClient>` so a host with several providers selects by model
id. With one registered `IChatClient`, `Model` is passed through as the model id but selects nothing.

### `DelayExecutor`

Does not sleep. It writes a timer row, checkpoints and halts, so the instance releases its lease: a
24-hour delay costs no execution capacity and survives a restart. Returns
`TimerElapsed(ExecutorId, WakeAt)`.

```csharp
var timers = context.Services!.GetRequiredService<ITimerService>();
ExecutorBinding cooloff = context.Node(new DelayExecutor("cool-off", TimeSpan.FromHours(24), timers));
```

> **`ITimerService` is not registered by the framework or the shipped host.** A `delay` node needs an
> implementation; register one before using this executor. The DSL test fixture has an in-memory one
> worth copying for local work.

### `HumanApprovalExecutor<T>`

Identity work — it returns its input unchanged. **The pause comes from the gate, not from the class**:
the executor exists so the place a human decides is a node in the graph rather than configuration on
some other node. Attach it with a gate:

```csharp
ExecutorBinding signOff = context.Node(
    new HumanApprovalExecutor<Order>("sign-off"),
    gate => gate.Mode(ExecutionMode.RequireApproval).AssignTo("group:ops"));
```

### `FanInExecutor<TItem, TOut>`

Aggregates a list into one message:

```csharp
ExecutorBinding decide = context.Node(new FanInExecutor<CheckResult, Decision>(
    "decide", checks => new Decision(checks.All(c => c.Passed))));
```

> **It does not work as the target of `AddFanInBarrierEdge`.** The executor declares
> `HostExecutor<List<TItem>, TOut>`, but the barrier's edge runner type-checks each released message
> **individually** — a target declaring `List<TItem>` matches nothing and the delivery is dropped as a
> type mismatch. Aggregate in a custom executor that holds arrivals and emits on the last one, as the
> DSL's `fan-in` node does. See [§20](#20-sharp-edges).

### The domain-event executors

Covered with topics, scopes and triggers in
[§11](#11-domain-events-publishing-waiting-triggering).

---

## 6. Custom executors

Derive from `HostExecutor<TIn, TOut>` and implement `ExecuteCoreAsync`. `HandleAsync` is **sealed**:
gate evaluation and the middleware pipeline live there and must not be overridden away.

```csharp
public sealed class PriceOrder(string id) : HostExecutor<Order, Priced>(id)
{
    public override IReadOnlyDictionary<string, object?> Metadata =>
        new Dictionary<string, object?> { ["node.kind"] = "pricing" };

    protected override async ValueTask<Priced> ExecuteCoreAsync(
        Order input, IWorkflowContext context, CancellationToken cancellationToken)
    {
        if (Runtime.Audit is { } audit)
        {
            await audit.RecordAsync("step", input.Id, new { lines = input.Lines.Count }, cancellationToken);
        }

        return new Priced(input.Id, input.Lines.Sum(l => l.Quantity * l.UnitPrice));
    }
}
```

`TOut` is constrained to a reference type because the pause path returns `null`, and the engine only
auto-sends non-null handler results. That is precisely what lets a gated or waiting executor park
without emitting a bogus message downstream.

### `Runtime` — what the host adds

`Runtime` is injected after construction, because executors are built before the instance id is
known. Outside a host it is `HostExecutorRuntime.Unattached`, which is what makes an executor
directly unit-testable.

| Member | Purpose |
| --- | --- |
| `InstanceId`, `TenantId` | This run's identity |
| `Attempt`, `CurrentSuperstep` | Where in the run this invocation is |
| `Descriptor` | Executor id, type, workflow name/version, execution mode, metadata |
| `Services` | The host's provider, when attached |
| `Audit` | The recorder, when the definition declares a record — **null otherwise, so guard it** |
| `Notify` | Event emission, when attached — likewise nullable |
| `Gates`, `Approvals`, `Pipeline` | Framework wiring; an author reads these, never sets them |

Override `Metadata` to describe the node for the graph endpoint and for classifiers: `WorkflowFailure`
carries it, so a classifier can decide by node *kind* without hard-coding ids.

### Parking deliberately

The park pattern is `RequestHaltAsync()` then a null result:

```csharp
protected override async ValueTask<Payload?> ExecuteCoreAsync(
    Order input, IWorkflowContext context, CancellationToken ct)
{
    if (await AlreadyDelivered(ct) is { } payload) return payload;   // second pass

    await Register(input, ct);
    await context.RequestHaltAsync();
    return null!;                                                    // never auto-sent
}
```

An executor written this way **runs twice**, so everything before the halt must be idempotent. This
is exactly how gates and `WaitForDomainEventExecutor` work; reach for it when a node waits on
something the framework does not model.

---

## 7. Templates

`ApiCallExecutor` and `LlmExecutor` bind `{{ path }}` placeholders through the shared
`TemplateEngine`. Deliberately not an expression language: templates appear in URLs and request
bodies, so the surface is kept small enough to reason about.

| Rule | Behaviour |
| --- | --- |
| `{{ Field }}`, `{{ Nested.Field }}` | Dotted path over the message the node received |
| `{{ context.Field }}`, `{{ input.Field }}` | `context` and `input` are aliases for the root |
| Source kinds | POCO properties (case-insensitive), `IDictionary<string, object?>`, `JsonElement` |
| Absent path | Renders as empty. A template never fails a run over a missing field |
| Unterminated `{{` | Emitted verbatim rather than silently truncating a URL |
| Formatting | `IFormattable` values render with `InvariantCulture` |

A message type can take over resolution entirely by implementing `ITemplateBindingSource`:

```csharp
public interface ITemplateBindingSource
{
    string? Resolve(string expression);   // null renders as empty
}
```

Checked before the dotted-path walk, so a self-resolving message keeps full control of its own
placeholder syntax. This is what lets the DSL's envelope answer `{{ $ctx.orderId }}` and
`{{ $.total * 1.2 }}` inside the same `ApiCallExecutor` a compiled workflow uses — and it is the seam
to reuse for any message carrying more than one addressable object.

---

## 8. Edges

Edges come from the Agent Framework's `WorkflowBuilder`. The constructor takes the start node, and
`WithOutputFrom` names the node (or nodes) whose result becomes the workflow's result.

```csharp
Workflow workflow = new WorkflowBuilder(validate)
    .AddEdge(validate, enrich)
    .AddEdge<Order>(enrich, escalate, condition: o => o is { Amount: > 10_000m })
    .AddEdge<Order>(enrich, settle,   condition: o => o is { Amount: <= 10_000m })
    .AddFanOutEdge(settle, [notifyOps, notifyCustomer])
    .AddFanInBarrierEdge([notifyOps, notifyCustomer], complete)
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

Three rules worth internalising:

- **Two conditional edges out of one node is the branch.** There is no switch construct. Make the
  predicates exhaustive: a message matching neither simply stops there and the run completes with **no
  output**, which looks like success and is the hardest branch bug to find.
- **The condition parameter is `T?`.** A pattern (`o is { … }`) reads better than a null-forgiving
  dereference and handles the null case explicitly.
- **Routing is by type as well as by edge.** A target whose `TIn` does not match the message is not
  an error at build time; the delivery is dropped at run time. Mismatched node shapes are the usual
  cause of "the graph ran and nothing happened".

---

## 9. Approval gates

A node attached with no gate runs autonomously. A gate is declared where the node is attached, and
nowhere else:

```csharp
ExecutorBinding settle = context.Node(new Settle("settle"), gate => gate
    .Mode(ExecutionMode.RequireApproval)
    .When<Order>(order => order.Amount > 25_000m)   // implies Conditional
    .Reason("RegulatedSettlement")
    .AssignTo("group:finance", "user:cfo")
    .RequireApprovers(2)
    .ExpiresAfter(TimeSpan.FromHours(8))
    .OnExpiry(ExpiryAction.Escalate, "group:exec")
    .AllowModification()
    .RequireSegregationOfDuties()
    .Locked());
```

| Builder call | Default | Notes |
| --- | --- | --- |
| `Mode(ExecutionMode)` | `RequireApproval` when a gate block is present | `Autonomous`, `RequireApproval`, `Conditional` |
| `When<T>(predicate)` / `WhenAsync(predicate)` | — | Sets `Conditional`. A message of another type never trips the gate |
| `Reason(string)` | none | Surfaced on the approval request |
| `AssignTo(params string[])` | empty — anyone may decide | `group:` and `user:` principals |
| `RequireApprovers(int)` | 1 | Quorum; the instance stays parked until it is met |
| `ExpiresAfter(TimeSpan)` | 24 hours | |
| `OnExpiry(action, escalateTo)` | `DeadStop` | `DeadStop`, `Reject`, `AutoApprove`, `Escalate` |
| `AllowModification(bool)` | false | Lets a decider amend the input the node will receive |
| `RequireSegregationOfDuties(bool)` | false | The initiator may not be the decider |
| `Locked(bool)` | false | Tenants may tighten, never loosen |

**Every gated node is reconfigurable per tenant at run time unless the author calls `.Locked()`.** A
locked gate is the author's floor: the API rejects a write that would loosen it, and the evaluator
re-tightens anything that reached the policy store by another route.

The pause is safe by construction: gate evaluation happens in `HandleAsync` **before** the pipeline
runs, so when an instance parks nothing downstream of that point has executed and no side effect has
occurred. A rejected decision arrives as `ApprovalRejectedException` through the classifier, so a
workflow can treat rejection as a branch rather than as a crash.

See [Human approval gates](wiki.md#human-approval-gates) for the decision flow and
[Tenant executor configuration](wiki.md#tenant-executor-configuration) for precedence.

---

## 10. Notifications and events

Every run emits events to a durable log; some also stream over SSE. A definition that says nothing
gets `NotificationPolicy.Default` — everything, logged and streamed.

```csharp
public NotificationPolicy Notifications { get; } = new()
{
    Level = NotificationLevel.Lifecycle,
    StreamEvents = true,
    ByNode = new Dictionary<string, NotificationLevel>(StringComparer.Ordinal)
    {
        ["settle"] = NotificationLevel.Standard
    },
    Emits = ["order.repriced"]
};
```

| Level | Emits |
| --- | --- |
| `Minimal` | Start, output and terminal only |
| `Lifecycle` | Adds superstep boundaries — progress without per-node chatter |
| `Standard` | Adds `executor.*`, `llm.*` and workflow-defined events. The default |

Two properties of the model matter more than the levels:

- **The durable log is not optional.** `StreamEvents = false` turns off the *live stream* only; every
  event is still written and the run is always reconstructable. The SSE endpoint then returns `409`
  naming the history route, because a silently empty stream is indistinguishable from a stalled run.
- **Some events are never suppressed.** Terminal events, approvals, control actions and broker
  deliveries are facts about the system rather than run chatter, and a workflow has no business
  hiding any of them. Suppression is also decided *before* a sequence number is taken, so a quiet
  workflow leaves no holes for `Last-Event-ID` catch-up to wait on.

### Emitting your own

`Emits` advertises names on `GET /workflows/{name}`; the node does the emitting:

```csharp
if (Runtime.Notify is { } notify)
{
    await notify.NotifyAsync("documents.scanned", new { count = input.Documents.Count }, ct);
}
// → event type: custom.documents.scanned
```

The `custom.` prefix is applied by the framework and cannot be opted out of, so a workflow can never
shadow a framework event however it names its own. A malformed name (empty, whitespace, empty dotted
segment) throws rather than emitting, because an event with a broken type is indistinguishable from
one that was never sent.

---

## 11. Domain events: publishing, waiting, triggering

Three separate capabilities that happen to share a topic space.

### Publishing

```csharp
var broker = context.Services!.GetRequiredService<IDomainEventBroker>();

ExecutorBinding publish = context.Node(new PublishDomainEventExecutor<OrderPlaced>(
    "publish-order-placed", broker,
    topic: "orders.placed",
    payload: o => new { o.OrderId, o.Amount },   // defaults to the input
    correlationKey: o => o.OrderId,
    scope: DeliveryScope.Local));
```

The node **passes its input through unchanged** — publishing is a side effect on the way past, so it
drops into an existing edge without rewiring the graph. The message carries the run's tenant and
source instance id automatically.

`DeliveryScope.Distributed` on a broker that cannot deliver it throws at **construction**, not at
publish time: that is a composition mistake, and finding it on the first message would mean finding
it in production. The topic overload validates the pattern at construction too.

### Waiting

```csharp
var subscriptions = context.Services!.GetRequiredService<IDomainEventSubscriptionStore>();

ExecutorBinding awaitPayment = context.Node(
    new WaitForDomainEventExecutor<OrderContext, PaymentSettled>(
        "await-settlement", subscriptions,
        topicFilter: "payment.settled",
        correlationKey: o => o.OrderId,
        timeout: TimeSpan.FromDays(3),
        onExpiry: WaitExpiryAction.DeadStop));   // or Resume, to take a timeout branch
```

It **runs twice**: the first pass registers a durable subscription and parks; after delivery the
runner replays to this executor, which finds the payload and returns it. Anything it does before
parking therefore happens twice — keep it to registering the wait. The instance releases its lease
while parked, so waiting for days costs nothing.

### Triggering

```csharp
public IReadOnlyList<DomainEventTrigger> Triggers =>
[
    new DomainEventTrigger { TopicFilter = "orders.placed" },
    new DomainEventTrigger
    {
        TopicFilter = "orders.*.expedited",
        CorrelationKey = "premium",                 // only messages with this key
        WorkflowVersion = "2.0.0",                  // pin, rather than resolving latest
        ContextSelector = m => m.PayloadJson        // remap when the payload is not the context
    }
];
```

The payload becomes the instance context and the message's correlation key becomes the instance's
correlation id. Redelivery is absorbed by the launcher's idempotency key, so a message cannot start
the same workflow twice.

Topics are dot-segmented with `*` (one segment) and `#` (remainder) wildcards; `TopicPattern`
validates both patterns and topics, and publishing an invalid topic fails the node rather than
emitting something unroutable. Scope is a property of the *message*, not a different call — see
[Local by default, global by declaration](wiki.md#local-by-default-global-by-declaration).

---

## 12. Failure, retry and limits

`Classify` decides what a thrown exception means for the instance.

| Disposition | Effect |
| --- | --- |
| `Retry` | Backoff and try again, until `MaxAttempts` or `MaxLifetimeHours` |
| `DeadStop` | Terminal. Retrying cannot help, so do not burn attempts discovering that |
| `Escalate` | Terminal, and flagged for operator attention |

`WorkflowFailure` carries `ExecutorId`, `Exception`, `AttemptCount`, `Superstep` and the executor's
`Metadata`, so a classifier can decide differently per node without inspecting message text.

```csharp
public FailureDisposition Classify(WorkflowFailure failure) => failure.Exception switch
{
    InsufficientFundsException   => FailureDisposition.DeadStop,
    ReconciliationBreakException => FailureDisposition.Escalate,
    _ => DefaultFailureClassifier.Instance.Classify(failure)
};
```

**Always delegate the default case.** The framework already knows a rate limit is worth retrying and
a validation error is not; a definition should only state where its own domain disagrees.

### What the default classifier already does

| Exception | Disposition |
| --- | --- |
| `WorkflowDeadStopException`, `ApprovalRejectedException` | `DeadStop` |
| `WorkflowValidationException`, `StructuredOutputException`, `JsonException` | `DeadStop` |
| `LlmRateLimitException`, `LlmOverloadedException` | `Retry` |
| `ApiCallFailureException` 408, 429, ≥ 500 | `Retry` |
| `ApiCallFailureException` other 4xx | `DeadStop` |
| `TimeoutException`, `TaskCanceledException`, `HttpRequestException` | `Retry` |
| Anything else | `Retry` |

Throwing `WorkflowDeadStopException` from inside an executor is the direct way to say "this run is
over" without routing the decision through the classifier at all.

### Retry mechanics

Retries are the host's, configured rather than authored — `WorkflowHost:Retry` sets `MaxAttempts`
(5), exponential backoff from `BackoffBaseSeconds` (2) capped at `BackoffCapSeconds` (300), full
jitter, and `MaxLifetimeHours` (24) as the wall-clock ceiling. An attempt resumes **from the last
checkpoint**, not from the beginning, so a retry re-runs the failed superstep rather than the run.

That is why side effects inside an executor must be idempotent, and why `ApiCallExecutor` sends an
idempotency key by default.

---

## 13. Audit records

An audit record is a workflow's own account of what it did — separate from the event log, which is
the framework's account of what happened. Declaring one is opt-in, and the shape is the workflow's
because only the workflow knows what is audit-significant about its run.

```csharp
public AuditRecordDefinition AuditRecord { get; } = new(
    "order", "One order, as processed.",
    [
        new AuditSectionDefinition("submission", "What was submitted.", Multiple: false),
        new AuditSectionDefinition("step", "One processing step."),
        new AuditSectionDefinition("outcome", "How the run settled.", Multiple: false)
    ]);
```

Executors then write to the recorder the runtime hands them:

```csharp
if (Runtime.Audit is { } audit)
{
    await audit.OpenAsync(input.OrderId, new Dictionary<string, object?> { ["lines"] = n }, ct);
    await audit.RecordAsync("step", key: input.LineId, new { accepted = true }, ct);
    await audit.CloseAsync(AuditRecordStatus.Completed, ct);
}
```

| Call | Contract |
| --- | --- |
| `OpenAsync(rootKey, attributes, ct)` | Opens or re-opens the root. Idempotent per instance, so a retried attempt reuses its record rather than starting a second one |
| `RecordAsync(sectionKind, key, payload, ct)` | Appends an entry. `sectionKind` must be declared; `key` groups entries within a kind |
| `CloseAsync(status, ct)` | Settles the record. `AuditRecordStatus.Completed` / `Failed`, or a status of your own |

Two properties to rely on:

- **Keying makes a retry correct.** An entry with the same `(instance, kind, key)` replaces the
  earlier one, so a re-run executor corrects its record instead of appending a second, contradictory
  entry.
- **Recording never fails the work it describes.** Storage failures are swallowed and logged by
  contract. An audit write is not a place to put a precondition.

Storage is workflow-agnostic (`IAuditRecordStore`: a root, string-typed sections, JSON payloads), so a
new workflow needs no schema change. The record surfaces on the instance state route, and
[the shipped example](../src/Abacus.Run.Service/Workflows/ExampleOrder/ExampleOrderWorkflow.cs) is
written to be read as the answer to "what does a definition have to do to get one".

---

## 14. Engine context inside an executor

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

**Use `QueueStateUpdateAsync` rather than executor fields for anything that must survive a restart.**
An executor instance is rebuilt by `BuildAsync` on resume, and a field is gone with it. This is the
single most common source of "it works until it resumes".

`YieldOutputAsync` is checked against the executor's declared output type, so a node cannot yield a
shape it did not declare.

---

## 15. Middleware

Two seams, both registered at composition rather than declared by a workflow — cross-cutting concerns
are the host's business, not an author's. Lower `Order` runs earlier in the outer pipeline.

```csharp
public sealed class TimingMiddleware : IExecutorMiddleware
{
    public int Order => 10;

    public bool AppliesTo(ExecutorDescriptor descriptor) => descriptor.ExecutorId != "noisy";

    public async ValueTask InvokeAsync(
        ExecutorInvocationContext context, ExecutorDelegate next, CancellationToken ct)
    {
        long start = Stopwatch.GetTimestamp();
        await next(context, ct);
        Record(context.Descriptor.ExecutorId, Stopwatch.GetElapsedTime(start), context.Succeeded);
    }
}
```

| Seam | Context | Wraps |
| --- | --- | --- |
| `IExecutorMiddleware` | `ExecutorInvocationContext` | Each executor invocation |
| `IWorkflowMiddleware` | `WorkflowInvocationContext` | A whole run |

`ExecutorInvocationContext.Exception` is **settable**, so middleware can observe, replace or swallow a
failure as the pipeline unwinds — which is how retry-shaping and drift detection work without the
workflow knowing. `Input` and `Output` are settable too; `Items` carries per-invocation state, with
`MiddlewareContextKeys` naming the framework's own entries (`outbound.call`, `llm.usage`,
`llm.prompt_version`).

`IOutboundCallHandle` is the interesting one: middleware can read the outbound `HttpRequestMessage`
of a built-in executor and **short-circuit it** with `SetSyntheticResponse`, which is how a call is
stubbed or replayed without the node knowing it happened.

`AddBuiltInMiddleware()` supplies OpenTelemetry spans for runs and executors, request/response
logging with redaction, and LLM drift monitoring.

---

## 16. Registration and hosting

```csharp
builder.Services
    .AddAbacus(builder.Configuration)          // framework + infrastructure selection + control plane
    .AddWorkflow<OrderWorkflow>()              // resolved from DI
    .AddWorkflow(new ShipOrderWorkflow())      // or supplied directly
    .AddExecutorMiddleware<TimingMiddleware>()
    .AddWorkflowMiddleware<CorrelationMiddleware>();
```

`AddAbacus` is this host's composition; the framework's own entry point is
`AddWorkflowHost(configuration)`, which registers every store behind an interface with in-memory
defaults, plus `AddBuiltInMiddleware()` and `AddBackgroundServices()`.

| Call | Registers |
| --- | --- |
| `AddWorkflowHost(config, configure?)` | Runtime, stores, checkpoints, events, broker, options |
| `AddWorkflow<T>()` / `AddWorkflow(instance)` | A definition, as `IWorkflowDefinition` |
| `AddExecutorMiddleware<T>()` / `(instance)` | An executor-level seam |
| `AddWorkflowMiddleware<T>()` | A run-level seam |
| `AddBuiltInMiddleware()` | Telemetry, logging, drift |
| `AddBackgroundServices()` | Dispatcher, expiry sweepers, broker router |

**Without `AddBackgroundServices()` instances are created and stay `Pending`** — nothing executes
them. Two definitions with the same name and version fail startup rather than one silently winning.

### What a definition may need from the host

| Dependency | Needed by | Registered by default? |
| --- | --- | --- |
| `ITimerService` | `DelayExecutor` | **No** — supply one |
| `IChatClient` (or a resolver) | `LlmExecutor` | No — supply one |
| `IModelPricing` | `costUsd` on LLM events | Bound from `Abacus:Llm:Pricing:*`; absent means unknown, not free |
| `IHttpClientFactory` | `ApiCallExecutor` | Standard ASP.NET registration; use `ApiCallOptions.HttpClientName` |
| `IDomainEventBroker`, `IDomainEventSubscriptionStore` | Publish / wait / trigger | Yes — in-process and in-memory |

Egress is enforced by default (`WorkflowHost:Egress:Enforce`), and an `ApiCallExecutor` whose URL is
not on an allow-list is refused before the request is made.

---

## 17. Versions, identity and drift

- **Executor ids are the key for tenant gate policies**, stored per workflow *version*. A tenant's
  configuration does not carry forward to a new version, so a version bump starts from the author's
  declared gates again.
- **An in-flight instance keeps the version it started on.** The registry resolves by the instance's
  recorded version, so redeploying never changes the shape of a run already underway.
- **`POST /instances/{id}/rerun` in restart mode creates the new instance at the *current* version** —
  the one case where a rerun can behave differently from the original.
- **A published `(name, version)` should be treated as immutable.** The framework enforces uniqueness
  at startup; it cannot tell that you changed the body of a node and kept the version. The DSL *can*
  and does, by hashing the document — which is one honest reason to prefer it for frequently-edited
  workflows.

The catalog reports where a version was authored: `GET /workflows/{name}` carries `source` —
`"compiled"` for a C# definition, `"dsl"` for a document — and a `documentHash` for the latter. A
definition may implement `IDocumentAuthoredWorkflow` to report its own provenance, which is how the
DSL does it without the framework knowing any front end exists.

---

## 18. What a definition gets for free

None of this is declared by a workflow, because none of it is a workflow's business.

| Capability | How it applies |
| --- | --- |
| **Checkpointing and resume** | Every superstep checkpoints; a parked instance releases its lease and resumes on any replica |
| **At-least-once execution** | Lease-based dispatch, retry with backoff, resume from the last checkpoint |
| **Executor and workflow middleware** | Every `Node(...)` runs the full pipeline |
| **Redaction** | Applied at write time, so the history API cannot leak what the live stream withheld |
| **Egress control** | Built-in HTTP nodes go through `EgressGuard` |
| **Multi-tenancy** | Definitions are global; instances are tenant-scoped |
| **Instance controls** | `cancel`, `suspend`, `resume`, `retry`, `rerun` |
| **Observability** | Event history, SSE with `Last-Event-ID` catch-up, instance logs, the graph endpoint |
| **Tenant gate configuration** | Every gated node, unless `.Locked()` |
| **Approval flow** | Quorum, expiry, escalation, segregation of duties, modification |

---

## 19. Choosing between C# and the DSL

Both front ends produce an `IWorkflowDefinition`, register in the same catalog, and run on the same
runtime. The choice is about *who* changes the workflow and *what* it needs to do.

| Reach for C# when | Reach for the DSL when |
| --- | --- |
| The work is computation — iterating a collection, aggregating, arithmetic over a domain model | The work is composition of nodes the host already ships |
| The graph needs raw bindings, agents or sub-workflows | The document only needs the built-in kinds and registered custom nodes |
| Nodes carry real logic worth unit-testing as code | The change is a threshold, a topic, an edge, a prompt |
| The definition should be reviewed as code, with the domain types it uses | The definition should be editable without a build, and validated against a schema |

The DSL's own [framework coverage map](dsl-authoring-guide.md#17-framework-coverage-map) is the
authoritative statement of what a document can and cannot reach. The short version: everything in
this guide except `DelegateExecutor`, raw/agent/sub-workflow bindings, `IWorkflowContext` access, and
iteration — each of which is available to a document through a **custom node**, which is a small C#
class registered by name.

They mix freely in one host. A common shape is domain logic in custom nodes written once, with the
graph that composes them authored as a document.

---

## 20. Sharp edges

Real defects and traps, stated rather than left to be discovered.

**`FanInExecutor<TItem, TOut>` cannot be the target of `AddFanInBarrierEdge`.** It declares
`HostExecutor<List<TItem>, TOut>`, but the barrier's edge runner type-checks each released message
individually, so a `List<TItem>` target matches nothing and deliveries are dropped. Aggregate in a
custom executor that holds arrivals and emits when the expected count lands.

**No `ITimerService` is registered anywhere.** `DelayExecutor` requires one, so a stock host cannot
run a delay node at all.

**`BuildAsync` must be deterministic.** It runs per attempt, including on resume. A graph that varies
by wall-clock time, random choice or a mutable service will not match its own checkpoint.

**Executor fields do not survive a resume.** Use `QueueStateUpdateAsync`.

**A raw node has no gate, no middleware, no audit and no notifier.** That is the trade, and it is
enforced: passing a gate to `RawNode` throws.

**Non-exhaustive edge predicates complete a run with no output.** The run reports success. Prefer a
final unconditional edge, or assert on the result.

**`WaitForDomainEventExecutor` and any hand-written park run twice.** Everything before the halt must
be idempotent.

**`costUsd` is null, not zero, without `IModelPricing`.** Do not sum it and report a total.

---

## Appendix A — worked variations

Each recipe is a complete `BuildAsync` (or the declaration that matters), showing one shape in
isolation. They compose — see the wiki's
[worked definition](wiki.md#a-definition-using-all-of-it) for several at once.

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
ExecutorBinding decide  = context.Node(new CollectChecks("decide", expected: 3));

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

The barrier target takes the **individual** message type and counts arrivals itself — see
[§20](#20-sharp-edges) for why `FanInExecutor<TItem, TOut>` does not work here:

```csharp
private sealed class CollectChecks(string id, int expected) : HostExecutor<CheckResult, Decision>(id)
{
    private readonly List<CheckResult> _arrived = [];

    protected override ValueTask<Decision> ExecuteCoreAsync(
        CheckResult input, IWorkflowContext context, CancellationToken ct)
    {
        _arrived.Add(input);

        if (_arrived.Count < expected)
        {
            return ValueTask.FromResult<Decision>(null!);   // not yet: emit nothing
        }

        var decision = new Decision(_arrived.All(c => c.Passed));
        _arrived.Clear();                                    // ready for a second barrier release
        return ValueTask.FromResult(decision);
    }
}
```

The arrivals are held for the duration of one attempt, which is all a barrier release spans — the
executor is rebuilt on resume, so nothing is expected to survive it. This is exactly what the DSL's
`fan-in` node does, with the expected count read from the document instead of passed in.

A wide fan-out is the usual reason to set `NotificationLevel.Lifecycle` — see
[A.10](#a10-quiet-a-chatty-workflow).

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

`HumanApprovalExecutor<T>` puts the decision in the graph as a node rather than as configuration on a
node that also does work. The executor is identity work; the gate is what pauses:

```csharp
ExecutorBinding signOff = context.Node(
    new HumanApprovalExecutor<Order>("sign-off"),
    gate => gate.Mode(ExecutionMode.RequireApproval).AssignTo("group:ops"));
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

A non-success status arrives as a typed `ApiCallFailureException` carrying status, body excerpt and
`Retry-After`, so [`Classify`](#a14-custom-failure-classification) can act on it rather than parsing
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
[LLM telemetry](wiki.md#llm-telemetry).

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
[Local by default, global by declaration](wiki.md#local-by-default-global-by-declaration).

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
[Turning SSE off for a workflow](wiki.md#turning-sse-off-for-a-workflow).

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
[Workflow audit records](wiki.md#workflow-audit-records).

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
configurable. Bind a workflow as an executor to compose graph shape; use an event trigger
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
See [Registering workflows and middleware](wiki.md#registering-workflows-and-middleware).

---

## Appendix B — options reference

Defaults are what you get by omitting the property.

### `ApiCallOptions`

| Property | Default | Notes |
| --- | --- | --- |
| `Method` | `GET` | |
| `UrlTemplate` | required | `{{ }}` placeholders resolved against the input |
| `Headers` | empty | Values are templated too |
| `BodyTemplate` | none | Sent as `application/json` |
| `TimeoutSeconds` | 30 | |
| `SuccessCodes` | 200, 201, 202, 204 | Anything else raises `ApiCallFailureException` |
| `ResponseAs` | none | Deserializes `Body`; `RawBody` is always the text |
| `AllowedHosts` | empty | Checked before the request |
| `EnforceEgress` | true | |
| `SendIdempotencyKey` | true | `{instance}:{executor}:{attempt}` |
| `HttpClientName` | `abacus.run.apicall` | Const, for `IHttpClientFactory` |

### `LlmOptions`

| Property | Default | Notes |
| --- | --- | --- |
| `Model` | required | Passed to the client resolver and as `ModelId` |
| `SystemPrompt` | none | |
| `UserTemplate` | required | Templated |
| `PromptVersion` | none | Tags the drift baseline |
| `StructuredOutput` | none | Enforced; failure is `StructuredOutputException` |
| `Temperature`, `MaxTokens` | provider defaults | |
| `StreamDeltas` | false | `llm.delta`, live only |
| `MaxReparseAttempts` | 2 | |
| `EmitCompletion` | true | One `llm.completed` per call |

### `ApprovalGate`

| Property | Default |
| --- | --- |
| `Mode` | `Autonomous` (`RequireApproval` when built by the builder) |
| `Predicate` | none — never serialized |
| `Reason` | none |
| `Assignees` | empty |
| `RequiredApprovers` | 1 |
| `Expiry` | 24 hours |
| `OnExpiry` | `DeadStop` |
| `EscalationAssignees` | empty |
| `AllowModification`, `RequireSegregationOfDuties`, `Locked` | false |

### `NotificationPolicy`

| Property | Default |
| --- | --- |
| `Level` | `Standard` |
| `StreamEvents` | true |
| `ByNode` | empty |
| `Emits` | empty |

### `DomainEventTrigger`

| Property | Default | Notes |
| --- | --- | --- |
| `TopicFilter` | required | `*` one segment, `#` remainder |
| `CorrelationKey` | none | Only messages carrying this key start a run |
| `WorkflowVersion` | latest | Pin to a version |
| `ContextSelector` | payload as-is | Remap the message into the context |

### Host options a definition feels

Configured under `WorkflowHost`, not by the definition, but they decide how a workflow behaves under
failure and load.

| Setting | Default |
| --- | --- |
| `Retry:MaxAttempts` | 5 |
| `Retry:BackoffBaseSeconds` / `BackoffCapSeconds` | 2 / 300 |
| `Retry:Jitter` | `Full` |
| `Retry:MaxLifetimeHours` | 24 |
| `Lease:DurationSeconds` / `RenewalSeconds` | 60 / 20 |
| `Checkpoint:Cadence` | `SuperStep` |
| `Checkpoint:InlineThresholdBytes` | 262144 — larger checkpoints overflow to blob storage |
| `Approvals:DefaultExpiryHours` | 24 |
| `Egress:Enforce` | true |
| `MaxConcurrentInstances` | 100 |

---

## Related

- [`wiki.md`](wiki.md) — orientation, operations, HTTP API, configuration
- [`dsl-authoring-guide.md`](dsl-authoring-guide.md) — the same runtime, authored as JSON
- [`ExampleOrderWorkflow.cs`](../src/Abacus.Run.Service/Workflows/ExampleOrder/ExampleOrderWorkflow.cs) — the shipped worked example
- [`PRD-Abacus-Run.md`](PRD-Abacus-Run.md), [`TDD-Abacus-Run.md`](TDD-Abacus-Run.md) — requirements and design

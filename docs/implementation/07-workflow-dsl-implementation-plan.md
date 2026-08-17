# Workflow DSL — implementation plan

Realizes [06-workflow-dsl-design.md](06-workflow-dsl-design.md). Nothing here changes how a compiled
workflow behaves; the DSL is a second front end onto the runtime that already exists.

**Status: not started — awaiting design review.**

| # | Phase | Delivers | Depends on | Status |
| - | ----- | -------- | ---------- | ------ |
| 1 | Envelope and expression core | `DslMessage`, AbEx parser and evaluator | — | ⬜ Not started |
| 2 | Document model and validation | Parser, JSON Schema, semantic validator, diagnostics | 1 | ⬜ Not started |
| 3 | Interpreter | `DslWorkflowDefinition`, node factories, graph construction | 1, 2 | ⬜ Not started |
| 4 | Host integration | Registration, `IContextValidatingWorkflow`, catalog and validate endpoints | 3 | ⬜ Not started |
| 5 | Documentation and worked example | Wiki chapter, README, a shipped example document | 4 | ⬜ Not started |
| 6 | Deferred | Runtime publication API, sub-workflows, iteration | 5 | ⬜ Out of scope |

Phases 1–2 are independently testable with no host involved and carry most of the risk. Phase 3 is
mechanical once they land. Phase 4 is small — deliberately, because the design keeps the core change
to a single opt-in interface.

---

## Project layout

A new project, `src/Abacus.Run.Dsl`, referencing `Abacus.Run` and referenced by the host.

Keeping it out of `Abacus.Run` is not tidiness. The DSL pulls in a JSON Schema validator and an
expression parser; a host that authors every workflow in C# should not carry either. The existing
architecture boundary tests enforce the layering, and this project sits at the same level as the
adapter projects: `Abstractions ← Core ← {Executors, …} ← Api`, with `Dsl` depending on the public
surface only.

```
src/Abacus.Run.Dsl/
  Model/          DslDocument, DslNode, DslEdge, …   (the parsed document)
  Expressions/    AbExLexer, AbExParser, AbExNode, AbExEvaluator, AbExValidator
  Validation/     DslSchemaValidator, DslSemanticValidator, DslDiagnostic
  Interpretation/ DslWorkflowDefinition, DslMessage, node factories
  Hosting/        AddDslWorkflow, AddDslNode, IDslNodeFactory
  Schema/         abacus-workflow-dsl-1.0.json  (embedded resource)
```

The schema is authored at [docs/schema/abacus-workflow-dsl-1.0.json](../schema/abacus-workflow-dsl-1.0.json)
and embedded from there — one copy, so the published schema and the enforced one cannot drift. A test
asserts the embedded resource is byte-identical to the file.

---

## Phase 1 — Envelope and expression core

No host, no document, no DI. Pure data and a parser, which is what makes this phase cheap to test
exhaustively and worth doing first.

### 1.1 `DslMessage`

```csharp
public sealed class DslMessage
{
    public JsonObject Ctx { get; }      // frozen at start, copied through unchanged
    public JsonNode? Data { get; }      // the current value
    public DslMeta Meta { get; }        // node id, superstep, attempt

    public DslMessage WithData(JsonNode? data);
    public static DslMessage Start(JsonElement context);
}
```

Immutable, so a message captured by a checkpoint cannot be mutated by a later node. `Ctx` is cloned
once at start and never again — the copy-through is a reference copy, which is what keeps a large
context from being duplicated per node.

### 1.2 AbEx

Hand-written recursive-descent lexer and parser producing an immutable AST. No parser generator: the
grammar is a page long, and a hand-written parser is what gives the precise column positions the
diagnostics in Phase 2 depend on.

- `AbExParser.Parse(string) → AbExResult` — AST or a diagnostic with an offset. Never throws on bad
  input; a malformed expression is data.
- `AbExEvaluator.Evaluate(AbExNode, DslMessage, RunMetadata) → AbExValue` — total. Absence is a
  value, never an exception.
- `AbExValidator.Analyse(AbExNode) → ExpressionFacts` — unknown functions, arity errors, depth, and
  **whether the expression is deterministic**. The determinism flag is what Phase 2's positional rule
  reads.

`AbExValue` is a small struct union over the JSON types plus *absent*. Arithmetic on numbers is
`decimal`.

**Tests (~120).** Precedence and associativity for every operator. Absence propagation through each
function and operator. Strict boolean coercion — `0`, `""`, `null` and absent are all false. Ordinal
string comparison. Decimal arithmetic including `0.1 + 0.2`. Division by zero → absent. Depth limit.
`matches` timeout. Every parse error carries the right offset. Round-trip: parse → print → parse.

### 1.3 Template integration

`TemplateEngine` currently resolves dotted paths through `TemplateBindings`. Extend it with an AbEx
binding source rather than replacing it — `{{ $ctx.orderId }}` and `{{ $.total * 1.2 }}` both work,
and the existing `{{ context.Field }}` form continues to resolve unchanged so no compiled workflow
using a template breaks.

**Tests (~25).** New forms, old forms, mixed, unterminated placeholder, absent → empty string.

---

## Phase 2 — Document model and validation

### 2.1 Model

Records mirroring the schema: `DslDocument`, `DslNode` (a discriminated hierarchy by `kind`),
`DslEdge`, `DslGate`, `DslTrigger`, `DslNotifications`, `DslFailureRule`, `DslAudit`, `DslLimits`.

Parsing is `System.Text.Json` with a custom converter on `DslNode` reading `kind` first. The parser
records a **JSON Pointer for every node it builds** — the diagnostics are only as good as the
positions, and positions retrofitted into a validator are far more expensive than positions built in.

### 2.2 Schema validation

Draft 2020-12 via `JsonSchema.Net`. Structural errors map to `DslDiagnostic` with the pointer the
validator reports.

The published schema is already exercised: it checks as a legal Draft 2020-12 document, accepts the
design's worked example, and rejects 17 hand-written malformed variants — bad `dsl` version,
uppercase node id, unknown `kind`, `transform` without `set`, `http` without `url`, `conditional`
gate without `when`, `escalate` expiry without assignees, malformed duration and principal, edge
without `to`, `when` on a barrier edge, `select` with one target, an unlisted exception name, an
unknown top-level property, a gate on a `fan-in` node, and a `custom` node without `node`. Those
cases become the Phase 2 fixtures rather than being written again from scratch.

### 2.3 Semantic validation

Everything JSON Schema cannot express, from the design's table. Each check gets a stable code:

| Code | Check |
| --- | --- |
| `DSL0101` | `dsl` major version is supported |
| `DSL0102` | Document hash matches a previously registered `(name, version)` |
| `DSL0201` | Node ids unique |
| `DSL0202` | `start` names a real node |
| `DSL0203` | Every `output` entry names a real node |
| `DSL0207` | Every edge endpoint exists (with a nearest-match suggestion) |
| `DSL0208` | No duplicate unconditional edge unless `idempotent` |
| `DSL0301` | Every node reachable from `start` |
| `DSL0302` | Every non-terminal node has an outgoing edge |
| `DSL0303` | No cycle without a `delay` or `wait-event` on it |
| `DSL0304` | Fan-in barrier sources all reach it |
| `DSL0401` | Every expression parses |
| `DSL0412` | Every function is known, with correct arity |
| `DSL0413` | No non-deterministic function in an edge condition or gate predicate |
| `DSL0414` | Expression depth within limits |
| `DSL0501` | Gate absent on non-gateable kinds |
| `DSL0502` | `conditional` mode has a `when` |
| `DSL0503` | `escalate` expiry names escalation assignees |
| `DSL0601` | `custom` node names a registered factory |
| `DSL0602` | `with` satisfies the factory's schema |
| `DSL0603` | `http` node declares allowed hosts when the host enforces egress |
| `DSL0701` | Document within size, node, and edge limits |

Codes `DSL06xx` need the host's registrations, so the validator takes an optional
`DslEnvironment` — present at registration and at `POST /v2/dsl/validate`, absent for offline
linting, which then reports those checks as skipped rather than passing. **Silently passing a check
that never ran is worse than not running it**, so the result distinguishes the two.

```csharp
public sealed record DslDiagnostic(
    string Code, DslSeverity Severity, string Pointer, string Message, string? Suggestion);

public sealed record DslValidationResult(
    bool IsValid,
    IReadOnlyList<DslDiagnostic> Diagnostics,
    IReadOnlyList<string> SkippedChecks);
```

### 2.4 Canonical hash

RFC 8785 JCS canonicalization then SHA-256. Used for the immutability rule in §8 of the design.

**Tests (~150).** One valid-document fixture per node kind. One invalid fixture per diagnostic code,
asserting **code, pointer, and severity** — a validator whose messages are untested drifts into
uselessness. Hash stability across key reordering and whitespace. Skipped-check reporting with no
environment.

---

## Phase 3 — Interpreter

### 3.1 `DslWorkflowDefinition`

```csharp
public sealed class DslWorkflowDefinition
    : IWorkflowDefinition<JsonElement, JsonElement>,
      IContextValidatingWorkflow
{
    public ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken ct);
    public FailureDisposition Classify(WorkflowFailure failure);
}
```

Conditionally implements `IAuditedWorkflowDefinition`, `IEventTriggeredWorkflow` and
`INotifyingWorkflow` when the document declares the corresponding block. The registry and runtime
already probe for these with `is`, so a definition that implements one it does not need would declare
an empty policy — hence three thin wrapper types selected at registration rather than one type that
always implements everything.

`BuildAsync` runs per attempt and must be cheap. The **parsed and validated document is cached at
registration**; a build walks the model and constructs executors, and never re-parses or re-validates.
Expression ASTs are parsed once at registration too, so a build binds already-parsed trees.

### 3.2 Node factories

`IDslNodeFactory` with a built-in implementation per `kind`, plus the registration seam:

```csharp
public interface IDslNodeFactory
{
    string Name { get; }
    JsonNode? ParameterSchema { get; }   // validated against 'with' at registration
    IHostExecutor Create(DslNodeContext context);
}
```

`DslNodeContext` carries the node model, the parsed expressions, and the `WorkflowBuildContext` — so
a factory reaches `Services` and `Audit` the same way a compiled definition does.

Each built-in factory wraps its existing executor in a `DslMessage`-shaped adapter that unwraps
`data`, invokes, and rewraps. The adapters are the only genuinely new execution code in this phase,
and each is a few lines.

### 3.3 Graph construction

Walk `edges`, mapping to `AddEdge` / `AddEdge<DslMessage>(condition)` / `AddFanOutEdge` /
`AddFanInBarrierEdge`. Conditions close over a pre-parsed AST. `WithOutputFrom` binds the `output`
nodes; `Build(validateOrphans: true)` — the semantic validator has already established reachability,
so this should never fire, and if it does that is a validator bug worth surfacing loudly.

### 3.4 Failure classification

Compile `onFailure` into a matcher chain, falling through to `DefaultFailureClassifier.Instance`.

**Tests (~110 unit, ~30 integration).** Each node kind builds and runs end to end. Conditional
routing, fan-out, fan-in, selector fan-out. A gated node parks and resumes on approval. A
`wait-event` node parks, receives, and resumes. A `delay` node checkpoints and releases its lease.
Failure rules classify. `custom` factory receives its `with`. Envelope `ctx` survives to the last
node. Strict mode rejects a shape violation as dead-stop.

---

## Phase 4 — Host integration

### 4.1 The one core change

`IContextValidatingWorkflow` in `Abacus.Run/Abstractions`, consulted by
[`WorkflowRegistry.ValidateContext`](../../src/Abacus.Run/Core/WorkflowRegistry.cs) **after** the
existing type bind succeeds. Additive and opt-in: a definition that does not implement it behaves
exactly as today.

### 4.2 Registration

```csharp
builder.Services.AddWorkflowHost(config)
    .AddDslWorkflow("workflows/order-settlement.json")
    .AddDslWorkflowsFromDirectory("workflows/", "*.workflow.json")
    .AddDslNode("score-risk", new RiskScoringNodeFactory());
```

Each registration parses, validates against the full environment, computes the hash, and registers an
`IWorkflowDefinition`. **A document that fails validation fails startup**, with every diagnostic
written to the log — the same place a bad compiled workflow fails, and for the same reason.

### 4.3 Endpoints

| Route | Purpose |
| --- | --- |
| `POST /v2/dsl/validate` | Validate a document without registering it. Returns diagnostics. What an authoring tool calls. |
| `GET /v2/dsl/schema` | The published JSON Schema, for editor completion |
| `GET /v2/dsl/nodes` | The registered node catalog with parameter schemas |
| `GET /v2/workflows/{name}` | Extended with `source: "dsl" \| "compiled"` and, for DSL, `documentHash` |

`POST /v2/dsl/validate` needs the same authorization as the catalog routes. It reflects the
environment's registered node names back to the caller, which is information about the host — not
secret, but not anonymous either.

**Tests (~40 integration).** Startup fails on an invalid document, with diagnostics logged. Startup
fails on a hash conflict for an existing `(name, version)`. A DSL workflow appears in the catalog and
starts through the normal route. Context schema violations return 400 with field errors. The validate
endpoint returns pointer-accurate diagnostics. Schema endpoint matches the file on disk.

---

## Phase 5 — Documentation and example

- A new wiki chapter, **Authoring with the DSL**, placed beside *Authoring a workflow*, with the same
  structure — document shape, node reference, expression reference, validation, limits — and a
  parallel appendix mapping each existing A.1–A.10 variation to its DSL equivalent. The compiled and
  DSL paths should be legible side by side, because the honest reason to pick one over the other is
  what a reader most needs.
- README: a short section, and the DSL named in the feature list.
- `src/Abacus.Run.Service/Workflows/ExampleOrder/example-order.workflow.json` — the existing
  `ExampleOrderWorkflow` expressed as a document, registered alongside the compiled one under a
  different name. A test asserts both produce the same result for the same context, which is the
  clearest possible statement that the DSL is a front end and not a fork.
- The design doc's §11 boundaries restated in the wiki, so a reader hits the limits in the docs rather
  than in an error message.

---

## Phase 6 — Deferred, and why

Named rather than silently omitted; each is a design of its own.

**Runtime publication API.** `IWorkflowRegistry` is immutable and built once at startup, and version
resolution, dispatch, the catalog and tenant gate policy all lean on that. Making it mutable also
raises authorization (who may publish?), tenancy (definitions are global while instances are
tenant-scoped), and in-flight instance migration. The file-based path already delivers the core win —
a workflow changes without a code change — so this is a genuine next step, not a missing piece.

**Sub-workflows.** The engine supports `workflow.BindAsExecutor(id)`. Composing documents needs
resolution, version pinning, and cycle detection across documents.

**Iteration.** Unbounded loops in a checkpointed engine have real semantics to establish — the
checkpoint's size, the superstep count, and what a retry means mid-iteration. Fan-out over an array
covers the common case in v1.

---

## Risk

| Risk | Mitigation |
| --- | --- |
| The expression language grows into a scripting host | The function set is closed and small; extension goes through `custom` nodes, not new syntax. Adding a function is a deliberate change to a documented list. |
| Diagnostics are unhelpful and the DSL is abandoned | Pointer accuracy is a tested requirement from Phase 2, not a polish item. Every diagnostic code has a test asserting its pointer. |
| The envelope's `ctx` inflates checkpoints | `Ctx` is a reference copy, cloned once at start. Measured in Phase 3 with a large-context fixture. |
| Redaction gaps — more data is in flight per message | DSL nodes traverse the same middleware pipeline. An integration test asserts a redacted field in `ctx` stays redacted at the last node. |
| Schema drift between published and embedded | One file, embedded from `docs/schema/`; a test asserts byte equality. |
| The DSL looks like it can do anything and cannot | §11 of the design and the wiki chapter both state the boundary. `custom` is presented as the answer, not as an escape hatch. |

---

## Verification summary

| Suite | Added | Covers |
| --- | --- | --- |
| Unit | ~405 | AbEx, model, validation, interpreter, factories |
| Integration | ~70 | Startup, catalog, endpoints, end-to-end runs, redaction, parity with the compiled example |
| Architecture | 3 | `Abacus.Run.Dsl` depends only on the public surface; no host reference; schema resource matches the file |

`dotnet build Abacus.Run.slnx` then `dotnet test`, with the existing suites unchanged — the
`IContextValidatingWorkflow` hook is the only core edit, and nothing implements it today.

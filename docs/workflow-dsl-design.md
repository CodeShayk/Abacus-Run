# Workflow DSL — design narrative

Abacus has one way to author a workflow: implement `IWorkflowDefinition<TContext, TResult>` in C#,
compile it, and register it at startup. That path is expressive, type-safe, and closed to anyone who
cannot ship a build.

This adds a second path. A workflow becomes a **JSON document** — validated against a published
schema, interpreted at build time, and registered exactly like a compiled one. Nothing about the
runtime changes. The DSL is a *front end* onto the same graph, the same executors, the same gates.

---

## 1. The governing rule

> **The DSL composes; it never computes.**
>
> A document declares *which* nodes exist, *how* they connect, and *when* an edge is taken. It never
> carries behaviour. Every unit of work a DSL workflow performs is a capability the host already
> shipped and vetted — a built-in executor, or a custom node the host registered by name.

Everything below follows from that sentence. It is what makes a document safe to accept from outside
the build, cheap to validate, and honest about its ceiling: a DSL workflow can only do what the host
already knows how to do, and the answer to "the DSL can't express this" is *register a node*, never
*embed a script*.

The corollary matters as much: **the DSL is not a replacement for the compiled path.** They are peers
with different centres of gravity.

| | Compiled definition | DSL document |
| --- | --- | --- |
| **Authored by** | An engineer with a build pipeline | Anyone with the schema |
| **Expresses** | Arbitrary behaviour | Composition of registered behaviour |
| **Typing** | Compile-time, generic | Runtime, JSON Schema per node |
| **Changed by** | A release | An edited document |
| **Ceiling** | The language | The registered node catalog |
| **Best for** | Domain logic, novel executors | Orchestration, per-tenant variation, rapid iteration |

A realistic system uses both: engineers ship nodes, and workflows wire them together.

---

## 2. What has to be true for JSON to describe this graph

The compiled API is generic and delegate-shaped. Four features of it do not survive contact with a
document, and each forces a decision.

**Generic executors.** `HostExecutor<TIn, TOut>` is parameterised, and JSON carries no type
arguments. → *Decision D1: one envelope type.*

**Delegates everywhere.** Edge conditions, gate predicates, transforms and correlation keys are all
`Func<...>`. → *Decision D2: a closed expression language.*

**Ambient C# scope.** A compiled node closes over whatever it likes. A document has no scope.
→ *Decision D3: the envelope carries the run's context explicitly.*

**Open-ended work.** `DelegateExecutor` accepts any lambda. A document must not.
→ *Decision D4: a named node catalog with a registration seam.*

---

## 3. D1 — One envelope, uniformly typed

Every DSL node is a `HostExecutor<DslMessage, DslMessage>`. `DslMessage` is a sealed class wrapping a
JSON object:

```json
{
  "ctx":  { "orderId": "ORD-1", "lines": [ … ] },
  "data": { "total": 429.50 },
  "meta": { "node": "price", "superstep": 3 }
}
```

- **`ctx`** — the start context, deep-frozen. Copied through every node unchanged. This is D3: it
  restores the ambient scope a document otherwise lacks, and it is the only reason an expression
  eleven nodes deep can still say `$ctx.orderId`.
- **`data`** — the current value. This is what a node reads and what it replaces.
- **`meta`** — provenance the interpreter maintains. Read-only to expressions.

Three things fall out of this, and they are the whole argument for it:

1. **Every edge type-checks by construction.** There is no type-flow analysis to write, because there
   is only one type. The engine's own `AddEdge<T>` conditions are always `AddEdge<DslMessage>`.
2. **Checkpoint and resume are free.** The envelope is already JSON; there is no serializer to teach
   about a DSL workflow's message types.
3. **`TOut : class` is satisfied**, so the null-return park path — the mechanism behind approval
   gates and event waits — works for DSL nodes with no change to `HostExecutor`.

What it costs is compile-time type safety, and the replacement is explicit: a node may declare
`input` and `output` JSON Schemas, enforced at runtime under `"strict": true`. A schema violation
throws `DslContractException`, which classifies as **dead-stop** — a node handed the wrong shape will
be handed it again on retry.

### The definition's own generic parameters

`DslWorkflowDefinition` implements `IWorkflowDefinition<JsonElement, JsonElement>`. That makes the
registry's type-bind step a no-op, which is correct but insufficient — a DSL document declares a
`context` schema and the registry must honour it.

This is **the one core change the DSL requires**: an opt-in interface consulted by
`WorkflowRegistry.ValidateContext` after the type bind succeeds.

```csharp
public interface IContextValidatingWorkflow
{
    ContextValidationResult ValidateContext(JsonElement context);
}
```

Additive, opt-in, and useful beyond the DSL — a compiled workflow wanting schema validation of its
start payload gets it the same way. Nothing else in `Abacus.Run` changes to support the DSL.

---

## 4. D2 — AbEx, the expression language

Conditions, guards, correlation keys and projections all need *some* computation. The requirement is
narrow and the risk is not, so the grammar is closed.

### Design constraints

An expression must be **total** (no exceptions — a missing path is a value, not a fault), **pure**
(no I/O, no state), **cheap** (bounded depth, no loops, no recursion), and **statically checkable**
(every function and operator resolved at validation time, so a typo fails a document review rather
than a production run).

### Grammar

```
expr    := or
or      := and ( "||" and )*
and     := unary ( "&&" unary )*
unary   := "!" unary | cmp
cmp     := add ( ("=="|"!="|"<"|"<="|">"|">=") add )?
add     := mul ( ("+"|"-") mul )*
mul     := primary ( ("*"|"/"|"%") primary )*
primary := literal | path | call | "(" expr ")"
call    := ident "(" [ expr ("," expr)* ] ")"
path    := root ( "." ident | "[" integer "]" )*
root    := "$" | "$ctx" | "$run" | ident
literal := number | string | "true" | "false" | "null"
```

### Roots

| Root | Binds to | Notes |
| --- | --- | --- |
| `$` | `data` of the current envelope | `$.total`, `$.lines[0].sku` |
| `$ctx` | the frozen start context | available at every node |
| `$run` | `instanceId`, `tenantId`, `attempt`, `superstep`, `workflow`, `version`, `now` | |

There is deliberately **no `$node.<id>`**. The engine is message-passing; a prior node's output is
not ambiently available, and a root that pretended otherwise would be a lie the interpreter could not
keep. A workflow that needs an earlier value carries it forward in `data` — which is what an explicit
`transform` node is for.

### Functions

A closed set. An unknown name is a **validation error**, not a runtime one.

| Function | Result |
| --- | --- |
| `len(x)` | length of a string or array; `0` for `null` |
| `has(path)` | whether the path resolves to anything other than absent |
| `lower(s)` / `upper(s)` | case folding, invariant culture |
| `contains(s, sub)`, `startsWith(s, p)`, `endsWith(s, p)` | ordinal string tests |
| `matches(s, pattern)` | regex, compiled once, **200 ms match timeout**, non-backtracking where the pattern allows |
| `coalesce(a, b, …)` | first non-absent, non-null argument |
| `number(x)`, `string(x)`, `bool(x)` | explicit coercion |

`matches` is the one dangerous entry; the timeout and the compile-time pattern check are what earn
its place. If a future review disagrees, it is the removable one.

### Semantics, stated so there is nothing to guess

- **Absence is a value.** A path that does not resolve yields *absent*. Absent propagates through
  comparisons as `false`, through `has()` as `false`, through `coalesce()` as skipped. It never
  throws.
- **Conditions are strict.** An expression used as a condition must evaluate to boolean `true` to be
  taken. Absent, `null`, `0`, and `""` are all **false**, and a non-boolean is a *validation* error
  where the type is statically knowable. There is no JavaScript truthiness here; the surprise is not
  worth the keystrokes.
- **Comparison is JSON-typed.** Number-to-number is numeric; string-to-string is ordinal; anything
  cross-type is `false` for ordering operators and `false` for `==`. No coercion ladder.
- **Arithmetic is decimal.** These documents price orders. Binary floating point is the wrong default
  and `0.1 + 0.2` is the wrong first impression. Division by zero yields absent.

### Determinism where routing depends on it

`BuildAsync` runs **once per attempt**, and a resumed instance must retrace the routing its
checkpoint recorded. So:

> `$run.now` and any future non-deterministic function are **forbidden in edge conditions and gate
> predicates**, and permitted in templates and projections.

A non-deterministic condition would let a resumed run take a different branch than the one it
checkpointed — silent, intermittent, and close to undebuggable. The validator rejects it by static
inspection rather than trusting the author to remember.

---

## 5. D4 — The node catalog

`kind` is the discriminator. Every value maps to an executor the host already ships:

| `kind` | Executor | Notes |
| --- | --- | --- |
| `transform` | `TransformExecutor` | `set` map of target path → AbEx expression |
| `http` | `ApiCallExecutor` | egress allow-list required |
| `llm` | `LlmExecutor` | model, prompts, structured output, cost |
| `delay` | `DelayExecutor` | durable — checkpoints and halts |
| `approval` | `HumanApprovalExecutor` | approval as an explicit node |
| `publish` | `PublishEventExecutor` | domain event out |
| `wait-event` | `WaitForEventExecutor` | parks until a message matches |
| `fan-in` | `FanInExecutor` | aggregates a barrier's inputs |
| `custom` | a registered `IDslNodeFactory` | **the extension seam** |

There is **no `delegate` kind**, and there never will be. Arbitrary code is precisely what a document
must not carry.

### `custom` is the whole scalability story

```csharp
services.AddDslNode("score-risk", new RiskScoringNodeFactory());
```

```json
{ "id": "score", "kind": "custom", "node": "score-risk",
  "with": { "model": "v3", "threshold": 0.82 } }
```

The factory receives the `with` object (validated against a schema the factory itself publishes) and
returns a `HostExecutor<DslMessage, DslMessage>`. An unregistered `node` name is a **startup**
failure, not a run-time one.

This is what keeps the DSL from having a ceiling: the answer to "the DSL cannot express this" is
always *ship a node and name it*, never *embed a script*. Engineers extend the vocabulary; authors
compose it.

---

## 6. Document shape

```json
{
  "dsl": "abacus.workflow/1.0",
  "name": "order-settlement",
  "version": "1.2.0",
  "description": "Prices an order, escalates large ones, settles.",

  "context": { "type": "object", "required": ["orderId"], "properties": { … } },
  "result":  { "$ref": "#/$defs/Settlement" },

  "start": "validate",
  "output": ["complete"],

  "nodes": [
    { "id": "validate", "kind": "transform",
      "set": { "total": "$ctx.lines[0].unitPrice * $ctx.lines[0].quantity" } },

    { "id": "settle", "kind": "http",
      "method": "POST",
      "url": "https://ledger.internal/v1/settlements",
      "allowedHosts": ["ledger.internal"],
      "body": "{\"order\":\"{{ $ctx.orderId }}\",\"amount\":{{ $.total }}}",
      "gate": {
        "mode": "conditional",
        "when": "$.total > 25000",
        "reason": "RegulatedSettlement",
        "assignTo": ["group:finance", "user:cfo"],
        "requireApprovers": 2,
        "expiresAfter": "PT8H",
        "onExpiry": { "action": "escalate", "assignTo": ["group:exec"] },
        "allowModification": true,
        "requireSegregationOfDuties": true,
        "locked": true
      } },

    { "id": "complete", "kind": "transform", "set": { "status": "'settled'" } }
  ],

  "edges": [
    { "from": "validate", "to": "settle", "when": "$.total > 0" },
    { "from": "settle",   "to": "complete" }
  ],

  "triggers":      [ { "topic": "orders.placed", "correlationKey": "$.orderId" } ],
  "notifications": { "level": "standard", "stream": true, "emits": ["priced"] },
  "onFailure":     [ { "match": { "exception": "ApiCallFailureException", "status": "5xx" },
                       "disposition": "retry" } ],
  "audit":         { "sections": ["submission", "outcome"] },
  "limits":        { "maxAttempts": 5 }
}
```

Two things about this shape are load-bearing.

**`dsl` is a versioned media identifier, not decoration.** `abacus.workflow/1.0` selects the schema
and the interpreter. A future `1.1` adds optional fields and stays readable by a `1.0` interpreter; a
`2.0` does not, and the interpreter refuses it by major version rather than failing on a field it
does not recognise.

**Templates and expressions are different surfaces.** `{{ … }}` inside a string is the existing
`TemplateEngine`, extended to evaluate AbEx and to render `ctx`/`data` roots. A bare string in
`when`, `set` or `correlationKey` is AbEx directly. Mixing the two conventions in one field would be
ambiguous, so no field accepts both.

---

## 7. Validation is two phases, because one is not enough

**Phase 1 — JSON Schema (Draft 2020-12).** Validates *shape*: required properties, `kind`-
discriminated variants via `if`/`then`, id patterns (`^[a-z][a-z0-9-]{0,63}$`), SemVer, ISO-8601
durations, topic patterns, enum values. Published at `docs/schema/abacus-workflow-dsl-1.0.json` so an
editor gives completion and inline errors before the document reaches the host.

**Phase 2 — the semantic validator.** JSON Schema cannot express any of this, and every item is a
real way to write a structurally valid document that is nonsense:

| Check | Why it is not a schema concern |
| --- | --- |
| Node ids unique | Schema cannot compare array items |
| Every edge endpoint exists | Cross-reference |
| `start` and every `output` name a real node | Cross-reference |
| No unreachable node | Graph traversal |
| No cycle without a `delay` or `wait-event` on it | Graph traversal; a tight loop is a hot spin |
| Every AbEx expression parses, with known functions | Sub-language |
| No non-deterministic function in a condition or predicate | Sub-language + position |
| Gate absent on non-gateable kinds | Cross-field |
| `custom` node names a registered factory | Environment |
| `with` matches the factory's schema | Environment |
| Egress hosts present when the host enforces them | Environment |
| Within `limits` — nodes, edges, expression depth, bytes | Policy |

Every diagnostic carries a **JSON Pointer**, a stable code, and a severity:

```
DSL0412  error  /nodes/3/gate/when   Unknown function 'lookupCustomer'.
DSL0207  error  /edges/5/to          Edge targets 'setle', which is not a node. Did you mean 'settle'?
DSL0631  warn   /nodes/7             Node 'notify' is unreachable from 'start'.
```

The pointer is not a nicety. A DSL without precise error locations is a DSL people abandon after the
third unhelpful failure, and retrofitting positions into a validator is far harder than building with
them.

Validation runs at **registration**, so a bad document fails startup — the same place a bad compiled
workflow fails. It also runs on demand at `POST /v2/dsl/validate`, which is what an authoring tool
calls and what makes the DSL usable without a deploy cycle.

---

## 8. Identity, immutability, and drift

A DSL workflow registers as `(name, version)` exactly like a compiled one, and inherits the
framework's existing rule: **a published version is immutable.** Instances pin their version, and a
document edited under a version its instances are running would rewrite history mid-flight.

Enforcement is a content hash. The interpreter computes `sha256` over the document's canonical form
(RFC 8785 JCS), records it on the descriptor, and stamps it on every instance. Registering a document
whose `(name, version)` is already known with a different hash is a **startup failure** naming both
hashes. Editing a workflow means bumping the version — which is what the compiled path already
demands, stated in a way a document author will actually encounter.

The hash also answers the operational question directly: *is this instance running the document I am
looking at?*

---

## 9. Where documents come from

**In scope for v1:** files on disk and embedded resources, registered at startup.

```csharp
builder.Services.AddWorkflowHost(config)
    .AddDslWorkflow("workflows/order-settlement.json")
    .AddDslWorkflowsFromDirectory("workflows/", searchPattern: "*.workflow.json")
    .AddDslNode("score-risk", new RiskScoringNodeFactory());
```

**Deliberately out of scope for v1:** a management API that accepts documents at run time.

That is not caution for its own sake. Today `IWorkflowRegistry` is immutable, built once at startup,
and *everything* leans on it — version resolution, dispatch, the catalog API, tenant gate policy.
Making it mutable is a genuine piece of work touching all of those, plus authorization (who may
publish a workflow?), tenancy (whose workflow is it — and definitions are global while instances are
tenant-scoped), and the migration of in-flight instances. It deserves its own design, not a paragraph
at the end of this one. Phase 6 of the plan names it.

The file-based path still delivers the actual win: a workflow changes without a code change, and the
document is reviewable in a pull request.

---

## 10. Safety

A DSL is an untrusted-input surface the moment it is authored by anyone who is not the person who
built the host. v1 loads from disk, but the design assumes it will not stay that way.

- **No code.** No delegate kind, no scripting, no reflection by name into arbitrary types. Only
  registered factories.
- **Bounded evaluation.** Expression depth ≤ 32, no loops or recursion in the grammar, regex match
  timeout 200 ms, document ≤ 1 MB, nodes ≤ 500, edges ≤ 2000. All configurable down, none up.
- **Egress unchanged.** `http` nodes go through the same `EgressGuard`. A document cannot widen an
  allow-list the host has fixed.
- **Redaction unchanged.** Envelopes traverse the same middleware pipeline, so the same redaction
  applies. `ctx` travelling in every message is exactly why this matters — the design deliberately
  puts more data in flight, and it must not put more data in logs.
- **Gates cannot be weakened.** `locked` behaves as it does for compiled workflows: tenants may
  tighten, never loosen.
- **Failure classification is a whitelist.** `onFailure` matches named framework exceptions and
  status ranges; it cannot name arbitrary types.

---

## 11. What this does not attempt

Stated plainly so review can disagree with the boundary rather than discover it:

- **Loops and iteration.** No `foreach`. Fan-out over an array is the intended shape, and unbounded
  iteration in a checkpointed engine has real semantics to work out. Deferred, not forgotten.
- **Sub-workflows.** The engine supports `workflow.BindAsExecutor(id)`. Composing DSL documents needs
  a resolution and versioning story of its own. Phase 6.
- **A surface syntax.** JSON is the interchange format. A YAML front end or a visual editor sits
  *above* this and produces these documents; neither belongs in the interpreter.
- **Round-tripping compiled workflows.** A compiled definition cannot be exported as a document. The
  DSL is not a serialization of C#; it is a different way in.

---

## 12. Summary

One envelope type makes the graph uniformly typed. One closed expression language makes conditions
expressible without making them dangerous. One named catalog with a registration seam makes the DSL
extensible without making it a scripting host. Two-phase validation with pointer-accurate diagnostics
makes it usable. A content hash makes it honest about versions.

Everything else is the runtime that already exists.

# Authoring workflows with the Abacus DSL

A complete reference for building a workflow definition as a JSON document, covering every framework
capability and how — or whether — a document reaches it.

Mirror of [Authoring workflows in C#](workflow-authoring-guide.md): the same runtime, the same
catalog, the same gates and events — reached from JSON instead of code. The
[wiki](wiki.md#two-ways-to-author-a-workflow) introduces both and is the operational manual behind
them. The schema is at
[`docs/schema/abacus-workflow-dsl-1.0.json`](schema/abacus-workflow-dsl-1.0.json) and served live
from `GET /dsl/schema`.

---

## Contents

- [1. The model](#1-the-model)
- [2. The envelope](#2-the-envelope)
- [3. Document anatomy](#3-document-anatomy)
- [4. AbEx — the expression language](#4-abex--the-expression-language)
- [5. Templates](#5-templates)
- [6. Node kinds](#6-node-kinds)
- [7. Edges](#7-edges)
- [8. Approval gates](#8-approval-gates)
- [9. Notifications and events](#9-notifications-and-events)
- [10. Domain events: publishing, waiting, triggering](#10-domain-events-publishing-waiting-triggering)
- [11. Failure, retry and limits](#11-failure-retry-and-limits)
- [12. Custom nodes](#12-custom-nodes)
- [13. Registration and hosting](#13-registration-and-hosting)
- [14. Validation and diagnostics](#14-validation-and-diagnostics)
- [15. Versions, identity and drift](#15-versions-identity-and-drift)
- [16. What a document inherits for free](#16-what-a-document-inherits-for-free)
- [17. Framework coverage map](#17-framework-coverage-map)
- [18. Declared but not yet enforced](#18-declared-but-not-yet-enforced)
- [19. Not expressible](#19-not-expressible)
- [Appendix A — worked variations](#appendix-a--worked-variations)
- [Appendix B — full field reference](#appendix-b--full-field-reference)

---

## 1. The model

> **The governing rule: the DSL composes, it never computes.**
>
> A document declares *which* nodes exist, *how* they connect, and *when* an edge is taken. It never
> carries behaviour. Every unit of work is a capability the host already shipped — a built-in node
> kind, or a custom node registered by name.

Three consequences follow, and they explain most of the design:

1. **There is no `delegate` kind and never will be.** Arbitrary code is precisely what a document
   must not carry. When the DSL cannot express something, the answer is *register a node*.
2. **A document's ceiling is the host's node catalog**, not the JSON syntax. Extending the DSL is an
   engineering task (ship a factory), not an authoring one.
3. **A document is safe to accept from outside the build.** It cannot execute, reach the filesystem,
   open a socket the host has not allow-listed, or loop unboundedly.

A DSL document registers as an ordinary `IWorkflowDefinition`. It appears in the same catalog, starts
through the same route, checkpoints through the same store, and is controlled by the same endpoints
as a compiled workflow. Nothing downstream of registration knows the difference.

---

## 2. The envelope

Every DSL node sends and receives one message type. That is what makes every edge type-check by
construction, and what lets a checkpoint serialize without a bespoke converter.

```json
{
  "ctx":  { "orderId": "ORD-1", "amount": 100 },
  "data": { "net": 100, "vat": 20, "total": 120 },
  "meta": { "node": "price", "superstep": 2, "attempt": 1 }
}
```

| Part | What it is |
| --- | --- |
| `ctx` | The **start context**, frozen. Copied through every node unchanged, so an expression at any depth can read it. A compiled node closes over C# scope; a document has none, so the envelope carries one. |
| `data` | The **current value**. What a node reads, and what it replaces. |
| `meta` | Provenance the interpreter maintains. Read-only. |

Two nodes bracket every DSL graph and are not declared in the document:

- **`$entry`** converts the start context into the first envelope. Without it nothing would run: the
  runner sends the deserialized context typed as `JsonElement`, and the engine routes by type.
- **`$exit`** unwraps the envelope to produce the workflow result — **the result is `data`, not the
  envelope**. The context is machinery, not an answer. If `data` is not an object it is wrapped as
  `{ "value": … }` so the result shape stays predictable.

Their ids begin with `$`, which a declared node id cannot, so they can never collide.

---

## 3. Document anatomy

```json
{
  "dsl": "abacus.workflow/1.0",
  "name": "order-settlement",
  "version": "1.2.0",
  "description": "Prices an order, escalates large ones, settles.",

  "context": { "type": "object", "required": ["orderId"] },
  "start": "price",
  "output": ["settle"],

  "nodes": [ … ],
  "edges": [ … ],

  "triggers":      [ … ],
  "notifications": { … },
  "onFailure":     [ … ],
  "audit":         { … },
  "limits":        { … }
}
```

| Field | Required | Purpose |
| --- | --- | --- |
| `dsl` | ✔ | Media identifier selecting schema and interpreter. Currently `abacus.workflow/1.0`. |
| `name` | ✔ | Registry key. Lowercase kebab, `^[a-z][a-z0-9-]{0,63}$`. |
| `version` | ✔ | SemVer. Instances pin it; a published version is immutable. |
| `description` | | Shown in the catalog and used as the audit record description. |
| `context` | | JSON Schema the start payload must satisfy. Enforced on every start request. |
| `start` | ✔ | The node the run begins at. |
| `output` | | Nodes whose `data` becomes the result. Defaults to every terminal node. |
| `nodes` | ✔ | 1–500 nodes. |
| `edges` | | 0–2000 edges. |
| `triggers` | | Topics that start an instance. |
| `notifications` | | Emission level, per-node overrides, SSE on/off. |
| `onFailure` | | Failure classification rules. |
| `audit` | | Declares an audit record shape. |
| `limits` | | `maxAttempts`, `maxLifetimeHours`. |

`dsl` is versioned deliberately. A future `1.1` adds optional fields and stays readable by a `1.0`
interpreter; a `2.0` does not, and is refused by major version rather than failing on some field it
does not recognise.

### The context schema

This is the DSL's answer to a compiled workflow's `TContext`. It is a full JSON Schema, and a start
request that fails it is rejected with **400** and per-field errors before any instance row is
created:

```json
"context": {
  "type": "object",
  "required": ["orderId", "amount"],
  "properties": {
    "orderId":  { "type": "string", "minLength": 1 },
    "amount":   { "type": "number", "minimum": 0 },
    "currency": { "type": "string", "enum": ["GBP", "USD", "EUR"] }
  }
}
```

Omit it and any JSON object is accepted.

---

## 4. AbEx — the expression language

Conditions, guards, correlation keys and projections need *some* computation. The grammar is closed
on purpose: **total** (no expression over any document can throw), **pure** (no I/O, no state), and
**statically checkable** (every function resolved at validation time).

### Roots

| Root | Binds to | Example |
| --- | --- | --- |
| `$` | The current `data` | `$.total`, `$.lines[0].sku` |
| `$ctx` | The frozen start context | `$ctx.orderId` |
| `$run` | Run identity | `$run.instanceId`, `$run.tenantId`, `$run.workflow`, `$run.version`, `$run.attempt`, `$run.superstep`, `$run.now` |

Every path starts with one of these three. There is deliberately **no `$node.<id>`**: the engine is
message-passing, a prior node's output is not ambiently available, and a root that pretended
otherwise would be a lie the interpreter could not keep. Carry values forward in `data` — that is
what a `transform` node is for.

### Grammar

```
expr    := or
or      := and ( "||" and )*
and     := cmp ( "&&" cmp )*
cmp     := add ( ("=="|"!="|"<"|"<="|">"|">=") add )?     -- non-associative
add     := mul ( ("+"|"-") mul )*
mul     := unary ( ("*"|"/"|"%") unary )*
unary   := ("!"|"-") unary | primary
primary := literal | path | call | "(" expr ")"
path    := ("$"|"$ctx"|"$run") ( "." ident | "[" integer "]" )*
literal := number | 'single-quoted' | "double-quoted" | true | false | null
```

Precedence, loosest to tightest: `||`, `&&`, comparison, `+ -`, `* / %`, unary `!` `-`.

Comparison is **non-associative**: `a < b < c` is refused at validation rather than silently
comparing a boolean to a number. Write `a < b && b < c`.

String literals prefer single quotes, because a document is already inside JSON: `"$.status == 'settled'"`
needs no escaping, `"\"settled\""` does.

### Functions

The complete list. An unknown name is a **validation error** with a nearest-match suggestion, never a
runtime surprise.

| Function | Arity | Result |
| --- | --- | --- |
| `len(x)` | 1 | Characters of a string, elements of an array, properties of an object; `0` for anything else |
| `has(path)` | 1 | Whether the path resolved to anything at all. A JSON `null` counts as present |
| `lower(s)` / `upper(s)` | 1 | Case folding, invariant culture; absent for a non-string |
| `contains(s, sub)` | 2 | Ordinal substring test |
| `startsWith(s, p)` | 2 | Ordinal prefix test |
| `endsWith(s, p)` | 2 | Ordinal suffix test |
| `matches(s, pattern)` | 2 | Regex test. **The pattern must be a string literal**, and matching times out at 200 ms |
| `coalesce(a, b, …)` | 1+ | First argument that is neither absent nor null |
| `number(x)` | 1 | Number, or a parseable string; absent otherwise |
| `string(x)` | 1 | Rendered form; absent stays absent |
| `bool(x)` | 1 | Boolean, or `"true"`/`"false"`; absent otherwise |

`matches` requires a literal pattern for a reason: a pattern assembled at run time cannot be reviewed
by reading the document, and an unbounded pattern is the one genuinely dangerous construct in the
grammar. The timeout means a pathological pattern is a non-match, never a stalled dispatcher.

### Semantics

These are the rules worth learning before they surprise you.

**Absence is a value.** A path that does not resolve yields *absent*. It never throws.

**Absence makes every comparison false — including `!=`.**

```
$.missing == 1     →  false
$.missing != 1     →  false      ← not true
has($.missing)     →  false      ← this is how you ask
```

A document asking whether a field it never set differs from a value must not be told "yes".

**Conditions are strictly boolean.** Only `true` is true.

```
"when": "$.flag"        →  true only if flag is boolean true
"when": "$.total"       →  false, even for 429.50
"when": "$.name"        →  false, even for "abc"
"when": "0"             →  false
"when": "''"            →  false
```

There is no truthiness ladder to remember. Compare explicitly: `$.total > 0`, `len($.name) > 0`.

**Comparison is JSON-typed.** Number-to-number is numeric, string-to-string is ordinal, anything
cross-type is `false`. No coercion ladder — `$.count == "3"` is false.

**Arithmetic is decimal, and numbers only.**

```
0.1 + 0.2   →  0.3        ← these documents price orders
1 / 0       →  absent     ← not an error
'a' + 'b'   →  absent     ← + does not concatenate; that is what templates are for
```

**Short-circuiting works**, which makes the guard idiom cheap and safe:

```
"when": "has($.order) && $.order.total > 25000"
```

### Determinism

> `$run.now` is **forbidden in edge conditions and gate predicates**, and permitted everywhere else.

`BuildAsync` runs once per attempt, and a resumed instance must retrace the routing its checkpoint
recorded. A condition that read the clock could take a different branch on resume — silent,
intermittent, close to undebuggable. The validator refuses it statically (`DSL0413`).

Need time-based routing? Compute it once in a `transform` and compare against that:

```json
{ "id": "stamp", "kind": "transform", "set": { "startedAt": "$run.now" } },
{ "from": "stamp", "to": "expired", "when": "$.startedAt < '2026-01-01'" }
```

---

## 5. Templates

A `{{ … }}` placeholder inside a string evaluates a **full AbEx expression** and renders it as text.
Templates appear in `http` URLs, headers and bodies, and in `llm` prompts.

```json
"url":  "https://ledger.internal/v1/orders/{{ $ctx.orderId }}",
"body": "{\"amount\":{{ $.total }},\"ref\":\"{{ upper($ctx.orderId) }}\"}"
```

- An **absent** placeholder renders as empty string — a template never fails a run over a missing
  field.
- An **unterminated** placeholder is emitted verbatim rather than truncating the rest of the string.
- Numbers render without trailing zeros (`1.50` → `1.5`), booleans as `true`/`false`, objects and
  arrays as JSON.

**Templates and expressions are different surfaces.** A field is one or the other, never both:

| Convention | Fields |
| --- | --- |
| Bare AbEx expression | `when`, `select`, `set` values, `payload` values, `correlationKey`, `contextFrom`, `notify.payload` values, `audit.key` |
| `{{ }}` template | `url`, `headers`, `body`, `prompt`, `system` |

---

## 6. Node kinds

Every node shares these fields:

```json
{
  "id": "settle",
  "kind": "http",
  "description": "Posts the settlement to the ledger.",
  "gate":   { … },
  "notify": { "name": "settled", "payload": { "ref": "$.body.reference" } }
}
```

`id` is the identity everything else keys off — gate policy, node state, per-node notification
overrides, the graph endpoint. **Renaming a node in a published version orphans any tenant policy
written against the old id**; bump the version instead.

### `transform` — projection

The only node that computes, and it computes only through AbEx.

```json
{ "id": "price", "kind": "transform",
  "set": {
    "orderId": "$ctx.orderId",
    "net":     "$ctx.amount",
    "vat":     "$ctx.amount * 0.2",
    "total":   "$ctx.amount * 1.2",
    "tier":    "coalesce($ctx.tier, 'standard')"
  },
  "replace": false }
```

| Field | Default | Meaning |
| --- | --- | --- |
| `set` | required | Target path → expression. Dotted targets create intermediate objects: `"order.total"` writes `{ "order": { "total": … } }` |
| `replace` | `false` | `false` merges into existing `data`; `true` discards it first |

**Every expression reads `data` as it was *before* the transform.** The order properties happen to be
written in cannot change the result:

```json
"set": { "a": "$.a + 1", "b": "$.a + 10" }
```

With `data = { "a": 1 }` this yields `{ "a": 2, "b": 11 }` — `b` reads the old `a`, not the new one.

An expression resolving to absent writes `null`.

**Produces:** the `set` map merged into (or replacing) `data`.

### `http` — outbound call

```json
{ "id": "settle", "kind": "http",
  "method": "POST",
  "url": "https://ledger.internal/v1/settlements",
  "headers": { "X-Order": "{{ $ctx.orderId }}", "Accept": "application/json" },
  "body": "{\"order\":\"{{ $ctx.orderId }}\",\"amount\":{{ $.total }}}",
  "allowedHosts": ["ledger.internal"],
  "timeoutSeconds": 30,
  "successCodes": [200, 201, 202],
  "sendIdempotencyKey": true }
```

| Field | Default | Meaning |
| --- | --- | --- |
| `method` | `GET` | `GET`/`POST`/`PUT`/`PATCH`/`DELETE`/`HEAD` |
| `url` | required | Templated |
| `headers` | none | Values templated |
| `body` | none | Templated |
| `timeoutSeconds` | `30` | 1–600 |
| `successCodes` | `200,201,202,204` | Anything else raises `ApiCallFailureException` |
| `allowedHosts` | none | Egress allow-list. **Required** when the host enforces egress |
| `sendIdempotencyKey` | `true` | Sends `Idempotency-Key: {instance}:{node}:{attempt}` |

This is the framework's `ApiCallExecutor`, hosted inside the DSL node — the egress guard, the
idempotency key, the `Retry-After` parsing and the typed failure exception all behave exactly as they
do for a compiled workflow. Nothing is reimplemented.

**Produces:** `{ "status": 200, "body": … }`. A JSON response body is parsed so it is addressable
(`$.body.reference`); a non-JSON body lands as a string.

### `llm` — model call

```json
{ "id": "summarise", "kind": "llm",
  "model": "gpt-4o",
  "system": "You summarise orders for an operations team.",
  "prompt": "Summarise order {{ $ctx.orderId }} totalling {{ $.total }}.",
  "promptVersion": "v3",
  "temperature": 0.2,
  "maxTokens": 500,
  "streamDeltas": false,
  "emitCompletion": true }
```

| Field | Default | Meaning |
| --- | --- | --- |
| `model` | required | Resolved through `IChatClientResolver`, or a single registered `IChatClient` |
| `system` / `prompt` | `prompt` required | Templated |
| `promptVersion` | none | Travels into drift middleware and the completion event |
| `temperature`, `maxTokens` | provider default | |
| `streamDeltas` | `false` | Emits transient `llm.delta` events |
| `emitCompletion` | `true` | Emits one `llm.completed` carrying model, tokens, cost and latency |

**Produces:** `{ text, value, model, inputTokens, outputTokens, costUsd, finishReason, elapsedMs }` —
so a document can branch on cost or token count, not just on the text.

### `delay` — durable wait

```json
{ "id": "cool-off", "kind": "delay", "for": "PT4H" }
```

ISO-8601 duration. Writes a timer row rather than blocking, so a long delay costs no execution
capacity.

**Produces:** the envelope **unchanged**. A delay is about *when* the next node runs, not about what
it receives — losing the payload would make every delay need a transform after it.

> Requires an `ITimerService` registration. See [§18](#18-declared-but-not-yet-enforced).

### `approval` — human decision as a node

```json
{ "id": "sign-off", "kind": "approval" }
```

Identity work; the pause is the point. The node **always carries a gate**, whether or not the
document spells one out — an `approval` node with no `gate` block behaves as
`{ "mode": "requireApproval" }`. Add a `gate` block to configure assignees, quorum or expiry.

Use this when the decision belongs in the graph. Use a `gate` on a working node when the decision is
configuration *about* that node.

**Produces:** the envelope unchanged.

### `publish` — emit a domain event

```json
{ "id": "announce", "kind": "publish",
  "topic": "orders.settled",
  "payload": { "order": "$ctx.orderId", "total": "$.total" },
  "correlationKey": "$ctx.orderId",
  "scope": "local" }
```

| Field | Default | Meaning |
| --- | --- | --- |
| `topic` | required | Dot-segmented topic |
| `payload` | whole `data` | Explicit projection. Defaulting to `data` rather than the envelope matters — a subscriber should receive the message, not this workflow's context |
| `correlationKey` | none | Expression; lets a waiting instance match this message |
| `scope` | `local` | `distributed` requires a broker that supports it, checked at build |

**Produces:** the envelope unchanged — publishing is a side effect on the way past, so the node drops
into an existing edge without rewiring the graph.

### `wait-event` — park until a message arrives

```json
{ "id": "await-payment", "kind": "wait-event",
  "topic": "payment.settled",
  "correlationKey": "$ctx.orderId",
  "timeout": "P3D",
  "onExpiry": "deadStop" }
```

| Field | Default | Meaning |
| --- | --- | --- |
| `topic` | required | Pattern: `*` matches one segment, `#` the remainder |
| `correlationKey` | none | Receive only messages carrying this key |
| `timeout` | none | ISO-8601 |
| `onExpiry` | `deadStop` | `deadStop` terminates; `resume` continues so the graph can handle it |

Runs twice: the first pass registers a durable subscription and parks (the instance checkpoints and
releases its lease, so a three-day wait costs nothing); after delivery the runner resumes and the
second pass returns the payload.

**Produces:** `data` becomes the delivered payload. `ctx` survives the park, so `$ctx.orderId` still
resolves afterwards.

### `fan-in` — barrier aggregation

```json
{ "id": "join", "kind": "fan-in", "into": "branches" }
```

The target of a barrier edge. Holds each branch's arrival and emits once the last one lands; the
expected count is read from the barrier edge in the document.

**Produces:** `{ "<into>": [ …each branch's data… ] }`, default `into` is `"items"`.

Cannot carry a gate — a barrier aggregates work that has already happened, so pausing it would gate
nothing.

### `custom` — a registered node

```json
{ "id": "score", "kind": "custom", "node": "score-risk",
  "with": { "model": "v3", "threshold": 0.82 } }
```

See [§12](#12-custom-nodes).

---

## 7. Edges

One shape covers everything: `from` and `to`, either of which may be a list.

```json
"edges": [
  { "from": "a", "to": "b" },
  { "from": "b", "to": "large", "when": "$.total > 25000" },
  { "from": "b", "to": "small", "when": "$.total <= 25000" },
  { "from": "large", "to": ["notify-ops", "notify-customer"] },
  { "from": ["notify-ops", "notify-customer"], "to": "join" },
  { "from": "c", "to": ["one", "two", "three"], "select": "$.chosenIndices" }
]
```

| Shape | Behaviour |
| --- | --- |
| `from: "a", to: "b"` | Sequential |
| `+ "when": "<expr>"` | Traversed only when the expression is boolean `true` |
| `from: "a", to: ["b","c"]` | Fan-out to every target |
| `+ "select": "<expr>"` | Fan-out to the subset the expression names by index |
| `from: ["a","b"], to: "c"` | Fan-in barrier — `c` runs once every source has delivered |

| Field | Default | Meaning |
| --- | --- | --- |
| `when` | none | Condition. Must be deterministic. Not valid on a barrier |
| `select` | none | Fan-out only. A number picks one target; an array picks several. Out-of-range indices are ignored |
| `label` | none | Shown in the graph view |
| `idempotent` | `false` | Permits a duplicate unconditional edge |

**Branching is two conditional edges out of one node** — there is no `switch`. Make the predicates
exhaustive: a message matching neither simply stops there, and the run completes with no output.

**A duplicate unconditional edge is refused** (`DSL0208`), because which one fires is ambiguous. Two
*conditional* edges between the same pair are fine — that is exactly how a branch with a fallback is
written. Set `idempotent: true` if the repeat is genuinely intended.

**Cycles are allowed only when something on them yields.** Polling and wait-and-recheck are
legitimate, but a cycle of pure compute nodes is a hot spin that occupies a dispatcher until the
lifetime cap. Put a `delay`, `wait-event` or `approval` node on the cycle (`DSL0303`).

---

## 8. Approval gates

A gate on any node makes the run pause for a human. Every option the compiled `ApprovalGateBuilder`
offers is expressible.

```json
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
}
```

| Field | Default | Meaning |
| --- | --- | --- |
| `mode` | required | `autonomous` (no gate), `requireApproval` (always), `conditional` (when the predicate trips) |
| `when` | required for `conditional` | Deterministic AbEx predicate over the node's input |
| `reason` | none | Surfaced to approvers and on the approval event |
| `assignTo` | none | `user:`, `group:` or `role:` prefixed principals |
| `requireApprovers` | `1` | Quorum |
| `expiresAfter` | `PT24H` | ISO-8601 |
| `onExpiry.action` | `deadStop` | `deadStop`, `reject`, `autoApprove`, `escalate` |
| `onExpiry.assignTo` | | Required when the action is `escalate` |
| `allowModification` | `false` | Approver may amend the node's input |
| `requireSegregationOfDuties` | `false` | The decider may not be the initiator |
| `locked` | `false` | Tenants may tighten this gate, never loosen it |

Notes that bite:

- **A gate with `assignTo` refuses an unauthenticated decider** with 403. That is correct, and worth
  remembering when testing.
- **Every gated node is reconfigurable per tenant** unless `locked` is set. See
  [Tenant executor configuration](wiki.md#tenant-executor-configuration).
- **`fan-in` nodes cannot be gated** (`DSL0501`).
- A `conditional` gate with no `when` is refused (`DSL0502`) — it would never trip, which is the same
  as having no gate.

---

## 9. Notifications and events

### Per-node notification

```json
{ "id": "price", "kind": "transform",
  "set": { "total": "$ctx.amount * 1.2" },
  "notify": {
    "name": "priced",
    "payload": { "order": "$ctx.orderId", "total": "$.total" }
  } }
```

Emits `custom.priced` on the instance's event stream after the node succeeds, interleaved correctly
with the lifecycle events around it. The `custom.` prefix is applied by the framework and cannot be
opted out of, so a workflow can never shadow a framework event. The payload is evaluated against the
node's *output* envelope.

A parked node emits nothing — the notification fires only on a real result.

### Workflow-level policy

```json
"notifications": {
  "level": "standard",
  "stream": true,
  "byNode": { "chatty-fan-out": "minimal", "the-interesting-one": "standard" },
  "emits": ["priced", "settled"]
}
```

| Field | Default | Meaning |
| --- | --- | --- |
| `level` | `standard` | `minimal` (start, output, terminal), `lifecycle` (adds superstep boundaries), `standard` (adds per-node and workflow-defined events) |
| `stream` | `true` | Whether events reach live SSE subscribers |
| `byNode` | none | Per-node level override, in both directions |
| `emits` | none | Names advertised by the catalog. Per-node `notify` names are added automatically |

> **The durable event log is not optional and cannot be turned off.** `stream: false` switches off
> only the live fan-out; every event is still written and readable at
> `GET /v2/workflows/{name}/instances/{id}/events`. Only the *timing* of observability changes.

Approvals, control actions, broker deliveries and terminal events are never suppressed at any level —
they are facts about the system, not run chatter.

---

## 10. Domain events: publishing, waiting, triggering

Three distinct capabilities, all reachable from a document.

**Publish** — a `publish` node, [§6](#publish--emit-a-domain-event).

**Wait** — a `wait-event` node, [§6](#wait-event--park-until-a-message-arrives).

**Trigger** — a message *starts* an instance:

```json
"triggers": [
  { "topic": "orders.placed" },
  { "topic": "orders.*.amended", "contextFrom": "$.order" }
]
```

| Field | Meaning |
| --- | --- |
| `topic` | Pattern. `*` matches one segment, `#` the trailing remainder |
| `correlationKey` | **A literal filter value, not an expression.** The subscription is registered before any message exists, so there is nothing for a path to read. A key written to look like an expression is warned about |
| `contextFrom` | An expression **rooted at the message payload**, projecting it into the workflow's start context. Omit to pass the whole payload through |

`contextFrom` is a genuine expression because a message *does* exist when it runs. If it resolves to
nothing the whole payload is used, so a mistyped path degrades rather than starting an empty run.

---

## 11. Failure, retry and limits

```json
"onFailure": [
  { "match": { "exception": "ApiCallFailureException", "status": "5xx" }, "disposition": "retry" },
  { "match": { "exception": "ApiCallFailureException", "status": "4xx" }, "disposition": "deadStop" },
  { "match": { "node": "settle" }, "disposition": "escalate" }
],
"limits": { "maxAttempts": 5, "maxLifetimeHours": 72 }
```

Rules are evaluated **in declaration order**; the first match wins. Anything unmatched falls through
to the framework's default classifier, which already knows that a rate limit is worth retrying and a
validation error is not. **A document only has to state where it disagrees.**

| `match` field | Matches |
| --- | --- |
| `exception` | A framework exception by name — a fixed whitelist, so a document cannot name arbitrary types |
| `status` | An exact code (`404`) or a class (`5xx`); only meaningful for `ApiCallFailureException` |
| `node` | Scopes the rule to one node id |

Matchable exceptions: `WorkflowDeadStopException`, `ApprovalRejectedException`,
`WorkflowValidationException`, `StructuredOutputException`, `ApiCallFailureException`,
`LlmRateLimitException`, `LlmOverloadedException`, `DslContractException`.

| Disposition | Effect |
| --- | --- |
| `retry` | Backoff and try again, until `maxAttempts` or `maxLifetimeHours` |
| `deadStop` | Terminal. Retrying cannot help, so attempts are not burned discovering that |
| `escalate` | Terminal, and flagged for operator attention |

---

## 12. Custom nodes

The extension seam, and the whole reason the DSL has no ceiling.

```csharp
public sealed class RiskScoringNodeFactory : IDslNodeFactory
{
    public string Name => "score-risk";

    // Validated against 'with' at registration, so a bad parameter fails startup.
    public JsonNode? ParameterSchema => JsonNode.Parse("""
        {
          "type": "object",
          "required": ["threshold"],
          "properties": {
            "threshold": { "type": "number", "minimum": 0, "maximum": 1 },
            "model":     { "type": "string" }
          }
        }
        """);

    public IHostExecutor Create(DslNodeContext context)
        => new RiskScorer(
            context.Node.Id,
            context.Parameters["threshold"]!.GetValue<decimal>(),
            context.Require<IRiskService>());
}

internal sealed class RiskScorer(string id, decimal threshold, IRiskService risk)
    : HostExecutor<DslMessage, DslMessage>(id)
{
    protected override async ValueTask<DslMessage> ExecuteCoreAsync(
        DslMessage input, IWorkflowContext context, CancellationToken cancellationToken)
    {
        decimal score = await risk.ScoreAsync(input.Data, cancellationToken);

        var data = (JsonObject)(input.Data ?? new JsonObject()).DeepClone();
        data["score"] = score;
        data["flagged"] = score > threshold;

        return input.WithData(data);
    }
}
```

```json
{ "id": "score", "kind": "custom", "node": "score-risk",
  "with": { "threshold": 0.82, "model": "v3" } }
```

Rules enforced at build time:

- The executor **must** be a `HostExecutor<DslMessage, DslMessage>`. Every edge carries the envelope,
  and a node emitting anything else would break the *next* edge rather than its own.
- The executor's id **must** match the declared node id — gate policy and node state key off it.
- An unregistered `node` name fails registration (`DSL0601`), not the first run.
- `with` is validated against `ParameterSchema` (`DSL0602`).

`DslNodeContext` gives a factory everything a compiled definition gets:

| Member | Purpose |
| --- | --- |
| `Node` | The declared node model, including its expressions |
| `Document` | The whole document |
| `Build` | The `WorkflowBuildContext` — instance id, tenant, attempt, audit |
| `Parameters` | The `with` object, empty rather than null |
| `Require<T>()` / `Optional<T>()` | Resolve host services |

A custom node is hosted the same way a built-in one is, so it gets bound expression roots, its
declared `notify`, and result projection for free. The author writes `ExecuteCoreAsync` and nothing
else.

---

## 13. Registration and hosting

```csharp
builder.Services.AddWorkflowHost(builder.Configuration)
    .AddWorkflow<ExampleOrderWorkflow>()               // compiled, unchanged

    .UseDsl()                                          // routes work before any document exists
    .ConfigureDsl(dsl =>
    {
        dsl.EnforceEgress = true;
        dsl.Policy = DslPolicy.Default with { MaxNodes = 200 };
    })

    .AddDslNode(new RiskScoringNodeFactory())
    .AddDslNode("stamp", ctx => new StampExecutor(ctx.Node.Id))   // delegate form

    .AddDslWorkflow("workflows/order-settlement.json")
    .AddDslWorkflowText(embeddedDocument, "embedded:order")
    .AddDslWorkflowsFromDirectory("workflows/", "*.workflow.json", recursive: true);

app.MapWorkflowApi();
app.MapDslApi();
```

**Order does not matter.** Documents are parsed and validated once the container is built, against
the *complete* node catalog — so `AddDslNode` may come after `AddDslWorkflow`. Making correctness
depend on the order composition happened to be written in would be a trap.

**An invalid document fails startup**, with *every* diagnostic from *every* failing document. Three
broken documents should take one startup to fix, not three.

Documents load from a directory in a stable ordinal order, so a conflict between two documents naming
the same `(name, version)` names the same one every time rather than looking intermittent.

### Routes

| Route | Purpose |
| --- | --- |
| `GET /dsl/schema` | The published JSON Schema, for editor completion |
| `GET /dsl/nodes` | Built-in kinds and every registered custom node with its parameter schema |
| `GET /dsl/functions` | The closed expression vocabulary with arities |
| `GET /dsl/documents` | Registered documents and their content hashes |
| `POST /dsl/validate` | Validate a document without registering it |
| `GET /workflows/{name}` | Not a DSL route, but reports `source` (`dsl` or `compiled`) and, for a document, its `documentHash` |

`POST /dsl/validate` is what an authoring tool calls: it validates against the **live host's**
catalog, which an offline linter cannot do. It reflects registered node names back to the caller, so
give it the same authorization as the catalog routes.

---

## 14. Validation and diagnostics

Two phases, because one cannot do the job.

**Phase 1 — JSON Schema** checks shape: required properties, `kind`-discriminated variants, id and
SemVer patterns, ISO-8601 durations, principal formats, enum values.

**Phase 2 — the semantic validator** checks everything a schema cannot express. A schema cannot
compare two array items, follow a reference, walk a graph, parse a sub-language, or know what the
host has registered.

The phases stop where continuing would be noise: a document failing the schema is not read into the
model, because reporting forty type errors from a half-understood document buries the one that
matters.

### Diagnostic codes

| Code | Check |
| --- | --- |
| `DSL0101` | `dsl` major version is supported |
| `DSL0102` | Content hash conflicts with an already-published `(name, version)` |
| `DSL0103` | Document is valid JSON and an object |
| `DSL0104` | Document matches the JSON Schema |
| `DSL0201` | Node ids are unique |
| `DSL0202` | `start` names a real node |
| `DSL0203` | Every `output` entry names a real node |
| `DSL0207` | Every edge endpoint exists *(with a nearest-match suggestion)* |
| `DSL0208` | No duplicate unconditional edge unless `idempotent` |
| `DSL0301` | Every node is reachable from `start` *(warning)* |
| `DSL0302` | No reachable dead end outside `output` *(warning)* |
| `DSL0303` | No cycle without a `delay`, `wait-event` or `approval` on it |
| `DSL0304` | Barrier sources are reachable, so the barrier can release |
| `DSL0401` | Every expression parses |
| `DSL0412` | Every function is known, with correct arity |
| `DSL0413` | No non-deterministic value in an edge condition or gate predicate |
| `DSL0414` | Expression depth within the limit |
| `DSL0501` | No gate on a non-gateable kind |
| `DSL0502` | A `conditional` gate has a `when` |
| `DSL0503` | `escalate` expiry names escalation assignees |
| `DSL0601` | Every `custom` node names a registered factory |
| `DSL0602` | `with` satisfies the factory's parameter schema |
| `DSL0603` | `http` nodes declare allowed hosts when egress is enforced |
| `DSL0701` | Document, node, edge and expression limits |

Every diagnostic carries a **JSON Pointer**:

```
DSL0412  error  /nodes/3/gate/when   Unknown function 'lookupCustomer'.  Did you mean 'coalesce'?
DSL0207  error  /edges/5/to          Edge targets 'setle', which is not a node.  Did you mean 'settle'?
DSL0301  warn   /nodes/7             Node 'notify' is unreachable from 'price'.
```

### Skipped checks

`DSL0102`, `DSL0601`, `DSL0602` and `DSL0603` need a host. Validating offline reports them as
**skipped** rather than passed, in a `skippedChecks` array:

```json
{ "valid": true, "skippedChecks": ["DSL0601", "DSL0602", "DSL0603", "DSL0102"] }
```

A check that silently did not run is worse than one that openly did not, because only the second can
be acted on.

### Limits

| Limit | Default |
| --- | --- |
| Document size | 1 MB |
| Nodes | 500 |
| Edges | 2000 |
| Expression depth | 32 |
| Expression length | 2048 characters |
| Regex match timeout | 200 ms |

All configurable **down** through `ConfigureDsl`, none up.

---

## 15. Versions, identity and drift

A document registers as `(name, version)` and inherits the framework's rule: **a published version is
immutable.** Instances pin their version, and editing a document under a version its instances are
running would rewrite history mid-flight.

Identity is a canonical SHA-256 (RFC 8785 JCS) of the document:

- Reformatting, whitespace and property reordering **do not** change the hash.
- One byte of behaviour **does**.

Registering a document whose `(name, version)` is already known with a different hash is a startup
failure naming both hashes (`DSL0102`). Editing a workflow means bumping the version — which the
compiled path already demands, stated in a way a document author actually encounters.

`GET /dsl/documents` reports each registered document's hash, which answers the operational question
directly: *is this instance running the document I am looking at?*

---

## 16. What a document inherits for free

None of this is declared in a document, because none of it is the document's business. DSL nodes
traverse exactly the same runtime as compiled ones.

| Capability | How it applies |
| --- | --- |
| **Checkpointing and resume** | Every superstep checkpoints; a parked instance releases its lease and resumes on any replica |
| **At-least-once execution** | Same lease-based dispatch and retry semantics |
| **Executor middleware** | Every DSL node runs through the host's `IExecutorMiddleware` pipeline — logging, metrics, redaction, drift detection |
| **Workflow middleware** | Same `IWorkflowMiddleware` wrapping of the whole run |
| **Redaction** | The same rules apply to envelopes. Worth noting: the envelope deliberately carries more in flight (`ctx` travels with every message), so redaction matters more, not less |
| **Egress control** | `http` nodes go through the same `EgressGuard`. A document cannot widen an allow-list the host has fixed |
| **Multi-tenancy** | Definitions are global; instances are tenant-scoped; `$run.tenantId` is readable |
| **Instance controls** | `cancel`, `suspend`, `resume`, `retry`, `rerun` work identically |
| **Observability** | Event history, SSE streaming with `Last-Event-ID` catch-up, instance logs, the graph endpoint |
| **Tenant gate configuration** | Gated DSL nodes are reconfigurable per tenant unless `locked` |
| **Approval flow** | Quorum, expiry, escalation, segregation of duties, modification |

The catalog reports a DSL node's `kind` in its metadata, so `GET /workflows/{name}/versions/{v}/nodes`
and the graph view describe a document exactly as they describe a compiled definition.

---

## 17. Framework coverage map

Every capability the compiled authoring surface offers, and how a document reaches it.

| Framework capability | DSL |
| --- | --- |
| `IWorkflowDefinition<TContext, TResult>` | The document itself; `context` schema replaces `TContext` |
| `TransformExecutor` | `kind: "transform"` |
| `DelegateExecutor` | ✖ **By design.** Use a `custom` node |
| `ApiCallExecutor` | `kind: "http"` |
| `LlmExecutor` | `kind: "llm"` |
| `DelayExecutor` | `kind: "delay"` |
| `HumanApprovalExecutor` | `kind: "approval"` |
| `FanInExecutor` | `kind: "fan-in"` |
| `PublishDomainEventExecutor` | `kind: "publish"` |
| `WaitForDomainEventExecutor` | `kind: "wait-event"` |
| Custom `HostExecutor<TIn,TOut>` | `kind: "custom"` + `IDslNodeFactory` |
| `RawNode` / `AIAgent` / sub-workflow bindings | ✖ Not exposed |
| `AddEdge` | `{ from, to }` |
| `AddEdge<T>(condition)` | `{ from, to, when }` |
| `AddFanOutEdge` | `{ from, to: [...] }` |
| `AddFanOutEdge<T>(targetSelector)` | `{ from, to: [...], select }` |
| `AddFanInBarrierEdge` | `{ from: [...], to }` |
| `WithOutputFrom` | `output` |
| Edge labels / `idempotent` | `label`, `idempotent` |
| `ApprovalGateBuilder` (all options) | `gate` block |
| `.Locked()` | `gate.locked` |
| `Classify(WorkflowFailure)` | `onFailure` rules, falling through to the default classifier |
| `INotifyingWorkflow` | `notifications` |
| `INodeNotifier.NotifyAsync` | `notify` on a node |
| `IDomainEventTriggeredWorkflow` | `triggers` |
| `IAuditedWorkflowDefinition` | `audit` — **shape only**, see §18 |
| `IContextValidatingWorkflow` | `context` schema |
| `MaxAttempts` / lifetime | `limits` |
| `IWorkflowContext.QueueStateUpdateAsync` etc. | ✖ Not exposed; a `custom` node has full access |
| Middleware, redaction, egress, tenancy, checkpointing | Inherited — see §16 |

---

## 18. Declared but not yet enforced

These fields are accepted by the schema and parsed into the model, but **nothing acts on them yet**.
They are documented here rather than quietly omitted, so nobody relies on behaviour that does not
exist.

| Field | Intended behaviour | Current behaviour |
| --- | --- | --- |
| `strict` | Enforce node `input`/`output` schemas at run time; a violation is dead-stop | Parsed, ignored |
| node `input` / `output` | Per-node contract schemas | Parsed, ignored |
| `result` | Schema the workflow result is expected to satisfy | Parsed, ignored |
| `llm.structuredOutput` | Parse model output into a declared shape | Parsed, ignored — the node returns text |
| `audit.key` | Business key the audit record opens under | Parsed and validated as an expression, ignored |
| `audit.sections` | Sections the record may contain | **Declares the record shape**, so the state endpoint returns a non-null `audit` object — but no DSL node writes entries into it |

The `audit` gap is the largest: a document can declare a record shape, and it will be advertised, but
only a `custom` node can actually record anything into it (via `context.Build.Audit`). A document of
built-in nodes produces an empty record.

Two host prerequisites are also worth stating:

- **`ITimerService` is not registered by the framework or the shipped host.** A `delay` node needs
  one; register an implementation before using that kind.
- **`IChatClient` or `IChatClientResolver` must be registered** for `llm` nodes. With a single
  `IChatClient`, the `model` field is passed through as the model id but does not select a client.

---

## 19. Not expressible

Deliberate boundaries, so you meet them here rather than in an error message.

**No loops or iteration.** There is no `foreach`, and no way to sum or map over an array. Fan-out
over branches is the intended shape for parallel work. Unbounded iteration in a checkpointed engine
has real semantics to establish first — checkpoint size, superstep count, and what a retry means
mid-iteration.

> This is the most commonly hit limit. A workflow that must aggregate over a collection needs a
> `custom` node — which is a five-line executor, not a workaround.

**No sub-workflows.** The engine supports composing workflows; resolving and version-pinning one
document from another needs its own design.

**No runtime publication.** Documents load from disk or memory at startup. A management API that
accepted them at run time would change the registry from immutable to mutable, which touches version
resolution, dispatch, authorization, tenancy and in-flight instance migration.

**No arbitrary code.** No scripting, no reflection by name into arbitrary types, no `eval`. Only
registered factories.

**No export from C#.** A compiled definition cannot be emitted as a document. The DSL is a different
way in, not a serialization of the compiled path.

---

## Appendix A — worked variations

Each mirrors a variation from the
[C# guide's appendix](workflow-authoring-guide.md#appendix-a--worked-variations), so the two front
ends can be read side by side.

### A.1 Linear

```json
{
  "dsl": "abacus.workflow/1.0",
  "name": "linear", "version": "1.0.0",
  "context": { "type": "object", "required": ["orderId", "amount"] },
  "start": "price", "output": ["finish"],
  "nodes": [
    { "id": "price",  "kind": "transform", "set": { "total": "$ctx.amount * 1.2" } },
    { "id": "finish", "kind": "transform", "set": { "order": "$ctx.orderId", "total": "$.total", "status": "'done'" } }
  ],
  "edges": [ { "from": "price", "to": "finish" } ]
}
```

### A.2 Branch

```json
"start": "classify", "output": ["escalate", "settle"],
"nodes": [
  { "id": "classify", "kind": "transform", "set": { "amount": "$ctx.amount" } },
  { "id": "escalate", "kind": "transform", "set": { "route": "'manual'" } },
  { "id": "settle",   "kind": "transform", "set": { "route": "'auto'" } }
],
"edges": [
  { "from": "classify", "to": "escalate", "when": "$.amount > 10000" },
  { "from": "classify", "to": "settle",   "when": "$.amount <= 10000" }
]
```

Make the predicates exhaustive — a message matching neither stops there.

### A.3 Fan-out and fan-in

```json
"start": "split", "output": ["join"],
"nodes": [
  { "id": "split", "kind": "transform", "set": { "seed": "$ctx.amount" } },
  { "id": "ops",   "kind": "transform", "set": { "channel": "'ops'",      "value": "$.seed + 1" } },
  { "id": "cust",  "kind": "transform", "set": { "channel": "'customer'", "value": "$.seed + 2" } },
  { "id": "join",  "kind": "fan-in", "into": "notified" }
],
"edges": [
  { "from": "split", "to": ["ops", "cust"] },
  { "from": ["ops", "cust"], "to": "join" }
]
```

Result: `{ "notified": [ { "channel": "ops", … }, { "channel": "customer", … } ] }`.

### A.4 Approval gate

```json
{ "id": "settle", "kind": "http",
  "url": "https://ledger.internal/v1/settlements",
  "allowedHosts": ["ledger.internal"],
  "method": "POST",
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
  } }
```

### A.5 Durable delay

```json
"nodes": [
  { "id": "submit",  "kind": "transform", "set": { "submitted": "true" } },
  { "id": "coolOff", "kind": "delay", "for": "PT4H" },
  { "id": "confirm", "kind": "transform", "set": { "confirmed": "$.submitted" } }
],
"edges": [
  { "from": "submit",  "to": "coolOff" },
  { "from": "coolOff", "to": "confirm" }
]
```

The envelope passes through the delay unchanged, so `confirm` still sees `submitted`.

### A.6 HTTP call

```json
{ "id": "fetch", "kind": "http",
  "method": "GET",
  "url": "https://catalog.internal/v1/skus/{{ $ctx.sku }}",
  "headers": { "Accept": "application/json" },
  "allowedHosts": ["catalog.internal"],
  "successCodes": [200, 404],
  "timeoutSeconds": 10 }
```

Then branch on the status the node produced:

```json
{ "from": "fetch", "to": "found",   "when": "$.status == 200" },
{ "from": "fetch", "to": "missing", "when": "$.status == 404" }
```

Listing `404` as a success code is what turns "not found" into a branch rather than a failure.

### A.7 LLM node

```json
{ "id": "summarise", "kind": "llm",
  "model": "gpt-4o",
  "system": "You write one-sentence order summaries.",
  "prompt": "Order {{ $ctx.orderId }}, total {{ $.total }}. Summarise.",
  "promptVersion": "v2",
  "temperature": 0.2,
  "maxTokens": 200 }
```

Branch on cost, which the node put on the envelope:

```json
{ "from": "summarise", "to": "review", "when": "$.costUsd > 0.5" }
```

### A.8 Started by an event

```json
"triggers": [ { "topic": "orders.placed", "contextFrom": "$.order" } ],
"context": { "type": "object", "required": ["orderId"] },
"start": "handle",
"nodes": [ { "id": "handle", "kind": "transform", "set": { "sawOrder": "$ctx.orderId" } } ]
```

A message `{ "order": { "orderId": "ORD-9" }, "meta": … }` starts an instance whose context is
`{ "orderId": "ORD-9" }`.

### A.9 Publish and wait

```json
"nodes": [
  { "id": "request", "kind": "publish",
    "topic": "payment.requested",
    "payload": { "order": "$ctx.orderId", "amount": "$.total" },
    "correlationKey": "$ctx.orderId" },
  { "id": "await", "kind": "wait-event",
    "topic": "payment.settled",
    "correlationKey": "$ctx.orderId",
    "timeout": "P3D",
    "onExpiry": "resume" },
  { "id": "done", "kind": "transform",
    "set": { "order": "$ctx.orderId", "paid": "$.amount" } }
],
"edges": [
  { "from": "request", "to": "await" },
  { "from": "await",   "to": "done" }
]
```

`onExpiry: "resume"` lets the graph handle a timeout instead of dead-stopping.

### A.10 Quiet a chatty workflow

```json
"notifications": {
  "level": "minimal",
  "byNode": { "score": "standard" },
  "stream": false
}
```

Minimal everywhere, loud on the one interesting node, no live streaming — and the durable log still
records everything.

### A.11 A custom node doing the domain work

```json
"nodes": [
  { "id": "load",  "kind": "http", "url": "https://data.internal/v1/case/{{ $ctx.caseId }}",
    "allowedHosts": ["data.internal"] },
  { "id": "score", "kind": "custom", "node": "score-risk", "with": { "threshold": 0.8 } },
  { "id": "route", "kind": "transform", "set": { "outcome": "$.flagged" } }
],
"edges": [
  { "from": "load",  "to": "score" },
  { "from": "score", "to": "route" }
]
```

The document orchestrates; the engineer's node computes. That division is the design.

---

## Appendix B — full field reference

```jsonc
{
  "dsl": "abacus.workflow/1.0",       // required
  "name": "kebab-case-name",          // required
  "version": "1.0.0",                 // required, SemVer
  "description": "…",
  "context": { /* JSON Schema */ },
  "result":  { /* JSON Schema — not yet enforced */ },
  "strict":  false,                   // not yet enforced
  "start": "node-id",                 // required
  "output": ["node-id"],              // defaults to terminal nodes

  "nodes": [                          // required, 1–500
    {
      "id": "node-id",                // required
      "kind": "transform",            // required
      "description": "…",
      "input":  { /* not yet enforced */ },
      "output": { /* not yet enforced */ },

      "gate": {
        "mode": "conditional",        // autonomous | requireApproval | conditional
        "when": "$.total > 25000",    // required for conditional
        "reason": "…",
        "assignTo": ["group:finance"],
        "requireApprovers": 1,
        "expiresAfter": "PT24H",
        "onExpiry": { "action": "deadStop", "assignTo": [] },
        "allowModification": false,
        "requireSegregationOfDuties": false,
        "locked": false
      },

      "notify": { "name": "priced", "payload": { "total": "$.total" } },

      // kind: transform
      "set": { "path.to.field": "<expr>" },
      "replace": false,

      // kind: http
      "method": "GET",
      "url": "<template>",
      "headers": { "K": "<template>" },
      "body": "<template>",
      "timeoutSeconds": 30,
      "successCodes": [200, 201, 202, 204],
      "allowedHosts": ["host"],
      "sendIdempotencyKey": true,

      // kind: llm
      "model": "…",
      "system": "<template>",
      "prompt": "<template>",
      "promptVersion": "…",
      "structuredOutput": { /* not yet enforced */ },
      "temperature": 0.2,
      "maxTokens": 500,
      "streamDeltas": false,
      "emitCompletion": true,

      // kind: delay
      "for": "PT5M",

      // kind: publish
      "topic": "a.b.c",
      "payload": { "k": "<expr>" },
      "correlationKey": "<expr>",
      "scope": "local",               // local | distributed

      // kind: wait-event
      // "topic", "correlationKey" as above
      "timeout": "P3D",
      "onExpiry": "deadStop",         // deadStop | resume

      // kind: fan-in
      "into": "items",

      // kind: custom
      "node": "registered-name",
      "with": { }
    }
  ],

  "edges": [                          // 0–2000
    {
      "from": "a",                    // or ["a","b"] for a barrier
      "to": "b",                      // or ["b","c"] for fan-out
      "when": "<expr>",               // not valid on a barrier
      "select": "<expr>",             // fan-out only
      "label": "…",
      "idempotent": false
    }
  ],

  "triggers": [
    { "topic": "a.b.*", "correlationKey": "literal", "contextFrom": "<expr>" }
  ],

  "notifications": {
    "level": "standard",              // minimal | lifecycle | standard
    "stream": true,
    "byNode": { "node-id": "minimal" },
    "emits": ["name"]
  },

  "onFailure": [
    { "match": { "exception": "ApiCallFailureException", "status": "5xx", "node": "id" },
      "disposition": "retry" }        // retry | deadStop | escalate
  ],

  "audit": { "key": "<expr>", "sections": ["submission", "outcome"] },

  "limits": { "maxAttempts": 5, "maxLifetimeHours": 72 }
}
```

---

## Related

- [Wiki: Authoring with the DSL](wiki.md#authoring-with-the-dsl) — the short version
- [Authoring workflows in C#](workflow-authoring-guide.md) — the compiled path, in the same depth
- [Wiki: two ways to author a workflow](wiki.md#two-ways-to-author-a-workflow) — choosing between them
- [JSON Schema](schema/abacus-workflow-dsl-1.0.json) — the normative contract
- [Design narrative](implementation/06-workflow-dsl-design.md) — why the DSL is shaped this way
- [Implementation plan](implementation/07-workflow-dsl-implementation-plan.md) — status and deviations

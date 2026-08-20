namespace Abacus.Run.IntegrationTests;

/// <summary>
/// One document per conversion the interpreter performs. Together they exercise every node kind,
/// every edge shape, gates, notifications, triggers, audit and failure rules — so a regression in any
/// one mapping fails a named test rather than a general "the DSL broke".
/// </summary>
internal static class DslDocuments
{
    /// <summary>Linear: two transforms and one edge. The envelope's baseline.</summary>
    internal const string Linear = """
    {
      "dsl": "abacus.workflow/1.0",
      "name": "dsl-linear",
      "version": "1.0.0",
      "context": {
        "type": "object",
        "required": ["orderId"],
        "properties": { "orderId": { "type": "string" }, "amount": { "type": "number" } }
      },
      "start": "price",
      "output": ["finish"],
      "nodes": [
        { "id": "price", "kind": "transform",
          "set": { "total": "$ctx.amount * 2", "order": "$ctx.orderId" } },
        { "id": "finish", "kind": "transform",
          "set": { "status": "'done'", "carried": "$ctx.orderId", "total": "$.total" } }
      ],
      "edges": [ { "from": "price", "to": "finish" } ]
    }
    """;

    /// <summary>Branch: two conditional edges out of one node, which is how the DSL says "if".</summary>
    internal const string Branch = """
    {
      "dsl": "abacus.workflow/1.0",
      "name": "dsl-branch",
      "version": "1.0.0",
      "start": "classify",
      "output": ["large", "small"],
      "nodes": [
        { "id": "classify", "kind": "transform", "set": { "amount": "$ctx.amount" } },
        { "id": "large", "kind": "transform", "set": { "band": "'large'" } },
        { "id": "small", "kind": "transform", "set": { "band": "'small'" } }
      ],
      "edges": [
        { "from": "classify", "to": "large", "when": "$.amount > 1000" },
        { "from": "classify", "to": "small", "when": "$.amount <= 1000" }
      ]
    }
    """;

    /// <summary>Fan-out to two branches, then a barrier that waits for both.</summary>
    internal const string FanOutIn = """
    {
      "dsl": "abacus.workflow/1.0",
      "name": "dsl-fan",
      "version": "1.0.0",
      "start": "split",
      "output": ["join"],
      "nodes": [
        { "id": "split", "kind": "transform", "set": { "seed": "$ctx.amount" } },
        { "id": "left",  "kind": "transform", "set": { "side": "'left'",  "value": "$.seed + 1" } },
        { "id": "right", "kind": "transform", "set": { "side": "'right'", "value": "$.seed + 2" } },
        { "id": "join",  "kind": "fan-in", "into": "branches" }
      ],
      "edges": [
        { "from": "split", "to": ["left", "right"] },
        { "from": ["left", "right"], "to": "join" }
      ]
    }
    """;

    /// <summary>A conditional gate that trips above a threshold, parking the instance.</summary>
    internal const string Gated = """
    {
      "dsl": "abacus.workflow/1.0",
      "name": "dsl-gated",
      "version": "1.0.0",
      "start": "prepare",
      "output": ["settle"],
      "nodes": [
        { "id": "prepare", "kind": "transform", "set": { "amount": "$ctx.amount" } },
        { "id": "settle", "kind": "transform",
          "set": { "status": "'settled'" },
          "gate": {
            "mode": "conditional",
            "when": "$.amount > 25000",
            "reason": "RegulatedSettlement",
            "assignTo": ["group:finance"],
            "expiresAfter": "PT8H",
            "onExpiry": { "action": "deadStop" }
          } }
      ],
      "edges": [ { "from": "prepare", "to": "settle" } ]
    }
    """;


    /// <summary>
    /// The same gate with no assignees. An assigned gate correctly refuses an anonymous decider with
    /// 403, so the resume path needs a gate anyone may decide.
    /// </summary>
    internal const string GatedOpen = """
    {
      "dsl": "abacus.workflow/1.0",
      "name": "dsl-gated-open",
      "version": "1.0.0",
      "start": "prepare",
      "output": ["settle"],
      "nodes": [
        { "id": "prepare", "kind": "transform", "set": { "amount": "$ctx.amount" } },
        { "id": "settle", "kind": "transform",
          "set": { "status": "'settled'" },
          "gate": { "mode": "conditional", "when": "$.amount > 25000", "reason": "LargeSettlement" } }
      ],
      "edges": [ { "from": "prepare", "to": "settle" } ]
    }
    """;
    /// <summary>An explicit approval node: the gate is the node, not configuration elsewhere.</summary>
    internal const string ApprovalNode = """
    {
      "dsl": "abacus.workflow/1.0",
      "name": "dsl-approval",
      "version": "1.0.0",
      "start": "prepare",
      "output": ["done"],
      "nodes": [
        { "id": "prepare", "kind": "transform", "set": { "amount": "$ctx.amount" } },
        { "id": "sign-off", "kind": "approval" },
        { "id": "done", "kind": "transform", "set": { "status": "'approved'" } }
      ],
      "edges": [
        { "from": "prepare", "to": "sign-off" },
        { "from": "sign-off", "to": "done" }
      ]
    }
    """;

    /// <summary>An HTTP call, with the response projected onto the envelope as status and body.</summary>
    internal const string Http = """
    {
      "dsl": "abacus.workflow/1.0",
      "name": "dsl-http",
      "version": "1.0.0",
      "start": "call",
      "output": ["read"],
      "nodes": [
        { "id": "call", "kind": "http",
          "method": "POST",
          "url": "https://ledger.internal/v1/orders/{{ $ctx.orderId }}",
          "headers": { "X-Order": "{{ $ctx.orderId }}" },
          "body": "{\"amount\":{{ $ctx.amount }}}",
          "allowedHosts": ["ledger.internal"] },
        { "id": "read", "kind": "transform",
          "set": { "status": "$.status", "reference": "$.body.reference" } }
      ],
      "edges": [ { "from": "call", "to": "read" } ]
    }
    """;

    /// <summary>An LLM node, with tokens and cost projected onto the envelope.</summary>
    internal const string Llm = """
    {
      "dsl": "abacus.workflow/1.0",
      "name": "dsl-llm",
      "version": "1.0.0",
      "start": "summarise",
      "output": ["read"],
      "nodes": [
        { "id": "summarise", "kind": "llm",
          "model": "stub-model",
          "system": "You summarise orders.",
          "prompt": "Summarise order {{ $ctx.orderId }} for {{ $ctx.amount }}." },
        { "id": "read", "kind": "transform",
          "set": { "summary": "$.text", "inputTokens": "$.inputTokens", "model": "$.model" } }
      ],
      "edges": [ { "from": "summarise", "to": "read" } ]
    }
    """;

    /// <summary>A durable delay, which must pass the envelope through rather than replace it.</summary>
    internal const string Delay = """
    {
      "dsl": "abacus.workflow/1.0",
      "name": "dsl-delay",
      "version": "1.0.0",
      "start": "prepare",
      "output": ["after"],
      "nodes": [
        { "id": "prepare", "kind": "transform", "set": { "marker": "'before'" } },
        { "id": "wait", "kind": "delay", "for": "PT1S" },
        { "id": "after", "kind": "transform", "set": { "carried": "$.marker" } }
      ],
      "edges": [
        { "from": "prepare", "to": "wait" },
        { "from": "wait", "to": "after" }
      ]
    }
    """;

    /// <summary>Publishes a domain event on the way past, and carries on.</summary>
    internal const string Publish = """
    {
      "dsl": "abacus.workflow/1.0",
      "name": "dsl-publish",
      "version": "1.0.0",
      "start": "prepare",
      "output": ["done"],
      "nodes": [
        { "id": "prepare", "kind": "transform", "set": { "orderId": "$ctx.orderId" } },
        { "id": "announce", "kind": "publish",
          "topic": "dsl.orders.priced",
          "payload": { "order": "$.orderId", "at": "'now'" },
          "correlationKey": "$.orderId" },
        { "id": "done", "kind": "transform", "set": { "status": "'published'" } }
      ],
      "edges": [
        { "from": "prepare", "to": "announce" },
        { "from": "announce", "to": "done" }
      ]
    }
    """;

    /// <summary>Parks until a matching message arrives, then resumes with its payload.</summary>
    internal const string WaitEvent = """
    {
      "dsl": "abacus.workflow/1.0",
      "name": "dsl-wait",
      "version": "1.0.0",
      "start": "prepare",
      "output": ["settled"],
      "nodes": [
        { "id": "prepare", "kind": "transform", "set": { "orderId": "$ctx.orderId" } },
        { "id": "await-payment", "kind": "wait-event",
          "topic": "dsl.payment.settled",
          "timeout": "P3D" },
        { "id": "settled", "kind": "transform",
          "set": { "paidAmount": "$.amount", "order": "$ctx.orderId" } }
      ],
      "edges": [
        { "from": "prepare", "to": "await-payment" },
        { "from": "await-payment", "to": "settled" }
      ]
    }
    """;

    /// <summary>A registered custom node — the DSL's extension seam.</summary>
    internal const string Custom = """
    {
      "dsl": "abacus.workflow/1.0",
      "name": "dsl-custom",
      "version": "1.0.0",
      "start": "seed",
      "output": ["double"],
      "nodes": [
        { "id": "seed", "kind": "transform", "set": { "value": "$ctx.amount" } },
        { "id": "double", "kind": "custom", "node": "doubler",
          "with": { "field": "value", "times": 3 } }
      ],
      "edges": [ { "from": "seed", "to": "double" } ]
    }
    """;

    /// <summary>A node that emits a workflow-defined notification.</summary>
    internal const string Notifying = """
    {
      "dsl": "abacus.workflow/1.0",
      "name": "dsl-notify",
      "version": "1.0.0",
      "start": "price",
      "output": ["price"],
      "nodes": [
        { "id": "price", "kind": "transform",
          "set": { "total": "$ctx.amount * 2" },
          "notify": { "name": "priced", "payload": { "total": "$.total", "order": "$ctx.orderId" } } }
      ],
      "edges": [],
      "notifications": { "level": "standard", "stream": true }
    }
    """;

    /// <summary>Fails on an HTTP status the document classifies as dead-stop.</summary>
    internal const string Failing = """
    {
      "dsl": "abacus.workflow/1.0",
      "name": "dsl-failing",
      "version": "1.0.0",
      "start": "call",
      "output": ["call"],
      "nodes": [
        { "id": "call", "kind": "http",
          "url": "https://ledger.internal/v1/fail",
          "allowedHosts": ["ledger.internal"] }
      ],
      "edges": [],
      "onFailure": [
        { "match": { "exception": "ApiCallFailureException", "status": "4xx" }, "disposition": "deadStop" }
      ]
    }
    """;

    /// <summary>Declares an audit record, so the audited variant is selected.</summary>
    internal const string Audited = """
    {
      "dsl": "abacus.workflow/1.0",
      "name": "dsl-audited",
      "version": "1.0.0",
      "start": "only",
      "output": ["only"],
      "nodes": [ { "id": "only", "kind": "transform", "set": { "status": "'done'" } } ],
      "edges": [],
      "audit": { "key": "$ctx.orderId", "sections": ["submission", "outcome"] }
    }
    """;

    /// <summary>Started by a domain event rather than by API.</summary>
    internal const string Triggered = """
    {
      "dsl": "abacus.workflow/1.0",
      "name": "dsl-triggered",
      "version": "1.0.0",
      "start": "handle",
      "output": ["handle"],
      "nodes": [
        { "id": "handle", "kind": "transform", "set": { "sawOrder": "$ctx.orderId" } }
      ],
      "edges": [],
      "triggers": [ { "topic": "dsl.orders.placed" } ]
    }
    """;

    /// <summary>A fan-out whose selector picks a subset of targets by index.</summary>
    internal const string SelectiveFanOut = """
    {
      "dsl": "abacus.workflow/1.0",
      "name": "dsl-select",
      "version": "1.0.0",
      "start": "route",
      "output": ["first", "second"],
      "nodes": [
        { "id": "route", "kind": "transform", "set": { "pick": "$ctx.amount" } },
        { "id": "first",  "kind": "transform", "set": { "chosen": "'first'" } },
        { "id": "second", "kind": "transform", "set": { "chosen": "'second'" } }
      ],
      "edges": [
        { "from": "route", "to": ["first", "second"], "select": "$.pick" }
      ]
    }
    """;

    /// <summary>
    /// Carries a secret in the start context, reads it in a node, and puts it in a notification
    /// payload. The node must see the real value; anything that leaves the process must not.
    /// </summary>
    internal const string Secret = """
    {
      "dsl": "abacus.workflow/1.0",
      "name": "dsl-secret",
      "version": "1.0.0",
      "start": "handle",
      "output": ["handle"],
      "nodes": [
        { "id": "handle", "kind": "transform",
          "set": {
            "order": "$ctx.orderId",
            "card": "$ctx.cardNumber",
            "cardLength": "len($ctx.cardNumber)"
          },
          "notify": {
            "name": "handled",
            "payload": { "order": "$ctx.orderId", "card": "$ctx.cardNumber" }
          } }
      ],
      "edges": [],
      "notifications": { "level": "standard", "stream": true }
    }
    """;

    /// <summary>
    /// Four hops, each reading the context. Run with a large context it answers the question the
    /// envelope raises: does carrying <c>ctx</c> through every node grow the checkpoint per hop?
    /// </summary>
    internal const string Wide = """
    {
      "dsl": "abacus.workflow/1.0",
      "name": "dsl-wide",
      "version": "1.0.0",
      "start": "one",
      "output": ["four"],
      "nodes": [
        { "id": "one",   "kind": "transform", "set": { "hop": "1", "size": "len($ctx.notes)" } },
        { "id": "two",   "kind": "transform", "set": { "hop": "2", "size": "$.size" } },
        { "id": "three", "kind": "transform", "set": { "hop": "3", "size": "$.size" } },
        { "id": "four",  "kind": "transform",
          "set": { "hop": "4", "size": "$.size", "order": "$ctx.orderId", "seen": "len($ctx.notes)" } }
      ],
      "edges": [
        { "from": "one", "to": "two" },
        { "from": "two", "to": "three" },
        { "from": "three", "to": "four" }
      ]
    }
    """;

    /// <summary>
    /// Names a registered factory that hands back the wrong executor shape. It registers — the
    /// catalog knows the name and the parameters check out — and fails when it is built, which is the
    /// only moment the shape exists to be checked.
    /// </summary>
    internal const string WrongShape = """
    {
      "dsl": "abacus.workflow/1.0",
      "name": "dsl-wrong-shape",
      "version": "1.0.0",
      "start": "seed",
      "output": ["broken"],
      "nodes": [
        { "id": "seed", "kind": "transform", "set": { "value": "$ctx.amount" } },
        { "id": "broken", "kind": "custom", "node": "wrong-shape" }
      ],
      "edges": [ { "from": "seed", "to": "broken" } ]
    }
    """;

    /// <summary>An http node with no allow-list, for a host that enforces egress.</summary>
    internal const string UnrestrictedHttp = """
    {
      "dsl": "abacus.workflow/1.0",
      "name": "dsl-open-egress",
      "version": "1.0.0",
      "start": "call",
      "output": ["call"],
      "nodes": [
        { "id": "call", "kind": "http", "url": "https://anywhere.example/v1/things" }
      ],
      "edges": []
    }
    """;

    internal static IReadOnlyList<(string Name, string Text)> All =>
    [
        ("linear", Linear),
        ("branch", Branch),
        ("fan-out-in", FanOutIn),
        ("gated", Gated),
        ("gated-open", GatedOpen),
        ("approval", ApprovalNode),
        ("http", Http),
        ("llm", Llm),
        ("delay", Delay),
        ("publish", Publish),
        ("wait-event", WaitEvent),
        ("custom", Custom),
        ("notifying", Notifying),
        ("failing", Failing),
        ("audited", Audited),
        ("triggered", Triggered),
        ("selective-fan-out", SelectiveFanOut),
        ("secret", Secret),
        ("wide", Wide),
        ("wrong-shape", WrongShape)
    ];
}

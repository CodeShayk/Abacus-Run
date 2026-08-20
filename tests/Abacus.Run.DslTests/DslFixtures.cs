using System.Text.Json.Nodes;
using Abacus.Run.Dsl.Validation;
using FluentAssertions;

namespace Abacus.Run.DslTests;

/// <summary>
/// Document fixtures. Tests start from a valid document and break exactly one thing, so a failure
/// names the check rather than a pile of unrelated diagnostics.
/// </summary>
internal static class DslFixtures
{
    /// <summary>A minimal valid document: two transform nodes and one edge.</summary>
    internal const string MinimalText = """
    {
      "dsl": "abacus.workflow/1.0",
      "name": "minimal",
      "version": "1.0.0",
      "start": "a",
      "output": ["b"],
      "nodes": [
        { "id": "a", "kind": "transform", "set": { "seen": "true" } },
        { "id": "b", "kind": "transform", "set": { "done": "true" } }
      ],
      "edges": [ { "from": "a", "to": "b" } ]
    }
    """;

    /// <summary>The design's worked example: every optional block populated.</summary>
    internal const string FullText = """
    {
      "dsl": "abacus.workflow/1.0",
      "name": "order-settlement",
      "version": "1.2.0",
      "description": "Prices an order, escalates large ones, settles.",
      "context": {
        "type": "object",
        "required": ["orderId"],
        "properties": { "orderId": { "type": "string" }, "amount": { "type": "number" } }
      },
      "start": "validate",
      "output": ["complete"],
      "nodes": [
        { "id": "validate", "kind": "transform",
          "set": { "total": "$ctx.amount" } },
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
        { "from": "settle", "to": "complete" }
      ],
      "triggers": [ { "topic": "orders.placed", "correlationKey": "$.orderId" } ],
      "notifications": { "level": "standard", "stream": true, "emits": ["priced"] },
      "onFailure": [
        { "match": { "exception": "ApiCallFailureException", "status": "5xx" }, "disposition": "retry" }
      ],
      "audit": { "key": "$ctx.orderId", "sections": ["submission", "outcome"] },
      "limits": { "maxAttempts": 5 }
    }
    """;

    internal static JsonObject Minimal() => (JsonObject)JsonNode.Parse(MinimalText)!;

    internal static JsonObject Full() => (JsonObject)JsonNode.Parse(FullText)!;

    /// <summary>Builds a document from the minimal fixture with one mutation applied.</summary>
    internal static string Broken(Action<JsonObject> mutate)
    {
        JsonObject document = Minimal();
        mutate(document);
        return document.ToJsonString();
    }

    internal static string BrokenFull(Action<JsonObject> mutate)
    {
        JsonObject document = Full();
        mutate(document);
        return document.ToJsonString();
    }

    internal static JsonObject Node(JsonObject document, int index) => (JsonObject)document["nodes"]![index]!;

    internal static JsonObject Edge(JsonObject document, int index) => (JsonObject)document["edges"]![index]!;

    /// <summary>Asserts exactly one diagnostic of the code, and returns it for pointer assertions.</summary>
    internal static DslDiagnostic ShouldReport(
        this DslParseResult result, string code, DslSeverity severity = DslSeverity.Error)
    {
        DslDiagnostic[] matches = result.Validation.Diagnostics
            .Where(d => d.Code == code).ToArray();

        matches.Should().NotBeEmpty(
            $"expected {code} but got:{Environment.NewLine}{result.Validation.Describe()}");

        matches[0].Severity.Should().Be(severity);
        matches[0].Pointer.Should().NotBeNull();
        return matches[0];
    }

    internal static void ShouldBeClean(this DslParseResult result)
    {
        result.IsValid.Should().BeTrue(
            $"the document should be valid but reported:{Environment.NewLine}{result.Validation.Describe()}");
    }
}

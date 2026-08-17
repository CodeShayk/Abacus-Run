using System.Text.Json.Nodes;
using Abacus.Run.Dsl.Validation;
using FluentAssertions;
using Xunit;

namespace Abacus.Run.DslTests;

public class DslCanonicalHashTests
{
    private static string Hash(string json) => DslCanonicalHash.Compute(JsonNode.Parse(json));

    [Fact]
    public void Reformatting_does_not_change_the_hash()
    {
        string compact = """{"a":1,"b":"x"}""";
        string spaced = """
        {
            "a" : 1 ,
            "b" : "x"
        }
        """;

        Hash(compact).Should().Be(Hash(spaced));
    }

    [Fact]
    public void Property_order_does_not_change_the_hash()
        => Hash("""{"a":1,"b":2,"c":3}""").Should().Be(Hash("""{"c":3,"a":1,"b":2}"""));

    [Fact]
    public void Nested_property_order_does_not_change_the_hash()
        => Hash("""{"outer":{"z":1,"a":2}}""").Should().Be(Hash("""{"outer":{"a":2,"z":1}}"""));

    [Fact]
    public void Number_scale_does_not_change_the_hash()
        => Hash("""{"a":1.50}""").Should().Be(Hash("""{"a":1.5}"""));

    [Fact]
    public void Array_order_does_change_the_hash()
        => Hash("""{"a":[1,2]}""").Should().NotBe(Hash("""{"a":[2,1]}"""));

    [Fact]
    public void A_changed_value_changes_the_hash()
        => Hash("""{"a":1}""").Should().NotBe(Hash("""{"a":2}"""));

    [Fact]
    public void A_changed_key_changes_the_hash()
        => Hash("""{"a":1}""").Should().NotBe(Hash("""{"b":1}"""));

    [Fact]
    public void Hash_is_a_lowercase_sha256()
        => Hash("""{"a":1}""").Should().MatchRegex("^[0-9a-f]{64}$");

    [Fact]
    public void Hash_is_stable_across_calls()
        => Hash(DslFixtures.FullText).Should().Be(Hash(DslFixtures.FullText));

    [Theory]
    [InlineData("\"a\\\"b\"", "a\"b")]
    [InlineData("\"a\\\\b\"", "a\\b")]
    [InlineData("\"a\\nb\"", "a\nb")]
    public void Strings_are_escaped_canonically(string json, string _)
        => DslCanonicalHash.Canonicalize(JsonNode.Parse($"{{\"k\":{json}}}"))
            .Should().StartWith("{\"k\":\"");

    [Fact]
    public void Null_values_survive_canonicalisation()
        => DslCanonicalHash.Canonicalize(JsonNode.Parse("""{"a":null}""")).Should().Be("""{"a":null}""");

    [Fact]
    public void Booleans_survive_canonicalisation()
        => DslCanonicalHash.Canonicalize(JsonNode.Parse("""{"a":true,"b":false}"""))
            .Should().Be("""{"a":true,"b":false}""");
}

public class DslSchemaResourceTests
{
    /// <summary>
    /// One copy of the schema. If the embedded resource and the published file ever diverge, an
    /// editor validates against one document and the host enforces another.
    /// </summary>
    [Fact]
    public void The_embedded_schema_matches_the_published_file()
    {
        string? repositoryRoot = FindRepositoryRoot();
        repositoryRoot.Should().NotBeNull("the test needs the repository to compare against");

        string path = Path.Combine(repositoryRoot!, "docs", "schema", "abacus-workflow-dsl-1.0.json");
        File.Exists(path).Should().BeTrue($"the published schema should be at {path}");

        Normalise(File.ReadAllText(path)).Should().Be(Normalise(DslSchemaValidator.SchemaText));
    }

    [Fact]
    public void The_embedded_schema_is_a_usable_schema()
    {
        DslSchemaValidator.SchemaText.Should().NotBeNullOrWhiteSpace();
        DslSchemaValidator.Validate(JsonNode.Parse(DslFixtures.MinimalText)).Should().BeEmpty();
    }

    [Fact]
    public void The_schema_declares_draft_2020_12()
        => DslSchemaValidator.SchemaText.Should().Contain("draft/2020-12/schema");

    private static string Normalise(string text)
        => text.ReplaceLineEndings("\n").TrimEnd();

    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "docs", "schema")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}

/// <summary>
/// A validator has to survive the documents it exists to complain about. These are the shapes that
/// tempt a validator into throwing instead of diagnosing.
/// </summary>
public class DslValidatorRobustnessTests
{
    [Theory]
    [InlineData("""{"dsl":"abacus.workflow/1.0","name":"x","version":"1.0.0","start":"a","nodes":[{"id":"a","kind":"transform","set":{"x":"1"}},{"id":"a","kind":"transform","set":{"y":"2"}}],"edges":[{"from":"a","to":"a"}]}""")]
    [InlineData("""{"dsl":"abacus.workflow/1.0","name":"x","version":"1.0.0","start":"ghost","nodes":[{"id":"a","kind":"transform","set":{"x":"1"}}],"edges":[{"from":"ghost","to":"phantom"}]}""")]
    [InlineData("""{"dsl":"abacus.workflow/1.0","name":"x","version":"1.0.0","start":"a","nodes":[{"id":"a","kind":"transform","set":{"x":"$.a = ="}}],"edges":[]}""")]
    [InlineData("""{"dsl":"abacus.workflow/1.0","name":"x","version":"1.0.0","start":"a","nodes":[{"id":"a","kind":"transform","set":{"x":"1"}},{"id":"b","kind":"transform","set":{"x":"1"}},{"id":"c","kind":"transform","set":{"x":"1"}}],"edges":[{"from":"a","to":"b"},{"from":"b","to":"c"},{"from":"c","to":"a"}]}""")]
    public void Pathological_documents_are_diagnosed_not_thrown(string text)
    {
        Action act = () => DslParser.Parse(text, new DslEnvironment { EnforceEgress = false });
        act.Should().NotThrow();
    }

    [Fact]
    public void A_duplicate_id_inside_a_cycle_is_still_diagnosed()
    {
        string text = """
        {
          "dsl": "abacus.workflow/1.0", "name": "x", "version": "1.0.0", "start": "a",
          "nodes": [
            { "id": "a", "kind": "transform", "set": { "x": "1" } },
            { "id": "a", "kind": "transform", "set": { "y": "2" } },
            { "id": "b", "kind": "transform", "set": { "z": "3" } }
          ],
          "edges": [ { "from": "a", "to": "b" }, { "from": "b", "to": "a" } ]
        }
        """;

        DslParseResult result = DslParser.Parse(text, new DslEnvironment { EnforceEgress = false });

        result.Validation.Has(DslCodes.DuplicateNodeId).Should().BeTrue();
        result.Validation.Has(DslCodes.TightCycle).Should().BeTrue();
    }

    [Fact]
    public void An_empty_object_is_diagnosed()
        => DslParser.Parse("{}").IsValid.Should().BeFalse();

    [Fact]
    public void ParseOrThrow_reports_every_diagnostic()
    {
        Action act = () => DslParser.ParseOrThrow(
            DslFixtures.Broken(d => d["start"] = "nope"), source: "bad.json");

        act.Should().Throw<DslValidationException>()
            .Which.Message.Should().Contain("bad.json").And.Contain("DSL0202");
    }

    [Fact]
    public void ParseOrThrow_returns_the_document_when_valid()
        => DslParser.ParseOrThrow(DslFixtures.MinimalText).Name.Should().Be("minimal");

    [Fact]
    public void ParseFile_reports_a_missing_file_rather_than_throwing()
    {
        DslParseResult result = DslParser.ParseFile(
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));

        result.IsValid.Should().BeFalse();
        result.Validation.Has(DslCodes.MalformedJson).Should().BeTrue();
    }

    [Fact]
    public void ParseFile_reads_a_real_document()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dsl-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, DslFixtures.MinimalText);

        try
        {
            DslParser.ParseFile(path).IsValid.Should().BeTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }
}

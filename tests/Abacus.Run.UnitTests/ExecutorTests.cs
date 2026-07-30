using System.Net;
using System.Text;
using System.Text.Json;
using Abacus.Run.Abstractions;
using Abacus.Run.Executors;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Abacus.Run.UnitTests;

public class TemplateEngineTests
{
    private sealed record Invoice(string Id, decimal Amount, Customer? Customer = null);

    private sealed record Customer(string Name);

    [Fact]
    public void Returns_templates_without_placeholders_unchanged()
        => TemplateEngine.Render("https://api/x", TemplateBindings.Empty).Should().Be("https://api/x");

    [Fact]
    public void Substitutes_a_property_from_the_root_object()
        => TemplateEngine.Render("/invoices/{{ Id }}", TemplateBindings.From(new Invoice("INV-1", 10)))
            .Should().Be("/invoices/INV-1");

    [Theory]
    [InlineData("{{ context.Id }}")]
    [InlineData("{{ input.Id }}")]
    [InlineData("{{Id}}")]
    [InlineData("{{   Id   }}")]
    public void Accepts_root_aliases_and_tolerates_whitespace(string template)
        => TemplateEngine.Render(template, TemplateBindings.From(new Invoice("INV-1", 10))).Should().Be("INV-1");

    [Fact]
    public void Resolves_nested_paths()
        => TemplateEngine.Render("{{ context.Customer.Name }}",
                TemplateBindings.From(new Invoice("INV-1", 10, new Customer("ACME"))))
            .Should().Be("ACME");

    [Fact]
    public void Resolves_from_json_elements()
    {
        JsonElement json = TestFactory.Json("""{"id":"INV-9","customer":{"name":"Globex"}}""");

        TemplateEngine.Render("{{ id }}/{{ customer.name }}", TemplateBindings.From(json))
            .Should().Be("INV-9/Globex");
    }

    [Fact]
    public void Resolves_from_dictionaries()
    {
        var bindings = TemplateBindings.From(new Dictionary<string, object?> { ["id"] = "D-1" });
        TemplateEngine.Render("{{ id }}", bindings).Should().Be("D-1");
    }

    [Fact]
    public void Property_lookup_is_case_insensitive()
        => TemplateEngine.Render("{{ id }}", TemplateBindings.From(new Invoice("INV-1", 10))).Should().Be("INV-1");

    [Fact]
    public void Unresolved_placeholders_render_as_empty()
        => TemplateEngine.Render("/x/{{ Missing }}/y", TemplateBindings.From(new Invoice("INV-1", 10)))
            .Should().Be("/x//y");

    [Fact]
    public void Handles_several_placeholders_in_one_template()
        => TemplateEngine.Render("{{Id}}-{{Amount}}", TemplateBindings.From(new Invoice("A", 42)))
            .Should().Be("A-42");

    [Fact]
    public void Unterminated_placeholders_are_emitted_verbatim()
    {
        // Silently truncating would produce a subtly wrong URL, which is worse than an obvious one.
        TemplateEngine.Render("/x/{{ Id", TemplateBindings.From(new Invoice("A", 1)))
            .Should().Be("/x/{{ Id");
    }

    [Fact]
    public void Numbers_are_formatted_invariantly()
        => TemplateEngine.Render("{{ Amount }}", TemplateBindings.From(new Invoice("A", 1234.56m)))
            .Should().Be("1234.56");

    [Fact]
    public void Null_bindings_resolve_to_empty()
        => TemplateEngine.Render("{{ anything }}", TemplateBindings.Empty).Should().BeEmpty();

    [Fact]
    public void Rejects_null_arguments()
    {
        FluentActions.Invoking(() => TemplateEngine.Render(null!, TemplateBindings.Empty))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => TemplateEngine.Render("x", null!))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => TemplateBindings.Empty.Resolve(""))
            .Should().Throw<ArgumentException>();
    }
}

public class ApiCallExecutorTests
{
    private sealed record InvoiceDto(string Id, decimal Amount);

    private static ApiCallExecutor Build(
        ApiCallOptions options, Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StubHandler(responder);
        var client = new HttpClient(handler);
        return new ApiCallExecutor("api", options, () => client);
    }

    private static ApiCallOptions Options(string url = "https://erp.internal/invoices/1") => new()
    {
        UrlTemplate = url,
        AllowedHosts = ["erp.internal"]
    };

    [Fact]
    public async Task Successful_call_returns_the_status_and_raw_body()
    {
        ApiCallExecutor executor = Build(Options(), _ => Json(HttpStatusCode.OK, """{"id":"INV-1","amount":10}"""));

        ApiCallResult result = await executor.HandleAsync(new { }, new FakeWorkflowContext(), default);

        result.StatusCode.Should().Be(200);
        result.RawBody.Should().Contain("INV-1");
    }

    [Fact]
    public async Task Response_is_deserialised_when_a_type_is_configured()
    {
        ApiCallOptions options = Options();
        options.ResponseAs = typeof(InvoiceDto);

        ApiCallExecutor executor = Build(options, _ => Json(HttpStatusCode.OK, """{"id":"INV-7","amount":99.5}"""));

        ApiCallResult result = await executor.HandleAsync(new { }, new FakeWorkflowContext(), default);

        result.Body.Should().BeOfType<InvoiceDto>();
        ((InvoiceDto)result.Body!).Id.Should().Be("INV-7");
    }

    [Fact]
    public async Task Url_is_rendered_from_the_input()
    {
        Uri? requested = null;
        ApiCallExecutor executor = Build(
            Options("https://erp.internal/invoices/{{ Id }}"),
            request =>
            {
                requested = request.RequestUri;
                return Json(HttpStatusCode.OK, "{}");
            });

        await executor.HandleAsync(new { Id = "INV-42" }, new FakeWorkflowContext(), default);

        requested!.AbsolutePath.Should().Be("/invoices/INV-42");
    }

    [Fact]
    public async Task Non_success_status_throws_a_classifiable_failure()
    {
        ApiCallExecutor executor = Build(Options(), _ => Json(HttpStatusCode.ServiceUnavailable, "upstream down"));

        Func<Task> act = async () => await executor.HandleAsync(new { }, new FakeWorkflowContext(), default);

        var failure = (await act.Should().ThrowAsync<ApiCallFailureException>()).Which;
        failure.StatusCode.Should().Be(503);
        failure.BodyExcerpt.Should().Contain("upstream down");

        DefaultFailureClassifier.Instance.Classify(WorkflowFailure.Create("api", failure))
            .Should().Be(FailureDisposition.Retry);
    }

    [Fact]
    public async Task Client_errors_classify_as_dead_stop()
    {
        ApiCallExecutor executor = Build(Options(), _ => Json(HttpStatusCode.UnprocessableEntity, "bad"));

        Func<Task> act = async () => await executor.HandleAsync(new { }, new FakeWorkflowContext(), default);
        var failure = (await act.Should().ThrowAsync<ApiCallFailureException>()).Which;

        DefaultFailureClassifier.Instance.Classify(WorkflowFailure.Create("api", failure))
            .Should().Be(FailureDisposition.DeadStop);
    }

    [Fact]
    public async Task Custom_success_codes_are_honoured()
    {
        ApiCallOptions options = Options();
        options.SuccessCodes = [418];

        ApiCallExecutor executor = Build(options, _ => Json((HttpStatusCode)418, "teapot"));

        ApiCallResult result = await executor.HandleAsync(new { }, new FakeWorkflowContext(), default);
        result.StatusCode.Should().Be(418);
    }

    [Fact]
    public async Task An_idempotency_key_is_sent_by_default()
    {
        string? key = null;
        ApiCallExecutor executor = Build(Options(), request =>
        {
            key = request.Headers.TryGetValues("Idempotency-Key", out IEnumerable<string>? values)
                ? values.First() : null;
            return Json(HttpStatusCode.OK, "{}");
        });

        await executor.HandleAsync(new { }, new FakeWorkflowContext(), default);

        key.Should().NotBeNull();
        key.Should().Contain("api", "the key is scoped to instance, executor, and attempt");
    }

    [Fact]
    public async Task The_idempotency_key_can_be_disabled()
    {
        ApiCallOptions options = Options();
        options.SendIdempotencyKey = false;

        bool present = true;
        ApiCallExecutor executor = Build(options, request =>
        {
            present = request.Headers.Contains("Idempotency-Key");
            return Json(HttpStatusCode.OK, "{}");
        });

        await executor.HandleAsync(new { }, new FakeWorkflowContext(), default);
        present.Should().BeFalse();
    }

    [Fact]
    public async Task Headers_are_templated()
    {
        string? header = null;
        ApiCallOptions options = Options();
        options.Headers["X-Invoice"] = "{{ Id }}";

        ApiCallExecutor executor = Build(options, request =>
        {
            header = request.Headers.TryGetValues("X-Invoice", out IEnumerable<string>? v) ? v.First() : null;
            return Json(HttpStatusCode.OK, "{}");
        });

        await executor.HandleAsync(new { Id = "INV-3" }, new FakeWorkflowContext(), default);
        header.Should().Be("INV-3");
    }

    [Fact]
    public async Task Body_is_templated_and_sent_as_json()
    {
        string? body = null;
        ApiCallOptions options = Options();
        options.Method = HttpMethod.Post;
        options.BodyTemplate = """{"invoice":"{{ Id }}"}""";

        ApiCallExecutor executor = Build(options, request =>
        {
            body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json(HttpStatusCode.OK, "{}");
        });

        await executor.HandleAsync(new { Id = "INV-5" }, new FakeWorkflowContext(), default);

        body.Should().Be("""{"invoice":"INV-5"}""");
    }

    [Fact]
    public async Task Egress_allowlist_blocks_unlisted_hosts()
    {
        ApiCallOptions options = Options("https://evil.example.com/x");

        ApiCallExecutor executor = Build(options, _ => Json(HttpStatusCode.OK, "{}"));

        Func<Task> act = async () => await executor.HandleAsync(new { }, new FakeWorkflowContext(), default);
        await act.Should().ThrowAsync<Abacus.Run.Core.EgressBlockedException>();
    }

    [Fact]
    public async Task Retry_after_is_surfaced_for_the_classifier()
    {
        ApiCallExecutor executor = Build(Options(), _ =>
        {
            HttpResponseMessage response = Json(HttpStatusCode.TooManyRequests, "slow down");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        });

        Func<Task> act = async () => await executor.HandleAsync(new { }, new FakeWorkflowContext(), default);
        var failure = (await act.Should().ThrowAsync<ApiCallFailureException>()).Which;

        failure.RetryAfter.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Body_excerpts_are_truncated()
        => ApiCallExecutor.Truncate(new string('x', 5000), 2048).Should().HaveLength(2048);

    [Fact]
    public void Short_bodies_are_not_truncated()
        => ApiCallExecutor.Truncate("short", 2048).Should().Be("short");

    [Fact]
    public void Metadata_describes_the_node()
    {
        ApiCallOptions options = Options();
        options.Method = HttpMethod.Post;
        var executor = new ApiCallExecutor("api", options, () => new HttpClient());

        executor.Metadata["node.kind"].Should().Be("api-call");
        executor.Metadata["http.method"].Should().Be("POST");
    }

    [Fact]
    public void Constructor_rejects_null_dependencies()
    {
        FluentActions.Invoking(() => new ApiCallExecutor("api", null!, () => new HttpClient()))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new ApiCallExecutor("api", Options(), null!))
            .Should().Throw<ArgumentNullException>();
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }
}

public class StructuredOutputParserTests
{
    private sealed record Classification(string Category, double Confidence);

    [Fact]
    public void Parses_plain_json()
    {
        var result = (Classification)StructuredOutputParser.Parse(
            """{"category":"invoice","confidence":0.92}""", typeof(Classification), 2);

        result.Category.Should().Be("invoice");
        result.Confidence.Should().Be(0.92);
    }

    [Fact]
    public void Unwraps_fenced_code_blocks()
    {
        var result = (Classification)StructuredOutputParser.Parse(
            "```json\n{\"category\":\"receipt\",\"confidence\":0.5}\n```", typeof(Classification), 2);

        result.Category.Should().Be("receipt");
    }

    [Fact]
    public void Extracts_json_embedded_in_prose()
    {
        var result = (Classification)StructuredOutputParser.Parse(
            "Here is the result: {\"category\":\"po\",\"confidence\":0.7} — hope that helps!",
            typeof(Classification), 2);

        result.Category.Should().Be("po");
    }

    [Fact]
    public void Throws_when_the_model_cannot_produce_the_schema()
    {
        // A model that fails twice will not succeed on a host-level retry, so this dead-stops.
        Action act = () => StructuredOutputParser.Parse("no json at all", typeof(Classification), 2);

        var thrown = act.Should().Throw<StructuredOutputException>().Which;
        thrown.ExpectedType.Should().Be<Classification>();

        DefaultFailureClassifier.Instance.Classify(WorkflowFailure.Create("llm", thrown))
            .Should().Be(FailureDisposition.DeadStop);
    }

    [Fact]
    public void Throws_on_a_json_null_result()
        => FluentActions.Invoking(() => StructuredOutputParser.Parse("null", typeof(Classification), 1))
            .Should().Throw<StructuredOutputException>();

    [Theory]
    [InlineData("```\n{\"a\":1}\n```", "{\"a\":1}")]
    [InlineData("```json\n{\"a\":1}\n```", "{\"a\":1}")]
    [InlineData("{\"a\":1}", "{\"a\":1}")]
    [InlineData("  {\"a\":1}  ", "{\"a\":1}")]
    public void Unwrap_strips_fences_and_whitespace(string input, string expected)
        => StructuredOutputParser.Unwrap(input).Should().Be(expected);

    [Fact]
    public void Unwrap_tolerates_a_fence_without_a_newline()
        => StructuredOutputParser.Unwrap("```json").Should().Be("```json");

    [Theory]
    [InlineData("prefix {\"a\":{\"b\":1}} suffix", "{\"a\":{\"b\":1}}")]
    [InlineData("prefix [1,2,3] suffix", "[1,2,3]")]
    [InlineData("no json here", "no json here")]
    [InlineData("{\"unbalanced\":1", "{\"unbalanced\":1")]
    public void Extract_finds_the_first_balanced_structure(string input, string expected)
        => StructuredOutputParser.ExtractFirstJsonObject(input).Should().Be(expected);

    [Fact]
    public void Rejects_a_null_target_type()
        => FluentActions.Invoking(() => StructuredOutputParser.Parse("{}", null!, 1))
            .Should().Throw<ArgumentNullException>();
}

public class SupportingExecutorTests
{
    [Fact]
    public async Task Transform_projects_its_input()
    {
        var executor = new TransformExecutor<Payload, Outcome>("t", p => new Outcome(p.Value.ToUpperInvariant()));

        Outcome result = await executor.HandleAsync(new Payload("abc"), new FakeWorkflowContext(), default);

        result.Value.Should().Be("ABC");
        executor.Metadata["node.kind"].Should().Be("transform");
    }

    [Fact]
    public async Task Delegate_executor_runs_a_sync_handler()
    {
        var executor = new DelegateExecutor<Payload, Outcome>("d", p => new Outcome($"sync:{p.Value}"));

        (await executor.HandleAsync(new Payload("x"), new FakeWorkflowContext(), default))
            .Value.Should().Be("sync:x");
    }

    [Fact]
    public async Task Delegate_executor_runs_an_async_handler_with_context()
    {
        var executor = new DelegateExecutor<Payload, Outcome>("d", async (p, ctx, ct) =>
        {
            await ctx.QueueStateUpdateAsync("seen", p.Value, ct);
            return new Outcome($"async:{p.Value}");
        });

        var context = new FakeWorkflowContext();
        Outcome result = await executor.HandleAsync(new Payload("y"), context, default);

        result.Value.Should().Be("async:y");
        (await context.ReadStateAsync<string>("seen", default)).Should().Be("y");
    }

    [Fact]
    public async Task Fan_in_aggregates_its_inputs()
    {
        var executor = new FanInExecutor<int, Outcome>("f", items => new Outcome(items.Sum().ToString()));

        (await executor.HandleAsync([1, 2, 3], new FakeWorkflowContext(), default)).Value.Should().Be("6");
    }

    [Fact]
    public async Task Fan_in_tolerates_a_null_batch()
    {
        var executor = new FanInExecutor<int, Outcome>("f", items => new Outcome(items.Count.ToString()));

        (await executor.HandleAsync(null!, new FakeWorkflowContext(), default)).Value.Should().Be("0");
    }

    [Fact]
    public async Task Human_approval_executor_passes_its_input_through()
    {
        var executor = new HumanApprovalExecutor<Payload>("h");
        var input = new Payload("keep");

        (await executor.HandleAsync(input, new FakeWorkflowContext(), default)).Should().BeSameAs(input);
    }

    [Fact]
    public async Task Delay_executor_schedules_a_timer_without_blocking()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-30T10:00:00Z"));
        var timers = new RecordingTimerService();
        var executor = new DelayExecutor("delay", TimeSpan.FromHours(2), timers, clock);
        var context = new FakeWorkflowContext();

        TimerElapsed result = await executor.HandleAsync(new object(), context, default);

        result.WakeAt.Should().Be(clock.GetUtcNow().AddHours(2));
        timers.Scheduled.Should().ContainSingle();
        (await context.ReadStateAsync<DateTimeOffset>("delay.wakeAt", default))
            .Should().Be(clock.GetUtcNow().AddHours(2));
    }

    [Fact]
    public void Executors_reject_invalid_construction()
    {
        FluentActions.Invoking(() => new TransformExecutor<Payload, Outcome>("t", null!))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new FanInExecutor<int, Outcome>("f", null!))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new DelayExecutor("d", TimeSpan.FromSeconds(-1), new RecordingTimerService()))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new DelayExecutor("d", TimeSpan.FromSeconds(1), null!))
            .Should().Throw<ArgumentNullException>();
    }

    private sealed class RecordingTimerService : ITimerService
    {
        public List<(string InstanceId, string ExecutorId, DateTimeOffset WakeAt)> Scheduled { get; } = [];

        public ValueTask ScheduleAsync(
            string instanceId, string executorId, DateTimeOffset wakeAt, CancellationToken cancellationToken)
        {
            Scheduled.Add((instanceId, executorId, wakeAt));
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<(string InstanceId, string ExecutorId)>> ClaimDueAsync(
            DateTimeOffset now, int max, CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<(string, string)>>(
                Scheduled.Where(s => s.WakeAt <= now).Select(s => (s.InstanceId, s.ExecutorId)).ToArray());
    }
}

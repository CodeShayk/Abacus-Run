using System.Runtime.CompilerServices;
using Abacus.Run.Abstractions;
using Abacus.Run.Executors;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace Abacus.Run.UnitTests;

public class LlmExecutorTests
{
    private sealed record Classification(string Category, double Confidence);

    private sealed record Document(string Text);

    private static LlmExecutor Build(LlmOptions options, IChatClient client)
        => new("classify", options, _ => client);

    private static LlmOptions Options(string userTemplate = "Classify: {{ Text }}") => new()
    {
        Model = "test-model-v1",
        SystemPrompt = "You are a classifier.",
        UserTemplate = userTemplate
    };

    [Fact]
    public async Task Returns_the_completion_text_and_usage()
    {
        var client = new FakeChatClient("invoice", inputTokens: 120, outputTokens: 8);
        LlmExecutor executor = Build(Options(), client);

        LlmResult result = await executor.HandleAsync(new Document("an invoice"), new FakeWorkflowContext(), default);

        result.Text.Should().Be("invoice");
        result.Value.Should().Be("invoice");
        result.InputTokens.Should().Be(120);
        result.OutputTokens.Should().Be(8);
        result.ModelId.Should().Be("test-model-v1");
    }

    [Fact]
    public async Task Renders_the_user_template_from_the_input()
    {
        var client = new FakeChatClient("ok");
        LlmExecutor executor = Build(Options("Classify: {{ Text }}"), client);

        await executor.HandleAsync(new Document("a receipt"), new FakeWorkflowContext(), default);

        client.LastMessages.Should().HaveCount(2);
        client.LastMessages[0].Role.Should().Be(ChatRole.System);
        client.LastMessages[1].Text.Should().Be("Classify: a receipt");
    }

    [Fact]
    public async Task Omits_the_system_message_when_no_prompt_is_configured()
    {
        LlmOptions options = Options();
        options.SystemPrompt = null;

        var client = new FakeChatClient("ok");
        await Build(options, client).HandleAsync(new Document("x"), new FakeWorkflowContext(), default);

        client.LastMessages.Should().ContainSingle();
        client.LastMessages[0].Role.Should().Be(ChatRole.User);
    }

    [Fact]
    public async Task Passes_model_temperature_and_token_budget_through()
    {
        LlmOptions options = Options();
        options.Temperature = 0.2f;
        options.MaxTokens = 512;

        var client = new FakeChatClient("ok");
        await Build(options, client).HandleAsync(new Document("x"), new FakeWorkflowContext(), default);

        client.LastOptions!.ModelId.Should().Be("test-model-v1");
        client.LastOptions.Temperature.Should().Be(0.2f);
        client.LastOptions.MaxOutputTokens.Should().Be(512);
    }

    [Fact]
    public async Task Parses_structured_output_into_the_declared_type()
    {
        LlmOptions options = Options();
        options.StructuredOutput = typeof(Classification);

        var client = new FakeChatClient("""{"category":"invoice","confidence":0.93}""");

        LlmResult result = await Build(options, client)
            .HandleAsync(new Document("x"), new FakeWorkflowContext(), default);

        result.Value.Should().BeOfType<Classification>();
        ((Classification)result.Value!).Category.Should().Be("invoice");
    }

    [Fact]
    public async Task Unparseable_structured_output_throws_and_classifies_as_dead_stop()
    {
        LlmOptions options = Options();
        options.StructuredOutput = typeof(Classification);

        var client = new FakeChatClient("I cannot produce JSON for this.");

        Func<Task> act = async () => await Build(options, client)
            .HandleAsync(new Document("x"), new FakeWorkflowContext(), default);

        var thrown = (await act.Should().ThrowAsync<StructuredOutputException>()).Which;

        DefaultFailureClassifier.Instance.Classify(WorkflowFailure.Create("classify", thrown))
            .Should().Be(FailureDisposition.DeadStop);
    }

    [Fact]
    public async Task Streaming_emits_a_delta_event_per_chunk()
    {
        LlmOptions options = Options();
        options.StreamDeltas = true;

        var client = new FakeChatClient("unused", streamChunks: ["The ", "invoice ", "is valid."]);
        var context = new FakeWorkflowContext();

        LlmResult result = await Build(options, client).HandleAsync(new Document("x"), context, default);

        context.Events.OfType<LlmDeltaWorkflowEvent>().Select(e => e.Delta)
            .Should().Equal("The ", "invoice ", "is valid.");

        result.Text.Should().Be("The invoice is valid.");
    }

    [Fact]
    public async Task Streaming_deltas_carry_the_executor_id()
    {
        LlmOptions options = Options();
        options.StreamDeltas = true;

        var client = new FakeChatClient("unused", streamChunks: ["a"]);
        var context = new FakeWorkflowContext();

        await Build(options, client).HandleAsync(new Document("x"), context, default);

        context.Events.OfType<LlmDeltaWorkflowEvent>().Single().ExecutorId.Should().Be("classify");
    }

    [Fact]
    public async Task Provider_failures_surface_for_the_classifier()
    {
        var client = new FakeChatClient("unused", throws: new LlmRateLimitException());

        Func<Task> act = async () => await Build(Options(), client)
            .HandleAsync(new Document("x"), new FakeWorkflowContext(), default);

        var thrown = (await act.Should().ThrowAsync<LlmRateLimitException>()).Which;

        DefaultFailureClassifier.Instance.Classify(WorkflowFailure.Create("classify", thrown))
            .Should().Be(FailureDisposition.Retry);
    }

    [Fact]
    public void Metadata_describes_the_model_and_prompt_version()
    {
        LlmOptions options = Options();
        options.PromptVersion = "v3";

        var executor = new LlmExecutor("classify", options, _ => new FakeChatClient("x"));

        executor.Metadata["node.kind"].Should().Be("llm");
        executor.Metadata["llm.model"].Should().Be("test-model-v1");
        executor.Metadata[Abacus.Run.Abstractions.Middleware.MiddlewareContextKeys.PromptVersion].Should().Be("v3");
    }

    [Fact]
    public void Constructor_rejects_null_dependencies()
    {
        FluentActions.Invoking(() => new LlmExecutor("x", null!, _ => new FakeChatClient("x")))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new LlmExecutor("x", Options(), null!))
            .Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void The_delta_event_exposes_its_payload()
    {
        var evt = new LlmDeltaWorkflowEvent("classify", "chunk");
        evt.ExecutorId.Should().Be("classify");
        evt.Delta.Should().Be("chunk");
        evt.Data.Should().Be("chunk");
    }

    private sealed class FakeChatClient : IChatClient
    {
        private readonly string _response;
        private readonly string[]? _streamChunks;
        private readonly Exception? _throws;
        private readonly long _inputTokens;
        private readonly long _outputTokens;

        public FakeChatClient(
            string response,
            string[]? streamChunks = null,
            Exception? throws = null,
            long inputTokens = 0,
            long outputTokens = 0)
        {
            _response = response;
            _streamChunks = streamChunks;
            _throws = throws;
            _inputTokens = inputTokens;
            _outputTokens = outputTokens;
        }

        public IList<ChatMessage> LastMessages { get; private set; } = [];
        public ChatOptions? LastOptions { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastMessages = messages.ToList();
            LastOptions = options;

            if (_throws is not null)
            {
                throw _throws;
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _response))
            {
                ModelId = options?.ModelId,
                FinishReason = ChatFinishReason.Stop,
                Usage = new UsageDetails { InputTokenCount = _inputTokens, OutputTokenCount = _outputTokens }
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastMessages = messages.ToList();
            LastOptions = options;

            if (_throws is not null)
            {
                throw _throws;
            }

            foreach (string chunk in _streamChunks ?? [_response])
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, chunk) { ModelId = options?.ModelId };
                await Task.Yield();
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}

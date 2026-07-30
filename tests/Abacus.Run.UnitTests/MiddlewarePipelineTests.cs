using Abacus.Run.Abstractions;
using Abacus.Run.Abstractions.Middleware;
using Abacus.Run.Core;
using FluentAssertions;
using Xunit;

namespace Abacus.Run.UnitTests;

public class MiddlewarePipelineTests
{
    private static ExecutorDelegate Terminal(List<string> log, Action<ExecutorInvocationContext>? act = null)
        => (ctx, _) =>
        {
            log.Add("terminal");
            act?.Invoke(ctx);
            ctx.Output ??= new Outcome("terminal");
            return ValueTask.CompletedTask;
        };

    [Fact]
    public async Task Executes_middleware_in_nested_order()
    {
        var log = new List<string>();
        var factory = new MiddlewarePipelineFactory(
        [
            new RecordingExecutorMiddleware(log, "outer", order: -10),
            new RecordingExecutorMiddleware(log, "inner", order: 10)
        ]);

        ExecutorDelegate pipeline = factory.BuildExecutorPipeline(TestFactory.Descriptor(), Terminal(log));
        await pipeline(TestFactory.Invocation(), default);

        log.Should().Equal("outer:before", "inner:before", "terminal", "inner:after", "outer:after");
    }

    [Fact]
    public async Task Registration_order_breaks_ties_at_equal_order()
    {
        var log = new List<string>();
        var factory = new MiddlewarePipelineFactory(
        [
            new RecordingExecutorMiddleware(log, "first"),
            new RecordingExecutorMiddleware(log, "second")
        ]);

        await factory.BuildExecutorPipeline(TestFactory.Descriptor(), Terminal(log))(TestFactory.Invocation(), default);

        log.Should().Equal("first:before", "second:before", "terminal", "second:after", "first:after");
    }

    [Fact]
    public async Task Short_circuit_skips_the_terminal_and_everything_inside()
    {
        var log = new List<string>();
        var factory = new MiddlewarePipelineFactory(
        [
            new RecordingExecutorMiddleware(log, "gatekeeper", order: -10, shortCircuit: true),
            new RecordingExecutorMiddleware(log, "never", order: 10)
        ]);

        await factory.BuildExecutorPipeline(TestFactory.Descriptor(), Terminal(log))(TestFactory.Invocation(), default);

        log.Should().Equal("gatekeeper:before", "gatekeeper:after");
        log.Should().NotContain("terminal");
        log.Should().NotContain("never:before");
    }

    [Fact]
    public async Task AppliesTo_filters_middleware_per_descriptor()
    {
        var log = new List<string>();
        var factory = new MiddlewarePipelineFactory(
        [
            new RecordingExecutorMiddleware(log, "llm-only", appliesTo: d => d.ExecutorId == "llm-node"),
            new RecordingExecutorMiddleware(log, "always")
        ]);

        await factory.BuildExecutorPipeline(TestFactory.Descriptor("http-node"), Terminal(log))(TestFactory.Invocation(), default);

        log.Should().NotContain("llm-only:before");
        log.Should().Contain("always:before");
    }

    [Fact]
    public async Task Exception_from_middleware_propagates_to_the_caller()
    {
        var factory = new MiddlewarePipelineFactory([new ThrowingMiddleware()]);

        Func<Task> act = async () =>
            await factory.BuildExecutorPipeline(TestFactory.Descriptor(), Terminal([]))(TestFactory.Invocation(), default);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Be("middleware exploded");
    }

    [Fact]
    public async Task Applicability_selection_is_cached_per_workflow_version_executor()
    {
        var probe = new CountingApplicabilityMiddleware();
        var factory = new MiddlewarePipelineFactory([probe]);
        var log = new List<string>();

        for (int i = 0; i < 5; i++)
        {
            await factory.BuildExecutorPipeline(TestFactory.Descriptor("e1"), Terminal(log))(TestFactory.Invocation(), default);
        }

        probe.AppliesToCalls.Should().Be(1, "applicability depends only on the descriptor, so it is cached");
        factory.CachedPipelineCount.Should().Be(1);
    }

    [Fact]
    public async Task Distinct_executors_get_distinct_cache_entries()
    {
        var factory = new MiddlewarePipelineFactory([new CountingApplicabilityMiddleware()]);
        var log = new List<string>();

        await factory.BuildExecutorPipeline(TestFactory.Descriptor("a"), Terminal(log))(TestFactory.Invocation(), default);
        await factory.BuildExecutorPipeline(TestFactory.Descriptor("b"), Terminal(log))(TestFactory.Invocation(), default);

        factory.CachedPipelineCount.Should().Be(2);
    }

    [Fact]
    public async Task Empty_pipeline_invokes_the_terminal_directly()
    {
        var log = new List<string>();
        var factory = new MiddlewarePipelineFactory();

        await factory.BuildExecutorPipeline(TestFactory.Descriptor(), Terminal(log))(TestFactory.Invocation(), default);

        log.Should().Equal("terminal");
        factory.ExecutorMiddlewareCount.Should().Be(0);
        factory.WorkflowMiddlewareCount.Should().Be(0);
    }

    [Fact]
    public async Task Workflow_pipeline_nests_in_order()
    {
        var log = new List<string>();
        var factory = new MiddlewarePipelineFactory(workflowMiddleware:
        [
            new RecordingWorkflowMiddleware(log, "outer", order: -5),
            new RecordingWorkflowMiddleware(log, "inner", order: 5)
        ]);

        WorkflowDelegate pipeline = factory.BuildWorkflowPipeline((_, _) =>
        {
            log.Add("run");
            return ValueTask.CompletedTask;
        });

        await pipeline(TestFactory.WorkflowInvocation(), default);

        log.Should().Equal("outer:before", "inner:before", "run", "inner:after", "outer:after");
    }

    [Fact]
    public void Build_rejects_null_arguments()
    {
        var factory = new MiddlewarePipelineFactory();

        factory.Invoking(f => f.BuildExecutorPipeline(null!, (_, _) => ValueTask.CompletedTask))
            .Should().Throw<ArgumentNullException>();

        factory.Invoking(f => f.BuildExecutorPipeline(TestFactory.Descriptor(), null!))
            .Should().Throw<ArgumentNullException>();

        factory.Invoking(f => f.BuildWorkflowPipeline(null!))
            .Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Counts_reflect_registered_middleware()
    {
        var log = new List<string>();
        var factory = new MiddlewarePipelineFactory(
            [new RecordingExecutorMiddleware(log, "a"), new RecordingExecutorMiddleware(log, "b")],
            [new RecordingWorkflowMiddleware(log, "w")]);

        factory.ExecutorMiddlewareCount.Should().Be(2);
        factory.WorkflowMiddlewareCount.Should().Be(1);
    }

    private sealed class ThrowingMiddleware : IExecutorMiddleware
    {
        public ValueTask InvokeAsync(ExecutorInvocationContext context, ExecutorDelegate next, CancellationToken cancellationToken)
            => throw new InvalidOperationException("middleware exploded");
    }

    private sealed class CountingApplicabilityMiddleware : IExecutorMiddleware
    {
        public int AppliesToCalls { get; private set; }

        public bool AppliesTo(ExecutorDescriptor descriptor)
        {
            AppliesToCalls++;
            return true;
        }

        public ValueTask InvokeAsync(ExecutorInvocationContext context, ExecutorDelegate next, CancellationToken cancellationToken)
            => next(context, cancellationToken);
    }
}

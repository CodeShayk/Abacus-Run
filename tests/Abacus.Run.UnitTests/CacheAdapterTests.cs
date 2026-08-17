using Abacus.Run.Abstractions;
using Abacus.Run.Api;
using Abacus.Run.Messaging;
using Abacus.Run.Notifications;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Abacus.Run.UnitTests;

public class InMemoryCacheStoreTests
{
    [Fact]
    public async Task A_missing_key_is_absent_rather_than_an_error()
        => (await new InMemoryCacheStore().GetAsync<string>("nope", default)).Should().BeNull();

    [Fact]
    public async Task A_stored_value_reads_back()
    {
        var store = new InMemoryCacheStore();
        await store.SetAsync("k", new { Name = "abacus" }, null, default);

        (await store.GetAsync<object>("k", default)).Should().NotBeNull();
    }

    [Fact]
    public async Task A_value_expires_once_its_ttl_has_passed()
    {
        var clock = new FakeTimeProvider();
        var store = new InMemoryCacheStore(clock);

        await store.SetAsync("k", "v", TimeSpan.FromMinutes(5), default);
        (await store.GetAsync<string>("k", default)).Should().Be("v");

        clock.Advance(TimeSpan.FromMinutes(6));

        (await store.GetAsync<string>("k", default)).Should()
            .BeNull("an expired entry is gone even though nothing swept it");
    }

    [Fact]
    public async Task No_ttl_means_the_value_stays()
    {
        var clock = new FakeTimeProvider();
        var store = new InMemoryCacheStore(clock);

        await store.SetAsync("k", "v", null, default);
        clock.Advance(TimeSpan.FromDays(30));

        (await store.GetAsync<string>("k", default)).Should().Be("v");
    }

    [Fact]
    public async Task GetOrCreate_computes_once_and_then_reads()
    {
        var store = new InMemoryCacheStore();
        int calls = 0;

        ValueTask<string> Factory(CancellationToken _)
        {
            calls++;
            return ValueTask.FromResult("computed");
        }

        (await store.GetOrCreateAsync("k", Factory, null, default)).Should().Be("computed");
        (await store.GetOrCreateAsync("k", Factory, null, default)).Should().Be("computed");

        calls.Should().Be(1);
    }

    [Fact]
    public async Task Remove_reports_whether_anything_was_there()
    {
        var store = new InMemoryCacheStore();
        await store.SetAsync("k", "v", null, default);

        (await store.RemoveAsync("k", default)).Should().BeTrue();
        (await store.RemoveAsync("k", default)).Should().BeFalse();
    }
}

/// <summary>
/// The adapter is the substitution unit, and these pin the reason: swapping it must move every
/// cache-shaped capability together, so a deployment cannot end up caching in one technology while
/// streaming through another.
/// </summary>
public class CacheAdapterSubstitutionTests
{
    private sealed class FakeCacheAdapter : ICacheAdapter
    {
        public string Technology => "Fake";
        public bool IsDistributed => true;
        public ICacheStore Store { get; } = new InMemoryCacheStore();
        public INotificationBus NotificationBackplane { get; } = new InMemoryNotificationBus();
    }

    private static ServiceProvider Build(Action<IServiceCollection>? substitute = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkflowHost();
        substitute?.Invoke(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task The_default_adapter_is_in_memory_and_says_it_is_not_distributed()
    {
        await using ServiceProvider provider = Build();

        ICacheAdapter adapter = provider.GetRequiredService<ICacheAdapter>();

        adapter.Technology.Should().Be("InMemory");
        adapter.IsDistributed.Should()
            .BeFalse("a multi-replica deployment needs to be told this, not left to discover it");
    }

    [Fact]
    public async Task The_backplane_and_the_store_both_come_from_the_registered_adapter()
    {
        await using ServiceProvider provider = Build();

        ICacheAdapter adapter = provider.GetRequiredService<ICacheAdapter>();

        provider.GetRequiredService<INotificationBus>().Should().BeSameAs(adapter.NotificationBackplane);
        provider.GetRequiredService<ICacheStore>().Should().BeSameAs(adapter.Store);
    }

    [Fact]
    public async Task Substituting_the_adapter_moves_the_cache_and_the_backplane_together()
    {
        await using ServiceProvider provider = Build(services =>
        {
            services.RemoveAll<ICacheAdapter>();
            services.AddSingleton<ICacheAdapter, FakeCacheAdapter>();
        });

        ICacheAdapter adapter = provider.GetRequiredService<ICacheAdapter>();
        adapter.Technology.Should().Be("Fake");

        provider.GetRequiredService<INotificationBus>().Should().BeSameAs(adapter.NotificationBackplane,
            "the SSE backplane follows the adapter");
        provider.GetRequiredService<ICacheStore>().Should().BeSameAs(adapter.Store,
            "general caching follows the same adapter — there is no seam between them to get wrong");
    }

    [Fact]
    public async Task Sse_streaming_works_with_no_infrastructure_configured()
    {
        await using ServiceProvider provider = Build();

        provider.GetService<INotificationBus>().Should()
            .NotBeNull("a host with no cache configured must still stream, using the in-memory cache");
    }
}

/// <summary>
/// The messaging adapter mirrors the cache adapter, and for the same reason: a future
/// Abacus.Adapters.Messaging.AWS should slot in without the framework changing to accommodate it.
/// </summary>
public class MessagingAdapterSubstitutionTests
{
    private sealed class FakeMessagingAdapter : IMessagingAdapter
    {
        public string Technology => "Fake";
        public bool IsDistributed => true;
        public IDomainEventBroker DomainEventBroker { get; } = new InProcessDomainEventBroker();
        public IControlChannel ControlChannel { get; } = new InProcessControlChannel();
    }

    private static ServiceProvider Build(Action<IServiceCollection>? substitute = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkflowHost();
        substitute?.Invoke(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task The_default_adapter_is_in_process_and_says_it_is_not_distributed()
    {
        await using ServiceProvider provider = Build();

        IMessagingAdapter adapter = provider.GetRequiredService<IMessagingAdapter>();

        adapter.Technology.Should().Be("InProcess");
        adapter.IsDistributed.Should()
            .BeFalse("a multi-service deployment needs to be told this, not left to discover it");
    }

    [Fact]
    public async Task The_broker_and_the_control_channel_both_come_from_the_registered_adapter()
    {
        await using ServiceProvider provider = Build();

        IMessagingAdapter adapter = provider.GetRequiredService<IMessagingAdapter>();

        provider.GetRequiredService<IDomainEventBroker>().Should().BeSameAs(adapter.DomainEventBroker);
        provider.GetRequiredService<IControlChannel>().Should().BeSameAs(adapter.ControlChannel);
    }

    [Fact]
    public async Task Substituting_the_adapter_moves_every_messaging_capability_together()
    {
        await using ServiceProvider provider = Build(services =>
        {
            services.RemoveAll<IMessagingAdapter>();
            services.AddSingleton<IMessagingAdapter, FakeMessagingAdapter>();
        });

        IMessagingAdapter adapter = provider.GetRequiredService<IMessagingAdapter>();
        adapter.Technology.Should().Be("Fake");

        provider.GetRequiredService<IDomainEventBroker>().Should().BeSameAs(adapter.DomainEventBroker);
        provider.GetRequiredService<IControlChannel>().Should().BeSameAs(adapter.ControlChannel,
            "control signalling follows the same adapter — one transport, not two");
    }

    [Fact]
    public async Task Cache_and_messaging_are_chosen_independently()
    {
        await using ServiceProvider provider = Build(services =>
        {
            services.RemoveAll<IMessagingAdapter>();
            services.AddSingleton<IMessagingAdapter, FakeMessagingAdapter>();
        });

        provider.GetRequiredService<IMessagingAdapter>().Technology.Should().Be("Fake");
        provider.GetRequiredService<ICacheAdapter>().Technology.Should()
            .Be("InMemory", "swapping the transport must not drag the cache along with it");
    }
}

public class InProcessControlChannelTests
{
    private static ControlSignal Cancel(string instanceId)
        => new(instanceId, ControlActions.Cancel, "operator asked", DateTimeOffset.UtcNow);

    [Fact]
    public async Task A_signal_reaches_a_subscriber_for_that_instance()
    {
        var channel = new InProcessControlChannel();
        var signalled = new TaskCompletionSource<ControlSignal>();

        await using IAsyncDisposable handle = await channel.SubscribeAsync(
            "i1", s => { signalled.TrySetResult(s); return Task.CompletedTask; }, default);

        await channel.PublishAsync(Cancel("i1"), default);

        ControlSignal received = await signalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        received.Action.Should().Be(ControlActions.Cancel);
        received.Reason.Should().Be("operator asked");
    }

    [Fact]
    public async Task A_signal_for_another_instance_is_not_delivered()
    {
        var channel = new InProcessControlChannel();
        int calls = 0;

        await using IAsyncDisposable handle = await channel.SubscribeAsync(
            "i1", _ => { Interlocked.Increment(ref calls); return Task.CompletedTask; }, default);

        await channel.PublishAsync(Cancel("i2"), default);
        await Task.Delay(200);

        calls.Should().Be(0);
    }

    [Fact]
    public async Task Unsubscribing_stops_delivery()
    {
        var channel = new InProcessControlChannel();
        int calls = 0;

        IAsyncDisposable handle = await channel.SubscribeAsync(
            "i1", _ => { Interlocked.Increment(ref calls); return Task.CompletedTask; }, default);

        await handle.DisposeAsync();
        await channel.PublishAsync(Cancel("i1"), default);
        await Task.Delay(200);

        calls.Should().Be(0);
    }

    [Fact]
    public async Task A_throwing_handler_does_not_fail_the_publisher()
    {
        var channel = new InProcessControlChannel();

        await using IAsyncDisposable handle = await channel.SubscribeAsync(
            "i1", _ => throw new InvalidOperationException("boom"), default);

        Func<Task> publish = () => channel.PublishAsync(Cancel("i1"), default).AsTask();

        await publish.Should().NotThrowAsync(
            "the instruction is already recorded on the instance row; a failed reaction cannot undo it");
    }
}

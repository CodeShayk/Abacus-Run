using Abacus.Adapters.Cache.Redis;
using Abacus.Run.Abstractions;
using Abacus.Run.Notifications;
using FluentAssertions;
using StackExchange.Redis;
using Xunit;

namespace Abacus.Run.BrokerTests;

/// <summary>
/// The contract, run against the in-process implementation. No Docker, so this is the suite an
/// adapter author can lean on while developing — and the one that proves the contract itself is
/// satisfiable before any network is involved.
/// </summary>
public class InMemoryCacheStoreConformanceTests : CacheStoreContract
{
    private static ICacheStore Store() => new InMemoryCacheStore();

    [Fact] public Task Missing_key() => A_missing_key_reads_as_absent(Store());
    [Fact] public Task Round_trip() => A_value_round_trips(Store());
    [Fact] public Task Complex_round_trip() => A_complex_value_round_trips(Store());
    [Fact] public Task Replace() => Setting_the_same_key_replaces_the_value(Store());
    [Fact] public Task Remove() => Remove_reports_whether_anything_was_there(Store());
    [Fact] public Task No_ttl() => A_value_with_no_ttl_persists(Store());
    [Fact] public Task Ttl_expiry() => A_value_expires_on_its_ttl(Store());
    [Fact] public Task Get_or_create() => GetOrCreate_computes_on_a_miss_and_reads_on_a_hit(Store());
    [Fact] public Task Key_independence() => Keys_are_independent(Store());

    /// <summary>
    /// In-memory only: the entry is evicted lazily on read rather than by a sweeper, which a real
    /// clock cannot demonstrate and a distributed store does differently.
    /// </summary>
    [Fact]
    public async Task Expiry_is_lazy_and_needs_no_sweeper()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var store = new InMemoryCacheStore(clock);

        await store.SetAsync("k", "v", TimeSpan.FromMinutes(5), default);
        clock.Advance(TimeSpan.FromMinutes(6));

        (await store.GetAsync<string>("k", default)).Should()
            .BeNull("nothing swept it; the read is what evicts it");
    }
}

/// <summary>
/// The same contract against Redis. Any disagreement between the two implementations shows up here
/// rather than in production, which is the whole reason the contract is shared.
/// </summary>
[Collection(RedisCollection.Name)]
public class RedisCacheStoreConformanceTests : CacheStoreContract, IAsyncLifetime
{
    private readonly RedisFixture _redis;
    private IConnectionMultiplexer? _connection;

    public RedisCacheStoreConformanceTests(RedisFixture redis) => _redis = redis;

    public async Task InitializeAsync()
    {
        if (_redis.Available)
        {
            _connection = await ConnectionMultiplexer.ConnectAsync(_redis.ConnectionString);
        }
    }

    public async Task DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }

    private ICacheStore Store() => new RedisCacheStore(_connection!);

    [RequiresDockerFact] public Task Missing_key() => A_missing_key_reads_as_absent(Store());
    [RequiresDockerFact] public Task Round_trip() => A_value_round_trips(Store());
    [RequiresDockerFact] public Task Complex_round_trip() => A_complex_value_round_trips(Store());
    [RequiresDockerFact] public Task Replace() => Setting_the_same_key_replaces_the_value(Store());
    [RequiresDockerFact] public Task Remove() => Remove_reports_whether_anything_was_there(Store());
    [RequiresDockerFact] public Task No_ttl() => A_value_with_no_ttl_persists(Store());
    [RequiresDockerFact] public Task Ttl_expiry() => A_value_expires_on_its_ttl(Store());
    [RequiresDockerFact] public Task Get_or_create() => GetOrCreate_computes_on_a_miss_and_reads_on_a_hit(Store());
    [RequiresDockerFact] public Task Key_independence() => Keys_are_independent(Store());

    /// <summary>
    /// Redis only: two stores with different prefixes share one server without colliding, which is
    /// what lets several applications point at the same instance.
    /// </summary>
    [RequiresDockerFact]
    public async Task Prefixes_isolate_two_applications()
    {
        var first = new RedisCacheStore(_connection!, "app-one:");
        var second = new RedisCacheStore(_connection!, "app-two:");

        await first.SetAsync("shared", "one", null, default);
        await second.SetAsync("shared", "two", null, default);

        (await first.GetAsync<string>("shared", default)).Should().Be("one");
        (await second.GetAsync<string>("shared", default)).Should().Be("two");
    }
}

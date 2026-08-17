using Abacus.Run.Abstractions;
using FluentAssertions;

namespace Abacus.Run.BrokerTests;

/// <summary>
/// What every <see cref="ICacheStore"/> must do, regardless of technology.
/// </summary>
/// <remarks>
/// <para>
/// Written once and run against every implementation, so "Memcached behaves like Redis behaves like
/// in-memory" is a checked fact rather than an intention. An adapter author writes a subclass and
/// finds out immediately where their implementation disagrees.
/// </para>
/// <para>
/// The assertions are plain methods rather than <c>[Fact]</c>s so each subclass can carry its own
/// skip semantics — the in-memory suite always runs, the Redis one skips without Docker. A shared
/// base with baked-in attributes would force one policy on both.
/// </para>
/// </remarks>
public abstract class CacheStoreContract
{
    protected static string Key() => $"k-{Guid.NewGuid():N}";

    public static async Task A_missing_key_reads_as_absent(ICacheStore store)
        => (await store.GetAsync<string>(Key(), default)).Should()
            .BeNull("a cache miss is an ordinary outcome, not an error");

    public static async Task A_value_round_trips(ICacheStore store)
    {
        string key = Key();
        await store.SetAsync(key, "value", null, default);

        (await store.GetAsync<string>(key, default)).Should().Be("value");
    }

    public static async Task A_complex_value_round_trips(ICacheStore store)
    {
        string key = Key();
        await store.SetAsync(key, new CachedThing("abacus", 42), null, default);

        CachedThing? read = await store.GetAsync<CachedThing>(key, default);

        read.Should().NotBeNull();
        read!.Name.Should().Be("abacus");
        read.Count.Should().Be(42);
    }

    public static async Task Setting_the_same_key_replaces_the_value(ICacheStore store)
    {
        string key = Key();
        await store.SetAsync(key, "first", null, default);
        await store.SetAsync(key, "second", null, default);

        (await store.GetAsync<string>(key, default)).Should().Be("second");
    }

    public static async Task Remove_reports_whether_anything_was_there(ICacheStore store)
    {
        string key = Key();
        await store.SetAsync(key, "v", null, default);

        (await store.RemoveAsync(key, default)).Should().BeTrue();
        (await store.RemoveAsync(key, default)).Should()
            .BeFalse("removing nothing is not a failure, but it is distinguishable");
        (await store.GetAsync<string>(key, default)).Should().BeNull();
    }

    public static async Task A_value_with_no_ttl_persists(ICacheStore store)
    {
        string key = Key();
        await store.SetAsync(key, "v", null, default);
        await Task.Delay(1200);

        (await store.GetAsync<string>(key, default)).Should()
            .Be("v", "no ttl means the entry lives until evicted or replaced");
    }

    /// <summary>Real time, because a ttl is the one thing a fake clock cannot verify across a wire.</summary>
    public static async Task A_value_expires_on_its_ttl(ICacheStore store)
    {
        string key = Key();
        await store.SetAsync(key, "v", TimeSpan.FromSeconds(1), default);

        (await store.GetAsync<string>(key, default)).Should().Be("v");

        await Task.Delay(1500);

        (await store.GetAsync<string>(key, default)).Should().BeNull();
    }

    public static async Task GetOrCreate_computes_on_a_miss_and_reads_on_a_hit(ICacheStore store)
    {
        string key = Key();
        int calls = 0;

        ValueTask<string> Factory(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult("computed");
        }

        (await store.GetOrCreateAsync(key, Factory, null, default)).Should().Be("computed");
        (await store.GetOrCreateAsync(key, Factory, null, default)).Should().Be("computed");

        calls.Should().Be(1, "the second call must be served from the cache");
    }

    public static async Task Keys_are_independent(ICacheStore store)
    {
        string a = Key();
        string b = Key();

        await store.SetAsync(a, "A", null, default);
        await store.SetAsync(b, "B", null, default);
        await store.RemoveAsync(a, default);

        (await store.GetAsync<string>(b, default)).Should()
            .Be("B", "removing one key must not disturb another");
    }

    public sealed record CachedThing(string Name, int Count);
}

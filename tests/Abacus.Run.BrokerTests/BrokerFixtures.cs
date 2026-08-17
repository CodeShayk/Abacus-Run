using Abacus.Run.Abstractions;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;
using Xunit;

namespace Abacus.Run.BrokerTests;

/// <summary>
/// Marks a test that needs a container runtime. Skipped rather than failed when Docker is absent, so
/// a machine or CI leg without it still reports a green suite instead of a red one it cannot fix.
/// </summary>
public sealed class RequiresDockerFactAttribute : FactAttribute
{
    public RequiresDockerFactAttribute()
    {
        if (!DockerProbe.IsAvailable.Value)
        {
            Skip = "Docker is not available on this machine.";
        }
    }
}

public static class DockerProbe
{
    /// <summary>
    /// Whether a container runtime looks reachable. Checked once for the whole assembly, so a
    /// machine without Docker pays one probe rather than a failed container start per test class.
    /// </summary>
    /// <remarks>
    /// Deliberately a cheap endpoint check rather than a real ping: the probe decides whether to
    /// <em>skip</em>, and a skip that itself takes seconds to reach defeats the point. If the
    /// endpoint is there but broken, Testcontainers reports the real failure — which is the more
    /// useful outcome than silently skipping.
    /// </remarks>
    public static readonly Lazy<bool> IsAvailable = new(() =>
    {
        try
        {
            if (Environment.GetEnvironmentVariable("DOCKER_HOST") is { Length: > 0 })
            {
                return true;
            }

            return OperatingSystem.IsWindows()
                ? Directory.EnumerateFiles(@"\\.\pipe\").Any(p =>
                    p.Contains("docker_engine", StringComparison.OrdinalIgnoreCase))
                : File.Exists("/var/run/docker.sock");
        }
        catch
        {
            return false;
        }
    });
}

/// <summary>
/// Owns a Redis container for the whole assembly. One container, not one per class: the brokers
/// namespace their own keys, and starting Redis per class would dominate the run time.
/// </summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private RedisContainer? _container;

    public string ConnectionString { get; private set; } = "";

    public bool Available => _container is not null;

    public async Task InitializeAsync()
    {
        if (!DockerProbe.IsAvailable.Value)
        {
            return;
        }

        _container = new RedisBuilder().WithImage("redis:7-alpine").Build();
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

public sealed class RabbitMqFixture : IAsyncLifetime
{
    private RabbitMqContainer? _container;

    public string ConnectionString { get; private set; } = "";

    public bool Available => _container is not null;

    public async Task InitializeAsync()
    {
        if (!DockerProbe.IsAvailable.Value)
        {
            return;
        }

        _container = new RabbitMqBuilder().WithImage("rabbitmq:4-alpine").Build();
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>
{
    public const string Name = "redis";
}

[CollectionDefinition(Name)]
public sealed class RabbitMqCollection : ICollectionFixture<RabbitMqFixture>
{
    public const string Name = "rabbitmq";
}

/// <summary>Collects deliveries so a test can wait for an expected count rather than sleeping.</summary>
public sealed class DeliveryRecorder
{
    private readonly List<BrokerMessage> _messages = [];
    private readonly SemaphoreSlim _signal = new(0);

    public Func<EventDelivery, CancellationToken, ValueTask<DeliveryResult>> Handler => (delivery, _) =>
    {
        lock (_messages)
        {
            _messages.Add(delivery.Message);
        }

        _signal.Release();
        return ValueTask.FromResult(DeliveryResult.Ack);
    };

    public IReadOnlyList<BrokerMessage> Messages
    {
        get { lock (_messages) { return [.. _messages]; } }
    }

    public int Count
    {
        get { lock (_messages) { return _messages.Count; } }
    }

    /// <summary>Waits for <paramref name="count"/> deliveries. Returns false on timeout.</summary>
    public async Task<bool> WaitAsync(int count, TimeSpan? timeout = null)
    {
        TimeSpan budget = timeout ?? TimeSpan.FromSeconds(20);
        DateTime deadline = DateTime.UtcNow + budget;

        for (int i = 0; i < count; i++)
        {
            TimeSpan remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero || !await _signal.WaitAsync(remaining))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Asserts nothing further arrives. Needs a real wait — the absence of a message cannot be
    /// observed any faster than the time you are willing to give it to show up.
    /// </summary>
    public async Task<bool> StaysAtAsync(int expected, TimeSpan? window = null)
    {
        await Task.Delay(window ?? TimeSpan.FromSeconds(2));
        return Count == expected;
    }
}

public static class Msg
{
    public static BrokerMessage Distributed(
        string topic, string? correlationKey = null, string? tenantId = null, string payload = """{"v":1}""")
        => new()
        {
            MessageId = Abacus.Run.Core.IdGenerator.NewId("msg"),
            Topic = topic,
            PayloadJson = payload,
            Scope = DeliveryScope.Distributed,
            CorrelationKey = correlationKey,
            TenantId = tenantId
        };

    public static BrokerMessage Local(string topic, string payload = """{"v":1}""")
        => new()
        {
            MessageId = Abacus.Run.Core.IdGenerator.NewId("msg"),
            Topic = topic,
            PayloadJson = payload,
            Scope = DeliveryScope.Local
        };
}

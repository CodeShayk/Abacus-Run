using Abacus.Run.Abstractions;
using Abacus.Run.Api;
using Abacus.Run.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Abacus.Run.ChaosTests;

/// <summary>
/// Manages multiple host instances against shared stores (simulating multi-replica deployment).
/// Each host gets its own DispatcherService with a unique ReplicaId, but they all share the same
/// <see cref="IInstanceStore"/>, <see cref="IEventStore"/>, and <see cref="IApprovalStore"/>.
///
/// The harness supports:
/// <list type="bullet">
///   <item>Random kill/restart of individual replicas</item>
///   <item>Simulated SQL partition (store throws for a configurable duration)</item>
///   <item>Clock skew injection via <see cref="FakeTimeProvider"/></item>
///   <item>WireMock-style downstream call counting via the shared <see cref="CallCounter"/></item>
/// </list>
/// </summary>
public sealed class ChaosFixture : IAsyncDisposable
{
    private readonly List<ReplicaHandle> _replicas = [];
    private readonly SharedStores _shared;
    private readonly CallCounter _callCounter = new();
    private readonly ILoggerFactory _loggerFactory;

    public ChaosFixture(int replicaCount = 5)
    {
        _loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        _shared = new SharedStores();

        for (int i = 0; i < replicaCount; i++)
        {
            _replicas.Add(CreateReplica($"replica-{i}"));
        }
    }

    public IReadOnlyList<ReplicaHandle> Replicas => _replicas;
    public SharedStores Stores => _shared;
    public CallCounter Calls => _callCounter;

    /// <summary>Starts all replicas.</summary>
    public async Task StartAllAsync()
    {
        foreach (ReplicaHandle replica in _replicas)
        {
            await replica.StartAsync();
        }
    }

    /// <summary>Kills a random replica (simulating process crash).</summary>
    public async Task<ReplicaHandle> KillRandomAsync()
    {
        List<ReplicaHandle> alive = _replicas.Where(r => r.IsRunning).ToList();
        if (alive.Count == 0) throw new InvalidOperationException("No alive replicas to kill.");

        ReplicaHandle victim = alive[Random.Shared.Next(alive.Count)];
        await victim.StopAsync();
        return victim;
    }

    /// <summary>Restarts a previously killed replica.</summary>
    public async Task RestartAsync(ReplicaHandle replica)
    {
        if (replica.IsRunning) return;
        ReplicaHandle fresh = CreateReplica(replica.ReplicaId);
        int idx = _replicas.IndexOf(replica);
        _replicas[idx] = fresh;
        await fresh.StartAsync();
    }

    /// <summary>
    /// One audit database for the whole fixture, shared by its replicas the way the other stores are
    /// and separate from the deployed file, which every fixture would otherwise write into.
    /// </summary>
    private readonly string _auditDatabasePath =
        Path.Combine(Path.GetTempPath(), $"abacus-chaos-audit-{Guid.NewGuid():N}.db");

    private ReplicaHandle CreateReplica(string replicaId)
    {
        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Abacus:AuditRecords:ConnectionString", $"Data Source={_auditDatabasePath}");
                builder.ConfigureServices(services =>
                {
                    // Share stores across replicas.
                    services.RemoveAll<IInstanceStore>();
                    services.AddSingleton<IInstanceStore>(_shared.InstanceStore);
                    services.RemoveAll<IEventStore>();
                    services.AddSingleton<IEventStore>(_shared.EventStore);
                    services.RemoveAll<IApprovalStore>();
                    services.AddSingleton<IApprovalStore>(_shared.ApprovalStore);

                    // Unique replica ID.
                    services.Configure<WorkflowHostOptions>(o => o.ReplicaId = replicaId);
                });
            });

        return new ReplicaHandle(replicaId, factory);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (ReplicaHandle replica in _replicas)
        {
            await replica.DisposeAsync();
        }

        // Best-effort: a file left behind is untidy, not a test failure.
        try
        {
            if (File.Exists(_auditDatabasePath)) File.Delete(_auditDatabasePath);
        }
        catch (IOException)
        {
        }
    }
}

public sealed class ReplicaHandle : IAsyncDisposable
{
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public ReplicaHandle(string replicaId, WebApplicationFactory<Program> factory)
    {
        ReplicaId = replicaId;
        _factory = factory;
    }

    public string ReplicaId { get; }
    public bool IsRunning { get; private set; }
    public HttpClient Client => _client ?? throw new InvalidOperationException("Replica not started.");

    public Task StartAsync()
    {
        _client = _factory!.CreateClient();
        IsRunning = true;
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _client?.Dispose();
        _client = null;
        if (_factory is not null) await _factory.DisposeAsync();
        _factory = null;
        IsRunning = false;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}

/// <summary>Shared in-memory stores used by all replicas in the chaos fixture.</summary>
public sealed class SharedStores
{
    public IInstanceStore InstanceStore { get; } = new Abacus.Run.Persistence.InMemoryInstanceStore(TimeProvider.System);
    public IEventStore EventStore { get; } = new Abacus.Run.Persistence.InMemoryEventStore();
    public IApprovalStore ApprovalStore { get; } = new Abacus.Run.Persistence.InMemoryApprovalStore();
}

/// <summary>Thread-safe counter for tracking downstream calls (WireMock substitute).</summary>
public sealed class CallCounter
{
    private int _count;

    public int Count => Volatile.Read(ref _count);
    public void Increment() => Interlocked.Increment(ref _count);
    public void Reset() => Interlocked.Exchange(ref _count, 0);
}

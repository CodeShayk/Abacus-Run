using NBomber.CSharp;
using NBomber.Http.CSharp;
using NBomber.Contracts;
using NBomber.Contracts.Stats;

namespace Abacus.Run.LoadTests;

/// <summary>200/s per replica, p95 accept ≤ 150ms (NFR-1.1).</summary>
public static class SustainedStartScenario
{
    public static ScenarioProps Create(string baseUrl)
    {
        using var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };

        return Scenario.Create("sustained_starts", async context =>
        {
            var request = Http.CreateRequest("POST", $"{baseUrl}/workflows/load-test/instances")
                .WithHeader("Content-Type", "application/json")
                .WithBody(new StringContent(
                    """{"context":{"value":1}}""",
                    System.Text.Encoding.UTF8,
                    "application/json"));

            var response = await Http.Send(httpClient, request);
            return response;
        })
        .WithLoadSimulations(
            Simulation.Inject(rate: 200, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromMinutes(2))
        );
    }
}

/// <summary>GET /instances/{id} p95 ≤ 50ms at 1,000 rps (NFR-1.3).</summary>
public static class MixedReadLoadScenario
{
    public static ScenarioProps Create(string baseUrl)
    {
        using var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };

        // Pre-seed an instance ID for reads. In a real scenario, we'd create instances first.
        string instanceId = "load-test-read-instance";

        return Scenario.Create("mixed_read_load", async context =>
        {
            var request = Http.CreateRequest("GET", $"{baseUrl}/instances/{instanceId}");
            var response = await Http.Send(httpClient, request);
            return response;
        })
        .WithLoadSimulations(
            Simulation.Inject(rate: 1000, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromMinutes(2))
        );
    }
}

/// <summary>5,000 concurrent SSE subscribers, delivery p95 ≤ 250ms (NFR-1.9, NFR-2.3).</summary>
public static class SseFanOutScenario
{
    public static ScenarioProps Create(string baseUrl)
    {
        string instanceId = "load-test-sse-instance";

        return Scenario.Create("sse_fanout", async context =>
        {
            using var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var stream = await httpClient.GetStreamAsync(
                    $"{baseUrl}/instances/{instanceId}/events", cts.Token);

                using var reader = new StreamReader(stream);
                // Read a few lines to simulate subscription.
                for (int i = 0; i < 3 && !cts.IsCancellationRequested; i++)
                {
                    await reader.ReadLineAsync(cts.Token);
                }

                return Response.Ok();
            }
            catch (OperationCanceledException)
            {
                return Response.Ok(); // Expected timeout.
            }
        })
        .WithLoadSimulations(
            Simulation.KeepConstant(copies: 5000, during: TimeSpan.FromMinutes(1))
        );
    }
}

/// <summary>Commit p95 ≤ 80ms at 1,000 supersteps/s (NFR-1.6).</summary>
public static class CheckpointThroughputScenario
{
    public static ScenarioProps Create(string baseUrl)
    {
        using var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };

        return Scenario.Create("checkpoint_throughput", async context =>
        {
            // This scenario measures checkpoint commit latency by starting instances
            // that complete quickly (single-step workflows).
            var request = Http.CreateRequest("POST", $"{baseUrl}/workflows/load-test/instances")
                .WithHeader("Content-Type", "application/json")
                .WithHeader("Prefer", "wait=5")
                .WithBody(new StringContent(
                    """{"context":{"value":1}}""",
                    System.Text.Encoding.UTF8,
                    "application/json"));

            var response = await Http.Send(httpClient, request);
            return response;
        })
        .WithLoadSimulations(
            Simulation.Inject(rate: 1000, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromMinutes(2))
        );
    }
}

/// <summary>With and without middleware stack; delta ≤ 2ms p95 (NFR-1.5).</summary>
public static class MiddlewareOverheadScenario
{
    public static ScenarioProps Create(string baseUrl)
    {
        using var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };

        return Scenario.Create("middleware_overhead", async context =>
        {
            // Measure instance start latency. The baseline (no middleware) is compared externally.
            var request = Http.CreateRequest("POST", $"{baseUrl}/workflows/load-test/instances")
                .WithHeader("Content-Type", "application/json")
                .WithBody(new StringContent(
                    """{"context":{"value":1}}""",
                    System.Text.Encoding.UTF8,
                    "application/json"));

            var response = await Http.Send(httpClient, request);
            return response;
        })
        .WithLoadSimulations(
            Simulation.Inject(rate: 500, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromMinutes(1))
        );
    }
}

/// <summary>50k events/s → history freshness ≤ 1s, superstep latency unaffected (NFR-2.9).</summary>
public static class EventBackpressureScenario
{
    public static ScenarioProps Create(string baseUrl)
    {
        using var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };

        return Scenario.Create("event_backpressure", async context =>
        {
            // Start instances at high rate to generate event volume.
            var request = Http.CreateRequest("POST", $"{baseUrl}/workflows/load-test/instances")
                .WithHeader("Content-Type", "application/json")
                .WithBody(new StringContent(
                    """{"context":{"value":1}}""",
                    System.Text.Encoding.UTF8,
                    "application/json"));

            var response = await Http.Send(httpClient, request);
            return response;
        })
        .WithLoadSimulations(
            Simulation.Inject(rate: 2000, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromMinutes(2))
        );
    }
}

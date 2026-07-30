using NBomber.CSharp;
using NBomber.Http.CSharp;
using NBomber.Contracts;
using NBomber.Contracts.Stats;

namespace Abacus.Run.LoadTests;

/// <summary>
/// NBomber load test runner per TDD §15.8. Run in CI nightly against a 3-replica deployment;
/// fails the build on NFR regression.
///
/// Usage: dotnet run -- --scenario sustained-starts --base-url http://localhost:5000
/// </summary>
public static class Program
{
    public static void Main(string[] args)
    {
        string baseUrl = GetArg(args, "--base-url") ?? "http://localhost:5000";
        string scenarioName = GetArg(args, "--scenario") ?? "all";

        var scenarios = new Dictionary<string, Func<string, ScenarioProps>>(StringComparer.OrdinalIgnoreCase)
        {
            ["sustained-starts"]       = SustainedStartScenario.Create,
            ["mixed-read"]             = MixedReadLoadScenario.Create,
            ["sse-fanout"]             = SseFanOutScenario.Create,
            ["checkpoint-throughput"]   = CheckpointThroughputScenario.Create,
            ["middleware-overhead"]     = MiddlewareOverheadScenario.Create,
            ["event-backpressure"]      = EventBackpressureScenario.Create,
        };

        var selected = scenarioName == "all"
            ? scenarios.Values.Select(f => f(baseUrl)).ToArray()
            : scenarios.TryGetValue(scenarioName, out var factory)
                ? [factory(baseUrl)]
                : throw new ArgumentException($"Unknown scenario: {scenarioName}. Available: {string.Join(", ", scenarios.Keys)}");

        NBomberRunner
            .RegisterScenarios(selected)
            .WithReportFormats(ReportFormat.Html, ReportFormat.Md)
            .Run();
    }

    private static string? GetArg(string[] args, string name)
    {
        int idx = Array.IndexOf(args, name);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }
}

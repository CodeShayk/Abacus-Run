using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Abacus.Run.IntegrationTests;

public class ControlPlaneIntegrationTests : IClassFixture<HostFixture>
{
    private readonly HostFixture _host;
    private readonly HttpClient _client;

    public ControlPlaneIntegrationTests(HostFixture host)
    {
        _host = host;
        _client = host.CreateClient();
    }

    [Fact]
    public async Task Dashboard_Page_Returns_200_OK_With_Html()
    {
        HttpResponseMessage response = await _client.GetAsync("/control/Dashboard");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");

        string html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("Abacus Run");
        html.Should().Contain("Dashboard");
        html.Should().Contain("Active");
    }

    [Fact]
    public async Task Instances_Page_Returns_200_OK_With_Html()
    {
        HttpResponseMessage response = await _client.GetAsync("/control/Instances");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");

        string html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("Instances");
        html.Should().Contain("Workflow");
        html.Should().Contain("Status");
    }

    [Fact]
    public async Task Approvals_Page_Returns_200_OK_With_Html()
    {
        HttpResponseMessage response = await _client.GetAsync("/control/Approvals");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");

        string html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("Pending Approvals");
    }

    [Fact]
    public async Task Workflows_Page_Returns_200_OK_With_Html()
    {
        HttpResponseMessage response = await _client.GetAsync("/control/Workflows");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");

        string html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("Registered Workflows");
        html.Should().Contain("order");
        html.Should().Contain("gated-order");
    }

    [Fact]
    public async Task Instance_Detail_Page_Returns_200_OK_For_Existing_Instance()
    {
        string instanceId = await _host.StartAsync(_client, "order", new OrderContext("ORD-CP-TEST"));

        HttpResponseMessage response = await _client.GetAsync($"/control/Instances/Detail?id={instanceId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string html = await response.Content.ReadAsStringAsync();
        html.Should().Contain(instanceId[..12]);
        html.Should().Contain("Graph");
        html.Should().Contain("Events");
        html.Should().Contain("Context");
    }

    [Fact]
    public async Task Diagnostics_Metrics_Endpoint_Returns_Metrics_Json()
    {
        HttpResponseMessage response = await _client.GetAsync("/diagnostics/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("activeInstances");
    }

    [Theory]
    [InlineData("/css/site.css")]
    [InlineData("/js/live.js")]
    [InlineData("/js/graph.js")]
    public async Task Static_assets_are_served(string path)
    {
        // A broken asset path 404s silently and leaves the UI unstyled with no live updates, which a
        // page-returns-200 assertion would sail straight past.
        HttpResponseMessage response = await _client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().NotBeEmpty();
    }

    [Fact]
    public async Task Layout_asset_links_resolve()
    {
        string html = await _client.GetStringAsync("/control/Dashboard");

        MatchCollection matches = Regex.Matches(html, @"(?:href|src)=""(/(?:css|js)/[^""]+)""");
        matches.Should().NotBeEmpty("the layout must reference the control plane's static assets");

        foreach (Match match in matches)
        {
            HttpResponseMessage asset = await _client.GetAsync(match.Groups[1].Value);
            asset.StatusCode.Should().Be(HttpStatusCode.OK, $"{match.Groups[1].Value} is referenced by the layout");
        }
    }
}

using Abacus.Run.Api;
using Abacus.Run.Dsl.Hosting;
using Abacus.Run.Service.ControlPlane;
using Abacus.Run.Service.Infrastructure.Auditing;
using Abacus.Run.Core;
using Abacus.Run.Service;
using Abacus.Run.Service.Workflows.ExampleOrder;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();

builder.Services
    .AddAbacus(builder.Configuration)
    // The worked example of the audit hook. It is the only workflow this host ships; a real
    // deployment registers its own definitions here the same way.
    .AddWorkflow<ExampleOrderWorkflow>()

    // Makes the DSL routes usable before any document exists — which is the state a host is in while
    // someone is writing their first one. Documents are added with AddDslWorkflow(path) or
    // AddDslWorkflowsFromDirectory(...) alongside the compiled registrations above.
    .UseDsl();

// Durable, workflow-agnostic storage for the audit records that workflow definitions declare.
// Displaces the framework's in-memory default.
builder.Services.AddSqliteAuditRecords(builder.Configuration);

WebApplication app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseStaticFiles();
app.UseRouting();

app.MapWorkflowApi();
app.MapDslApi();
app.MapControlPlane("/control");
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" }));

app.Run();

/// <summary>Exposed so integration tests can drive the real host through WebApplicationFactory.</summary>
public partial class Program;

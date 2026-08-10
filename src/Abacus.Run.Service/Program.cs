using Abacus.Run.Api;
using Abacus.Run.Service.ControlPlane;
using Abacus.Run.Service.Infrastructure.Auditing;
using Abacus.Run.Core;
using Abacus.Run.Service;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddAbacus(builder.Configuration);

// Durable, workflow-agnostic storage for the audit records that workflow definitions declare.
// Displaces the framework's in-memory default.
builder.Services.AddSqliteAuditRecords(builder.Configuration);

WebApplication app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseStaticFiles();
app.UseRouting();

app.MapWorkflowApi();
app.MapControlPlane("/control");
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" }));

app.Run();

/// <summary>Exposed so integration tests can drive the real host through WebApplicationFactory.</summary>
public partial class Program;

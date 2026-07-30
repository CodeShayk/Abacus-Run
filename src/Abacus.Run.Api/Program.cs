using Abacus.Run.Api;
using Abacus.Run.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddSingleton<IEventBus, InMemoryEventBus>();

builder.Services
    .AddWorkflowHost(builder.Configuration)
    .AddBuiltInMiddleware()
    .AddBackgroundServices();

WebApplication app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapWorkflowApi();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" }));

app.Run();

/// <summary>Exposed so integration tests can drive the real host through WebApplicationFactory.</summary>
public partial class Program;

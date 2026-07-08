using ResearchDiscovery.Api.Cli;
using ResearchDiscovery.Api.Filters;
using ResearchDiscovery.Api.Hosting;
using ResearchDiscovery.Infrastructure.DependencyInjection;
using ResearchDiscovery.Infrastructure.Persistence;

// Multi-mode entry point: `dotnet ResearchDiscovery.Api.dll ingest backfill|delta`
// runs a one-shot ingestion, `... analyze <category>` runs a one-shot Phase 2
// analysis (both without Kestrel or the scheduler) and exits; anything else
// starts the web host. All modes share the exact same configuration pipeline
// and AddInfrastructure composition.
if (args.Length > 0 && args[0].Equals("ingest", StringComparison.OrdinalIgnoreCase))
{
    return await IngestCommandRunner.RunAsync(args);
}

if (args.Length > 0 && args[0].Equals("analyze", StringComparison.OrdinalIgnoreCase))
{
    return await AnalyzeCommandRunner.RunAsync(args);
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

builder.Services.AddScoped<AdminApiKeyFilter>();
builder.Services.AddSingleton<IngestionJobQueue>();
builder.Services.AddHostedService<IngestionQueueHostedService>();
builder.Services.AddHostedService<DailyIngestionHostedService>();
builder.Services.AddSingleton<AnalysisJobQueue>();
builder.Services.AddHostedService<AnalysisQueueHostedService>();

var app = builder.Build();

await DatabaseStartup.MigrateIfConfiguredAsync(app.Services, app.Configuration);

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapControllers();

// Serves the built React SPA in the single-container deployment. Registered
// after MapControllers so /api/* always wins.
app.MapFallbackToFile("index.html");

await app.RunAsync();
return 0;

// Exposes the entry point to WebApplicationFactory in integration tests.
public partial class Program;

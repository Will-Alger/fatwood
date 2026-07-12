using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using ResearchDiscovery.Api.Auth;
using ResearchDiscovery.Api.Cli;
using ResearchDiscovery.Api.Hosting;
using ResearchDiscovery.Application.Options;
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

if (args.Length > 0 && args[0].Equals("embed", StringComparison.OrdinalIgnoreCase))
{
    return await EmbedCommandRunner.RunAsync();
}

if (args.Length > 0 && args[0].Equals("eval", StringComparison.OrdinalIgnoreCase))
{
    return await EvalCommandRunner.RunAsync(args);
}

if (args.Length > 0 && args[0].Equals("enrich", StringComparison.OrdinalIgnoreCase))
{
    return await EnrichCommandRunner.RunAsync(args);
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

// Identity lives in Entra External ID (JWT bearer); authority comes from the
// account row, stamped by UserContextMiddleware. Without a configured tenant
// the API runs as a synthetic local admin — never in production, which
// fails fast instead of silently deploying an open admin surface.
var authOptions = builder.Configuration
    .GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();

if (authOptions.Enabled)
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = authOptions.Authority;
            options.Audience = authOptions.Audience;
            // Keep the raw OIDC claim names (oid/email/name) instead of the
            // legacy SOAP-era claim-type mapping.
            options.MapInboundClaims = false;
            options.TokenValidationParameters.NameClaimType = "name";
            options.TokenValidationParameters.RoleClaimType = "roles";
        });
}
else if (builder.Environment.IsProduction() && !authOptions.DangerouslyAllowAnonymous)
{
    throw new InvalidOperationException(
        "Auth:Authority is not configured. Production requires real authentication; " +
        "set Auth:DangerouslyAllowAnonymous=true only to run deliberately open.");
}
else
{
    builder.Services.AddAuthentication(DevAuthHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevAuthHandler.SchemeName, null);
}

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AuthPolicies.Admin, p => p.RequireRole(nameof(ResearchDiscovery.Domain.Entities.UserRole.Admin)));
    options.AddPolicy(AuthPolicies.ActiveUser, p => p
        .RequireAuthenticatedUser()
        .RequireClaim(AuthPolicies.ActiveClaim, "true"));
});

builder.Services.AddSingleton<IngestionJobQueue>();
builder.Services.AddHostedService<IngestionQueueHostedService>();
builder.Services.AddHostedService<DailyIngestionHostedService>();
builder.Services.AddSingleton<AnalysisJobQueue>();
builder.Services.AddSingleton<AnalysisProgressTracker>();
builder.Services.AddHostedService<AnalysisQueueHostedService>();

var app = builder.Build();

await DatabaseStartup.MigrateIfConfiguredAsync(app.Services, app.Configuration);

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseMiddleware<UserContextMiddleware>();
app.UseAuthorization();

app.MapControllers();

// Serves the built React SPA in the single-container deployment. Registered
// after MapControllers so /api/* always wins.
app.MapFallbackToFile("index.html");

await app.RunAsync();
return 0;

// Exposes the entry point to WebApplicationFactory in integration tests.
public partial class Program;

using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
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

// Abuse braking distinct from the dollar budget: generous global per-caller
// limit (bots hammering anonymous endpoints), tight bucket on the endpoints
// that spend tokens. Keyed by account when signed in, by IP otherwise.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetTokenBucketLimiter(
            RateLimiterKey(ctx),
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 300,
                TokensPerPeriod = 150,
                ReplenishmentPeriod = TimeSpan.FromSeconds(10),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    options.AddPolicy("llm", ctx =>
        RateLimitPartition.GetTokenBucketLimiter(
            RateLimiterKey(ctx),
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 10,
                TokensPerPeriod = 5,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    // Native-auth proxy: Entra sees our egress IP, so per-caller brute-force
    // braking happens here. Generous enough for a real sign-up (start,
    // challenge, OTP retries, token) with room to fumble a password.
    options.AddPolicy("auth", ctx =>
        RateLimitPartition.GetTokenBucketLimiter(
            RateLimiterKey(ctx),
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 30,
                TokensPerPeriod = 10,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    static string RateLimiterKey(HttpContext ctx) =>
        ctx.User.FindFirst("oid")?.Value
            ?? ctx.Connection.RemoteIpAddress?.ToString()
            ?? "unknown";
});

builder.Services.AddHttpClient(NativeAuthProxy.HttpClientName, client =>
    client.Timeout = TimeSpan.FromSeconds(30));

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

if (!app.Environment.IsDevelopment())
{
    // TLS terminates upstream (Cloudflare edge / ACA ingress); HSTS still
    // pins browsers to https for the domain.
    app.UseHsts();
}

// Baseline security headers on every response, SPA and API alike. The CSP
// allows exactly what the app uses: self-hosted assets, inline style
// attributes (React), and the External ID endpoints MSAL talks to.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    headers["X-Frame-Options"] = "DENY";
    headers["Content-Security-Policy"] =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "font-src 'self'; " +
        "connect-src 'self' https://fatwoodio.ciamlogin.com https://login.microsoftonline.com; " +
        "frame-src https://fatwoodio.ciamlogin.com; " +
        "frame-ancestors 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self' https://fatwoodio.ciamlogin.com";
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseMiddleware<UserContextMiddleware>();
// After authentication so signed-in callers are limited per account, not per
// NAT'd IP.
app.UseRateLimiter();
app.UseAuthorization();

app.MapControllers();
app.MapNativeAuthProxy(authOptions.Authority);

// Serves the built React SPA in the single-container deployment. Registered
// after MapControllers so /api/* always wins.
app.MapFallbackToFile("index.html");

await app.RunAsync();
return 0;

// Exposes the entry point to WebApplicationFactory in integration tests.
public partial class Program;

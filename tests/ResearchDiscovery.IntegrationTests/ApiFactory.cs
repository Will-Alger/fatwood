using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Infrastructure.Persistence;

namespace ResearchDiscovery.IntegrationTests;

/// <summary>
/// Hosts the real API over an in-memory Sqlite database. Tests may seed data
/// through the context (fixtures are allowed in tests — only the running app
/// is restricted to live arXiv data). The arXiv client is stubbed to return
/// nothing so no test ever leaves the process.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    /// <summary>Set before the host is first used to enable the admin surface.</summary>
    public string? AdminApiKey { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Database:MigrateOnStartup", "false");
        builder.UseSetting("Ingestion:Schedule:Enabled", "false");
        builder.UseSetting("Admin:ApiKey", AdminApiKey ?? string.Empty);

        builder.ConfigureServices(services =>
        {
            _connection.Open();

            services.RemoveAll(typeof(DbContextOptions<AppDbContext>));
            services.RemoveAll(typeof(IDbContextOptionsConfiguration<AppDbContext>));
            services.RemoveAll(typeof(IDbContextFactory<AppDbContext>));
            services.RemoveAll(typeof(AppDbContext));
            services.AddDbContextFactory<AppDbContext>(options => options.UseSqlite(_connection));

            services.RemoveAll(typeof(IArxivClient));
            services.AddSingleton<IArxivClient, StubArxivClient>();
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();

        return host;
    }

    public async Task SeedAsync(Func<AppDbContext, Task> seed)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await seed(db);
        await db.SaveChangesAsync();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _connection.Dispose();
    }

    private sealed class StubArxivClient : IArxivClient
    {
        public Task<ArxivPage> QueryAsync(ArxivQuery query, CancellationToken ct) =>
            Task.FromResult(new ArxivPage(0, []));
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ResearchDiscovery.Infrastructure.Persistence;

public static class DatabaseStartup
{
    /// <summary>
    /// Applies pending migrations when Database:MigrateOnStartup is enabled.
    /// Suitable for local/compose and single-replica deployments; production
    /// pipelines should run a migration bundle instead and disable this flag.
    /// </summary>
    public static async Task MigrateIfConfiguredAsync(
        IServiceProvider services, IConfiguration configuration, CancellationToken ct = default)
    {
        if (!configuration.GetValue("Database:MigrateOnStartup", defaultValue: true))
        {
            return;
        }

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(nameof(DatabaseStartup));

        logger.LogInformation("Applying database migrations (Database:MigrateOnStartup=true)");
        await db.Database.MigrateAsync(ct);
        logger.LogInformation("Database is up to date");
    }
}

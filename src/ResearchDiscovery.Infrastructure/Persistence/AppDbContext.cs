using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using ResearchDiscovery.Domain.Entities;

namespace ResearchDiscovery.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Paper> Papers => Set<Paper>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<PaperCategory> PaperCategories => Set<PaperCategory>();

    public DbSet<CategoryIngestionState> CategoryIngestionStates => Set<CategoryIngestionState>();

    public DbSet<IngestionRun> IngestionRuns => Set<IngestionRun>();

    public DbSet<IngestionLock> IngestionLocks => Set<IngestionLock>();

    public DbSet<AnalysisResult> AnalysisResults => Set<AnalysisResult>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // SQLite (used only by the test suite) cannot compare or order
        // DateTimeOffset columns; store them as binary. All timestamps in this
        // model are UTC, so binary ordering stays chronological. Checked by
        // provider name to avoid referencing the Sqlite package here.
        if (Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            var converter = new DateTimeOffsetToBinaryConverter();
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var property in entityType.GetProperties())
                {
                    if (property.ClrType == typeof(DateTimeOffset) ||
                        property.ClrType == typeof(DateTimeOffset?))
                    {
                        property.SetValueConverter(converter);
                    }
                }
            }
        }
    }
}

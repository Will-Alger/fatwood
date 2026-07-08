using Microsoft.EntityFrameworkCore;
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
    }
}

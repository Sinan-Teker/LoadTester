using Microsoft.EntityFrameworkCore;

namespace LoadTester.Web.Data;

/// <summary>SQLite store. Dates are written as UTC; SQLite does not keep the kind.</summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<ScenarioEntity> Scenarios => Set<ScenarioEntity>();
    public DbSet<TestRunEntity> Runs => Set<TestRunEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ScenarioEntity>(entity =>
        {
            entity.ToTable("Scenarios");
            entity.Property(s => s.Name).HasMaxLength(200);
        });

        modelBuilder.Entity<TestRunEntity>(entity =>
        {
            entity.ToTable("Runs");
            entity.HasIndex(r => r.ScenarioId);
            entity.Property(r => r.ScenarioName).HasMaxLength(200);
            entity.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
        });
    }
}

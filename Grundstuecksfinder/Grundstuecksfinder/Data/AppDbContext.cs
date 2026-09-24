using Grundstuecksfinder.Models;
using Microsoft.EntityFrameworkCore;

namespace Grundstuecksfinder.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Property> Properties => Set<Property>();
    public DbSet<ImportRun> ImportRuns => Set<ImportRun>();
    public DbSet<SourceState> SourceStates => Set<SourceState>();
    public DbSet<DailyTelemetry> DailyTelemetry => Set<DailyTelemetry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<DailyTelemetry>(b =>
        {
            b.HasKey(t => t.Date);
        });

        modelBuilder.Entity<Property>(b =>
        {
            b.HasIndex(p => p.Plz);
            b.HasIndex(p => p.Gemeinde);
            b.HasIndex(p => p.Source);
            b.HasOne(p => p.ImportRun)
             .WithMany()
             .HasForeignKey(p => p.ImportRunId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ImportRun>(b =>
        {
            b.HasIndex(r => r.Source);
            // Readable in the table, and adding a reason can't shift the stored values.
            b.Property(r => r.Reason).HasConversion<string>();
        });

        modelBuilder.Entity<SourceState>(b =>
        {
            b.HasKey(s => s.Source);
            b.HasOne(s => s.ServedRun)
             .WithMany()
             .HasForeignKey(s => s.ServedRunId)
             .OnDelete(DeleteBehavior.Restrict);
        });
    }
}

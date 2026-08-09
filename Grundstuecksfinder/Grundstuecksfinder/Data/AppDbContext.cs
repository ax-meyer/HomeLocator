using Grundstuecksfinder.Models;
using Microsoft.EntityFrameworkCore;

namespace Grundstuecksfinder.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Property> Properties => Set<Property>();
    public DbSet<ImportLog> ImportLogs => Set<ImportLog>();
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
            b.HasOne(p => p.ImportLog)
             .WithMany()
             .HasForeignKey(p => p.ImportLogId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ImportLog>(b =>
        {
            b.HasIndex(l => new { l.Source, l.DatasetName, l.FileName, l.FileTimestamp }).IsUnique();
        });
    }
}

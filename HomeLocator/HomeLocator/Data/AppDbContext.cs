using HomeLocator.Models;
using Microsoft.EntityFrameworkCore;

namespace HomeLocator.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Property> Properties => Set<Property>();
    public DbSet<ImportLog> ImportLogs => Set<ImportLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Property>(b =>
        {
            b.HasIndex(p => p.Plz);
            b.HasIndex(p => p.Gemeinde);
            b.HasOne(p => p.ImportLog)
             .WithMany()
             .HasForeignKey(p => p.ImportLogId)
             .OnDelete(DeleteBehavior.Restrict);
        });
    }
}

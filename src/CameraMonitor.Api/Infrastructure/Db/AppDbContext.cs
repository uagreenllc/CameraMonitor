using Microsoft.EntityFrameworkCore;

namespace CameraMonitor.Api.Infrastructure.Db;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<SegmentRow> Segments => Set<SegmentRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SegmentRow>(e =>
        {
            e.Property(s => s.StartUtc).HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
            e.HasIndex(s => new { s.CameraId, s.LensId, s.StartUtc });
            e.HasIndex(s => s.Path).IsUnique();
        });
    }
}

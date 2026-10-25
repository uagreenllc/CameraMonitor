using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CameraMonitor.Api.Infrastructure.Db;

/// <summary>Used only by `dotnet ef` so it does not need to boot Program.cs (cameras.json, validated options).</summary>
internal sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite("Data Source=design-time.sqlite").Options);
}

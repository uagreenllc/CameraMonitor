using CameraMonitor.Api.Features.Cameras;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CameraMonitor.Api.Infrastructure.Db;

public static class DbSetup
{
    public static string ConnectionString(string dataDir) => $"Data Source={dataDir}/db/cm.sqlite;Default Timeout=5";

    public static async Task MigrateAsync(IServiceProvider services, CancellationToken ct)
    {
        var dataDir = services.GetRequiredService<IOptions<ServerOptions>>().Value.DataDir;
        Directory.CreateDirectory(Path.Combine(dataDir, "db"));

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync(ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
    }
}

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace CameraMonitor.Tests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cm-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_dir);
        var cameras = Path.Combine(_dir, "cameras.json");
        File.WriteAllText(cameras, """{"Cameras":{"Items":[]}}""");

        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Server:DataDir"] = _dir,
            ["Server:LiveDir"] = Path.Combine(_dir, "live"),
            ["Server:CamerasFile"] = cameras,
        }));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}

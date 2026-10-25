using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CameraMonitor.Tests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cm-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_dir);
        var cameras = Path.Combine(_dir, "cameras.json");
        File.WriteAllText(cameras, """{"Cameras":{"Items":[]}}""");

        // UseSetting (host settings) is visible to Program.cs at CreateBuilder time, unlike ConfigureAppConfiguration.
        builder.UseSetting("Server:DataDir", _dir);
        builder.UseSetting("Server:LiveDir", Path.Combine(_dir, "live"));
        builder.UseSetting("Server:CamerasFile", cameras);
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

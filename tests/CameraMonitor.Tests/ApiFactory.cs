using System.Text.Json;
using CameraMonitor.Api.Features.Cameras;
using CameraMonitor.Api.Features.Streams;
using CameraMonitor.Tests.Streams;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CameraMonitor.Tests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cm-tests", Guid.NewGuid().ToString("N"));
    private readonly string _data = $"./cm-tests-data/{Guid.NewGuid():N}"; // relative: ffmpeg paths must not contain a drive colon
    public string DataDir => _data;

    private FfmpegFactory _ffmpeg = (_, _) => new FakeFfmpegProcess(TimeProvider.System);
    private CamerasOptions _cameras = new();

    /// <summary>Replaces the default fake ffmpeg. Call before CreateClient().</summary>
    public ApiFactory WithFfmpeg(FfmpegFactory factory)
    {
        _ffmpeg = factory;
        return this;
    }

    /// <summary>Sets the cameras written to cameras.json. Call before CreateClient().</summary>
    public ApiFactory WithCameras(CamerasOptions options)
    {
        _cameras = options;
        return this;
    }

    private readonly Dictionary<string, string> _settings = [];

    /// <summary>Sets an extra host setting (e.g. Server:FfmpegPath). Call before CreateClient().</summary>
    public ApiFactory WithSetting(string key, string value)
    {
        _settings[key] = value;
        return this;
    }

    private bool _noHostedServices;

    /// <summary>Removes every IHostedService (for tests that call the services directly). Call before CreateClient().</summary>
    public ApiFactory WithoutHostedServices()
    {
        _noHostedServices = true;
        return this;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_dir);
        var cameras = Path.Combine(_dir, "cameras.json");
        File.WriteAllText(cameras, JsonSerializer.Serialize(new { Cameras = _cameras }));

        // UseSetting (host settings) is visible to Program.cs at CreateBuilder time, unlike ConfigureAppConfiguration.
        builder.UseSetting("Server:DataDir", _data);
        builder.UseSetting("Server:LiveDir", _data + "/live");
        builder.UseSetting("Server:CamerasFile", cameras);
        builder.UseSetting("Server:MinFreeGb", "0"); // tests must not depend on the dev machine's free space
        builder.UseSetting("Server:RetentionDays", "36500"); // fixed-date fixtures must not age out
        foreach (var (k, v) in _settings) builder.UseSetting(k, v);
        builder.ConfigureTestServices(services =>
        {
            services.Replace(ServiceDescriptor.Singleton(_ffmpeg));
            if (_noHostedServices) services.RemoveAll<IHostedService>();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) DeleteBestEffort(_data);
        if (disposing) DeleteBestEffort(_dir);
    }

    // SQLite WAL/SHM files can linger briefly on Windows after the host is disposed.
    private static void DeleteBestEffort(string dir)
    {
        for (var i = 0; i < 5 && Directory.Exists(dir); i++)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { Thread.Sleep(100); }
        }
    }
}

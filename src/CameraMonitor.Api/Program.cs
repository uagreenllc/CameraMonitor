using System.Text.Json;
using System.Text.Json.Serialization;
using CameraMonitor.Api.Features.Archive;
using CameraMonitor.Api.Features.Cameras;
using CameraMonitor.Api.Features.Events;
using CameraMonitor.Api.Features.Status;
using CameraMonitor.Api.Features.Streams;
using CameraMonitor.Api.Infrastructure.Db;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration));

var server = builder.Configuration.GetSection(ServerOptions.Section).Get<ServerOptions>() ?? new();
builder.Configuration.AddJsonFile(Path.GetFullPath(server.CamerasFile), optional: false, reloadOnChange: true);

builder.Services.AddOptions<ServerOptions>().BindConfiguration(ServerOptions.Section)
    .Validate(o => !o.DataDir.Contains(':') && !o.LiveDir.Contains(':'),
        "Server:DataDir and Server:LiveDir must be relative or colon-free paths (ffmpeg tee syntax uses ':'); on Windows use ./data and ./data/live")
    .ValidateOnStart();
builder.Services.AddOptions<CamerasOptions>().BindConfiguration(CamerasOptions.Section).ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<CamerasOptions>, CamerasOptionsValidator>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHealthChecks();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
builder.Services.AddSingleton<StreamStateStore>();
builder.Services.AddSingleton<FfmpegFactory>(sp => (args, name) => new FfmpegProcess(
    sp.GetRequiredService<IOptions<ServerOptions>>().Value.FfmpegPath, args, name,
    sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILoggerFactory>().CreateLogger("ffmpeg")));
builder.Services.AddDbContext<AppDbContext>((sp, o) =>
    o.UseSqlite(DbSetup.ConnectionString(sp.GetRequiredService<IOptions<ServerOptions>>().Value.DataDir)));
builder.Services.AddSingleton<TileService>();
builder.Services.AddSingleton<ExportService>();
builder.Services.AddSingleton<IDurationProbe, FfprobeDurationProbe>();
builder.Services.AddSingleton<SegmentIndexer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SegmentIndexer>());
builder.Services.AddSingleton<DirectoryScanner>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DirectoryScanner>());
builder.Services.AddSingleton<RetentionStatus>();
builder.Services.AddSingleton<RetentionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RetentionService>());
builder.Services.AddHostedService<StreamSupervisor>();
builder.Services.AddSingleton<XmAgentStore>();
builder.Services.AddHostedService<XmAgentSupervisor>();

var app = builder.Build();
await DbSetup.MigrateAsync(app.Services, CancellationToken.None);
// Tiles and HLS are ~10 req/s per open grid: keep them out of Information.
app.UseSerilogRequestLogging(o => o.GetLevel = (ctx, _, ex) =>
    ex != null || ctx.Response.StatusCode >= 500 ? LogEventLevel.Error
    : ctx.Response.StatusCode >= 400 ? LogEventLevel.Warning
    : ctx.Request.Path.StartsWithSegments("/api/tile") || ctx.Request.Path.StartsWithSegments("/live") || ctx.Request.Path.StartsWithSegments("/seg")
        || ctx.Request.Path.StartsWithSegments("/events") || ctx.Request.Path.StartsWithSegments("/api/snapshot") ? LogEventLevel.Debug
    : LogEventLevel.Information);
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapHealthChecks("/health");
app.MapCamerasEndpoints();
app.MapLiveEndpoints();
app.MapArchiveEndpoints();
app.MapStatusEndpoints();
app.MapPtzEndpoints();
app.MapEventsEndpoints();
app.Run();

public partial class Program { }

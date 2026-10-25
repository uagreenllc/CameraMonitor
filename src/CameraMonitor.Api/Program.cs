using System.Text.Json;
using System.Text.Json.Serialization;
using CameraMonitor.Api.Features.Cameras;
using CameraMonitor.Api.Features.Status;
using CameraMonitor.Api.Features.Streams;
using CameraMonitor.Api.Infrastructure.Db;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration));

var server = builder.Configuration.GetSection(ServerOptions.Section).Get<ServerOptions>() ?? new();
builder.Configuration.AddJsonFile(Path.GetFullPath(server.CamerasFile), optional: false, reloadOnChange: true);

builder.Services.AddOptions<ServerOptions>().BindConfiguration(ServerOptions.Section).ValidateOnStart();
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
builder.Services.AddHostedService<StreamSupervisor>();

var app = builder.Build();
await DbSetup.MigrateAsync(app.Services, CancellationToken.None);
app.UseSerilogRequestLogging();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapHealthChecks("/health");
app.MapCamerasEndpoints();
app.MapLiveEndpoints();
app.MapStatusEndpoints();
app.Run();

public partial class Program { }

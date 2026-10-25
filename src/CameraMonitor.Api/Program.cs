using CameraMonitor.Api.Features.Cameras;
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

var app = builder.Build();
app.UseSerilogRequestLogging();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapHealthChecks("/health");
app.Run();

public partial class Program { }

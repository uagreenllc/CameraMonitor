using Serilog;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration));
builder.Services.AddHealthChecks();

var app = builder.Build();
app.UseSerilogRequestLogging();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapHealthChecks("/health");
app.Run();

public partial class Program { }

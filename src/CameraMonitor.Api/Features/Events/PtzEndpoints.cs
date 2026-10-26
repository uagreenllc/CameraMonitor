using CameraMonitor.Api.Features.Cameras;
using CameraMonitor.Api.Infrastructure;
using CameraMonitor.Api.Infrastructure.Xm;
using Microsoft.Extensions.Options;

namespace CameraMonitor.Api.Features.Events;

public sealed record PtzRequest(string Command, string Action);

public sealed record PresetRequest(string Action, int Preset);

public static class PtzEndpoints
{
    private static readonly HashSet<string> Directions =
    [
        "DirectionUp", "DirectionDown", "DirectionLeft", "DirectionRight",
        "DirectionLeftUp", "DirectionLeftDown", "DirectionRightUp", "DirectionRightDown", "ZoomTile", "ZoomWide",
    ];

    private static readonly Dictionary<string, string> PresetCommands = new() { ["set"] = "SetPreset", ["goto"] = "GotoPreset", ["clear"] = "ClearPreset" };

    private static IResult Problem(int status, string title, string detail) => Results.Problem(detail, statusCode: status, title: title);

    /// <summary>Resolves camera, lens and a connected agent, or the error result to return.</summary>
    private static IResult? Resolve(string cam, string lens, bool needPtz, IOptionsMonitor<CamerasOptions> cameras, XmAgentStore agents,
        out XmCameraAgent agent, out LensConfig lensConfig)
    {
        agent = null!;
        lensConfig = null!;
        if (!SafeName.IsSafe(cam) || !SafeName.IsSafe(lens)) return Problem(400, "Некоректний запит", "Некоректний ідентифікатор камери.");
        var l = cameras.CurrentValue.Items.FirstOrDefault(c => c.Id == cam)?.Lenses.FirstOrDefault(x => x.Id == lens);
        if (l is null || (needPtz && !l.Ptz)) return Problem(404, "Не знайдено", "Камеру або об’єктив не знайдено.");
        if (agents.Agent(cam) is not { Client: not null } a) return Problem(503, "Недоступно", "Камера не підключена по DVRIP");
        agent = a;
        lensConfig = l;
        return null;
    }

    private static async Task<IResult> SendAsync(XmCameraAgent agent, string command, int channel, int preset, CancellationToken ct)
    {
        try
        {
            await agent.PtzAsync(command, channel, preset, 5, ct);
            return Results.NoContent();
        }
        catch (InvalidOperationException) // disconnected between the check and the call
        {
            return Problem(503, "Недоступно", "Камера не підключена по DVRIP");
        }
    }

    public static IEndpointRouteBuilder MapPtzEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/ptz/{cam}/{lens}", async (string cam, string lens, PtzRequest body, IOptionsMonitor<CamerasOptions> cameras,
            XmAgentStore agents, CancellationToken ct) =>
        {
            if (!Directions.Contains(body.Command) || body.Action is not ("start" or "stop"))
                return Problem(400, "Некоректний запит", "Невідома команда PTZ.");
            if (Resolve(cam, lens, true, cameras, agents, out var agent, out var l) is { } err) return err;
            return await SendAsync(agent, body.Command, l.DvripChannel, body.Action == "start" ? DvripClient.PtzStartPreset : DvripClient.PtzStopPreset, ct);
        });

        app.MapPost("/api/ptz/{cam}/{lens}/preset", async (string cam, string lens, PresetRequest body, IOptionsMonitor<CamerasOptions> cameras,
            XmAgentStore agents, CancellationToken ct) =>
        {
            if (!PresetCommands.TryGetValue(body.Action, out var command) || body.Preset is < 1 or > 255)
                return Problem(400, "Некоректний запит", "Некоректна дія або номер пресету (1–255).");
            if (Resolve(cam, lens, true, cameras, agents, out var agent, out var l) is { } err) return err;
            return await SendAsync(agent, command, l.DvripChannel, body.Preset, ct);
        });

        app.MapGet("/api/snapshot/{cam}/{lens}.jpg", async (string cam, string lens, IOptionsMonitor<CamerasOptions> cameras,
            XmAgentStore agents, HttpContext http, CancellationToken ct) =>
        {
            if (Resolve(cam, lens, false, cameras, agents, out var agent, out var l) is { } err) return err;
            var jpeg = await agent.SnapshotAsync(l.DvripChannel, ct);
            if (jpeg is null) return Problem(503, "Недоступно", "Камера не повернула знімок.");
            http.Response.Headers.CacheControl = "no-store";
            return Results.File(jpeg, "image/jpeg");
        });
        return app;
    }
}

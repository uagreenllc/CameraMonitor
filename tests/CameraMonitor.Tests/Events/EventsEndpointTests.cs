using System.Net;
using System.Text.Json;
using CameraMonitor.Api.Infrastructure.Db;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CameraMonitor.Tests.Events;

public sealed class EventsEndpointTests
{
    private static readonly DateTime T = new(2026, 10, 25, 14, 0, 0, DateTimeKind.Utc);

    private static async Task SeedAsync(ApiFactory f, params EventRow[] rows)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Events.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private static EventRow Row(string lens, DateTime at, string? snap = null, DateTime? end = null) =>
        new() { CameraId = "a", LensId = lens, Type = "human", StartedUtc = at, EndedUtc = end, SnapshotPath = snap };

    [Fact]
    public async Task Events_list_newest_first_with_snapshot_url()
    {
        using var f = new ApiFactory().WithoutHostedServices();
        var http = f.CreateClient();
        await SeedAsync(f, Row("b", T, "events/a/x.jpg"), Row("b", T.AddMinutes(5)), Row("b", T.AddDays(-3)));
        var arr = JsonDocument.Parse(await http.GetStringAsync("/api/events?cam=a&day=2026-10-25")).RootElement;
        arr.GetArrayLength().Should().Be(2);
        arr[0].GetProperty("snapshot").ValueKind.Should().Be(JsonValueKind.Null);
        arr[1].GetProperty("snapshot").GetString().Should().Be($"/events/{arr[1].GetProperty("id").GetInt64()}.jpg");
        arr[1].GetProperty("camera").GetString().Should().Be("a");
    }

    [Fact]
    public async Task Events_jpg_serves_file_and_404s_when_missing()
    {
        using var f = new ApiFactory().WithoutHostedServices();
        var http = f.CreateClient();
        Directory.CreateDirectory($"{f.DataDir}/events/a");
        File.WriteAllBytes($"{f.DataDir}/events/a/x.jpg", [1, 2, 3]);
        await SeedAsync(f, Row("b", T, "events/a/x.jpg"), Row("b", T, "events/a/missing.jpg"), Row("b", T, "events/../../evil.jpg"));
        (await http.GetByteArrayAsync("/events/1.jpg")).Should().Equal(1, 2, 3);
        (await http.GetAsync("/events/2.jpg")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await http.GetAsync("/events/3.jpg")).StatusCode.Should().NotBe(HttpStatusCode.OK);
        (await http.GetAsync("/events/99.jpg")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Timeline_includes_events_of_the_lens_only()
    {
        using var f = new ApiFactory().WithoutHostedServices();
        var http = f.CreateClient();
        await SeedAsync(f, Row("b", T, end: T.AddSeconds(30)), Row("c", T), Row("b", T.AddMinutes(1)));
        var ev = JsonDocument.Parse(await http.GetStringAsync("/api/timeline?cam=a&lens=b&day=2026-10-25")).RootElement.GetProperty("events");
        ev.GetArrayLength().Should().Be(2);
        ev[0].GetProperty("type").GetString().Should().Be("human");
        ev[0].GetProperty("lens").GetString().Should().Be("b");
        ev[0].GetProperty("to").ValueKind.Should().Be(JsonValueKind.String);
        ev[1].GetProperty("to").ValueKind.Should().Be(JsonValueKind.Null);
    }
}

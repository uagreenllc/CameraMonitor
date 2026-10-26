using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace CameraMonitor.Tests.Status;

public sealed class StatusEndpointTests
{
    [Fact]
    public async Task Status_has_disk_block_with_null_retention_when_archive_empty()
    {
        using var factory = new ApiFactory();
        var json = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/status");

        var disk = json.GetProperty("disk");
        foreach (var k in new[] { "freeGb", "archiveGb", "oldestSegmentUtc", "effectiveRetentionDays", "minRetentionDays", "lastRunUtc", "lastDeleted" })
            disk.TryGetProperty(k, out _).Should().BeTrue(k);
        disk.GetProperty("effectiveRetentionDays").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Status_has_cameras_block()
    {
        using var factory = new ApiFactory();
        var json = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/status");

        json.GetProperty("cameras").ValueKind.Should().Be(JsonValueKind.Array);
        json.GetProperty("cameras").GetArrayLength().Should().Be(0);
    }
}

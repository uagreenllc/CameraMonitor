using System.Net;

namespace CameraMonitor.Tests;

public sealed class HealthTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Health_returns_200_Healthy()
    {
        var resp = await factory.CreateClient().GetAsync("/health");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Be("Healthy");
    }
}

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

public sealed class StartupValidationTests
{
    [Theory]
    [InlineData("Server:DataDir")]
    [InlineData("Server:LiveDir")]
    public void Colon_in_data_paths_fails_startup(string key)
    {
        using var factory = new ApiFactory().WithSetting(key, "C:/x");
        var start = () => factory.CreateClient();
        start.Should().Throw<Microsoft.Extensions.Options.OptionsValidationException>();
    }
}

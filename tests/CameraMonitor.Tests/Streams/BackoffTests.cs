using CameraMonitor.Api.Features.Streams;

namespace CameraMonitor.Tests.Streams;

public class BackoffTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(3, 8)]
    [InlineData(6, 60)]
    [InlineData(10, 60)]
    [InlineData(-2, 1)]
    public void NextDelay_doubles_and_caps(int attempt, int seconds) =>
        Backoff.NextDelay(attempt).Should().Be(TimeSpan.FromSeconds(seconds));
}

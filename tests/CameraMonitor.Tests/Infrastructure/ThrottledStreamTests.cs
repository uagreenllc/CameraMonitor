using CameraMonitor.Api.Infrastructure;
using Microsoft.Extensions.Time.Testing;

namespace CameraMonitor.Tests.Infrastructure;

public class ThrottledStreamTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);

    private static async Task<TimeSpan> FakeTimeToWrite(int kbps, int bytes, int chunk)
    {
        var clock = new FakeTimeProvider();
        using var sink = new MemoryStream();
        await using var s = new ThrottledStream(sink, kbps, clock);
        var advanced = TimeSpan.Zero;
        for (var i = 0; i < bytes; i += chunk)
        {
            var write = s.WriteAsync(new byte[Math.Min(chunk, bytes - i)]).AsTask();
            while (!write.IsCompleted)
            {
                advanced.Should().BeLessThan(TimeSpan.FromSeconds(10), "the write should complete");
                clock.Advance(Tick);
                advanced += Tick;
                await Task.WhenAny(write, Task.Delay(200));   // the delay's continuation may run on another thread
            }

            await write;
        }

        sink.Length.Should().Be(bytes);
        return advanced;
    }

    [Fact]
    public async Task Writes_at_most_the_configured_rate()
    {
        // 1000 kbit/s = 125 000 B/s: 250 000 bytes take 2 s
        (await FakeTimeToWrite(1000, 250_000, 25_000)).Should().BeCloseTo(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(150));
    }

    [Fact]
    public async Task Zero_is_unlimited()
    {
        (await FakeTimeToWrite(0, 250_000, 25_000)).Should().Be(TimeSpan.Zero);
    }
}

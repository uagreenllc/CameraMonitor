using CameraMonitor.Api.Infrastructure;

namespace CameraMonitor.Tests.Infrastructure;

public class SafeNameTests
{
    [Theory]
    [InlineData("index.m3u8", true)]
    [InlineData("..", false)]
    [InlineData(".", false)]
    [InlineData("a..b", false)]
    [InlineData("a/b", false)]
    [InlineData(@"a\b", false)]
    [InlineData("", false)]
    [InlineData("a\n", false)]
    public void IsSafe_checks_segment(string name, bool expected) => SafeName.IsSafe(name).Should().Be(expected);

    [Fact]
    public void IsSafe_rejects_65_chars_accepts_64()
    {
        SafeName.IsSafe(new string('a', 65)).Should().BeFalse();
        SafeName.IsSafe(new string('a', 64)).Should().BeTrue();
    }
}

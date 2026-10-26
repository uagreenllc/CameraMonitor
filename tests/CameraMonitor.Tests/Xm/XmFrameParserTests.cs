using CameraMonitor.Api.Infrastructure.Xm;

namespace CameraMonitor.Tests.Xm;

public class XmFrameParserTests
{
    private static readonly DateTime T = new(2026, 10, 26, 14, 3, 10);
    private static readonly byte[] V1 = [0, 0, 0, 1, 0x67, 0xAA], V2 = [0, 0, 0, 1, 0x41, 0xBB], V3 = [0, 0, 0, 1, 0x41, 0xCC];

    private static byte[] Stream() =>
    [
        .. XmTestStream.IFrame(T, 15, 1280, 720, V1),
        .. XmTestStream.PFrame(V2),
        .. XmTestStream.Audio([1, 2]),
        .. XmTestStream.PFrame(V3),
    ];

    [Fact]
    public void UnpackTime_decodes_python_dvr_packing()
    {
        XmFrameParser.UnpackTime(0x6AB4E0CA).Should().Be(T);
    }

    [Fact]
    public void UnpackTime_rejects_invalid_fields()
    {
        var act = () => XmFrameParser.UnpackTime(26u << 26); // month 0, day 0
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(10000)]
    public void Feed_strips_headers_in_any_chunking(int chunk)
    {
        var data = Stream();
        var parser = new XmFrameParser();
        using var output = new MemoryStream();
        for (var i = 0; i < data.Length; i += chunk)
            parser.Feed(data.AsSpan(i, Math.Min(chunk, data.Length - i)), output);

        output.ToArray().Should().Equal([.. V1, .. V2, .. V3]);
        parser.FirstIFrame.Should().Be(new XmFrameInfo(XmFrameType.IFrame, T, 15, 1280, 720));
        parser.VideoFrames.Should().Be(3);
    }

    [Fact]
    public void Invalid_time_leaves_CameraTime_null()
    {
        var data = XmTestStream.IFrame(T, 15, 1280, 720, V1);
        data[8] = data[9] = data[10] = data[11] = 0;
        var parser = new XmFrameParser();
        parser.Feed(data, new MemoryStream());
        parser.FirstIFrame!.CameraTime.Should().BeNull();
        parser.FirstIFrame.Fps.Should().Be(15);
    }

    [Fact]
    public void Unknown_magic_throws()
    {
        var act = () => new XmFrameParser().Feed([0, 0, 1, 0, 0, 0, 0, 0], new MemoryStream());
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void XmStripStream_feeds_parser()
    {
        var parser = new XmFrameParser();
        using var output = new MemoryStream();
        using (var strip = new XmStripStream(parser, output))
            strip.Write(Stream(), 0, Stream().Length);
        output.ToArray().Should().Equal([.. V1, .. V2, .. V3]);
        parser.VideoFrames.Should().Be(3);
    }
}

using CameraMonitor.Api.Features.Streams;
using CameraMonitor.Api.Infrastructure;
using FluentAssertions;
using static CameraMonitor.Tests.Streams.Fmp4TestBoxes;

namespace CameraMonitor.Tests.Streams;

public class Fmp4BoxReaderTests
{
    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    [Fact]
    public void Feed_in_one_byte_chunks_yields_init_once_and_fragments()
    {
        var ftyp = Box("ftyp", 1, 2, 3, 4);
        var moov = Box("moov", 5, 6, 7);
        var moofA = Box("moof", 1);
        var mdatA = Box("mdat", 0, 0, 0, 3, 0x65, 0x88, 0x84);
        var moofB = Box("moof", 2);
        var mdatB = Box("mdat", 0, 0, 0, 3, 0x41, 0x9A, 0x00);
        var inits = new List<byte[]>();
        var frags = new List<Fmp4Fragment>();
        var r = new Fmp4BoxReader(inits.Add, frags.Add);

        foreach (var b in Cat(ftyp, moov, moofA, mdatA, moofB, mdatB)) r.Feed([b]);

        inits.Should().ContainSingle().Which.Should().Equal(Cat(ftyp, moov));
        r.Init.Should().Equal(Cat(ftyp, moov));
        frags.Should().HaveCount(2);
        frags[0].Data.Should().Equal(Cat(moofA, mdatA));
        frags[0].Keyframe.Should().BeTrue();
        frags[1].Data.Should().Equal(Cat(moofB, mdatB));
        frags[1].Keyframe.Should().BeFalse();
    }

    [Theory]
    [InlineData(new byte[] { 0, 0, 0, 2, 0x09, 0xF0, 0, 0, 0, 3, 0x65, 0x88, 0x84 }, true)]
    [InlineData(new byte[] { 0, 0, 0, 3, 0x41, 0x9A, 0x00 }, false)]
    [InlineData(new byte[] { 0, 0, 0, 2, 0x06, 0x05 }, false)]
    public void StartsWithKeyframe_walks_avcc_nals(byte[] payload, bool expected) =>
        Fmp4BoxReader.StartsWithKeyframe(payload).Should().Be(expected);

    [Fact]
    public void Boxes_between_fragments_are_dropped()
    {
        var frags = new List<Fmp4Fragment>();
        var r = new Fmp4BoxReader(_ => { }, frags.Add);
        var moof = Box("moof", 1);
        var mdat = Box("mdat", 0, 0, 0, 2, 0x65, 0x88);
        var mfra = Box("mfra", 9, 9, 9);

        r.Feed(Cat(Box("ftyp", 1), Box("moov", 2), moof, mdat, mfra, moof, mdat));

        frags.Should().HaveCount(2);
        frags.Should().OnlyContain(f => f.Data.Length == moof.Length + mdat.Length);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(70_000_000)]
    public void Invalid_box_size_is_rejected(int size)
    {
        var r = new Fmp4BoxReader(_ => { }, _ => { });
        var header = new byte[] { (byte)(size >> 24), (byte)(size >> 16), (byte)(size >> 8), (byte)size, (byte)'f', (byte)'t', (byte)'y', (byte)'p' };
        var act = () => r.Feed(header);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void CodecString_reads_avcC()
    {
        var init = Cat(Box("ftyp", 1), Box("moov", Box("avcC", 0x01, 0x64, 0x00, 0x1F, 0xFF)));
        Fmp4BoxReader.CodecString(init).Should().Be("avc1.64001f");
        Fmp4BoxReader.CodecString(Box("ftyp", 1)).Should().BeNull();
    }

    [Fact]
    public void Buffer_capacity_returns_to_small_size_after_a_large_box()
    {
        var r = new Fmp4BoxReader(_ => { }, _ => { });
        r.Feed(Cat(Box("ftyp", 1), Box("moov", 2)));
        var small = r.Capacity;

        r.Feed(Box("moof", 1));
        r.Feed(Box("mdat", new byte[3_000_000]));
        r.Capacity.Should().Be(small);

        r.Feed(Box("moof", 1));
        r.Feed(Box("mdat", 0, 0, 0, 2, 0x65, 0x88));
        r.Capacity.Should().Be(small);
    }

    [FfmpegFact]
    public async Task Real_ffmpeg_fmp4_is_parsed()
    {
        string[] args =
        [
            "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=15", "-t", "3",
            "-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency", "-g", "15", "-bf", "0", "-pix_fmt", "yuv420p",
            "-f", "mp4", "-movflags", "frag_keyframe+empty_moov+default_base_moof",
            "-frag_duration", "200000", "-min_frag_duration", "100000", "pipe:1",
        ];
        var (code, bytes) = await ProcessRunner.RunAsync("ffmpeg", args, TimeSpan.FromSeconds(30), CancellationToken.None);
        code.Should().Be(0);
        var frags = new List<Fmp4Fragment>();
        var r = new Fmp4BoxReader(_ => { }, frags.Add);

        r.Feed(bytes);

        r.Init.Should().NotBeNull();
        frags.Count.Should().BeGreaterThanOrEqualTo(10);
        frags[0].Keyframe.Should().BeTrue();
        Fmp4BoxReader.CodecString(r.Init).Should().StartWith("avc1.");
    }
}

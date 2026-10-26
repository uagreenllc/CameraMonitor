using System.Text.Json.Nodes;
using CameraMonitor.Api.Infrastructure.Xm;

namespace CameraMonitor.Tests.Xm;

public sealed class DvripCodecTests
{
    private const string LoginReplyHex =
        "ff00000042000000000000000000e903800000007b2022416c697665496e74657276616c22203a2032312c20224368616e6e656c4e756d22203a20322c2022446576696365547970652022203a2022495043222c202245787472614368616e6e656c22203a20302c202252657422203a203130302c202253657373696f6e494422203a20223078303030303030343222207d0a00";

    private static byte[] Fixture() => Convert.FromHexString(LoginReplyHex);

    [Theory]
    [InlineData("", "tlJwpbo6")]
    [InlineData("admin", "6QNMIQGe")]
    [InlineData("123456", "nTBCS19C")]
    [InlineData("UaGreen-2026", "yaN2LAiY")]
    [InlineData("пароль", "iigYAvIp")]
    public void SofiaHash_matches_python_dvr(string password, string expected) =>
        DvripCodec.SofiaHash(password).Should().Be(expected);

    [Fact]
    public void EncodeJson_login_matches_python_header()
    {
        var body = new JsonObject
        {
            ["EncryptType"] = "MD5",
            ["LoginType"] = "DVRIP-Web",
            ["PassWord"] = "tlJwpbo6",
            ["UserName"] = "admin",
        };

        var result = DvripCodec.EncodeJson(0, 0, 1000, body);

        result[0].Should().Be(0xFF);
        result[1..14].Should().OnlyContain(b => b == 0);
        result[14].Should().Be(0xE8);
        result[15].Should().Be(0x03);
        BitConverter.ToUInt32(result, 16).Should().Be((uint)(result.Length - 20));
        result[^2].Should().Be(0x0A);
        result[^1].Should().Be(0x00);
        DvripCodec.ParseJson(result.AsSpan(20))!["PassWord"]!.GetValue<string>().Should().Be("tlJwpbo6");
    }

    [Fact]
    public void TryReadFrame_parses_login_reply_fixture()
    {
        var ok = DvripCodec.TryReadFrame(Fixture(), out var frame, out var consumed);

        ok.Should().BeTrue();
        frame.Version.Should().Be(0);
        frame.Session.Should().Be(0x42u);
        frame.Sequence.Should().Be(0u);
        frame.MsgId.Should().Be(1001);
        frame.Payload.Length.Should().Be(128);
        consumed.Should().Be(148);

        var json = DvripCodec.ParseJson(frame.Payload.Span)!;
        json["Ret"]!.GetValue<int>().Should().Be(100);
        json["SessionID"]!.GetValue<string>().Should().Be("0x00000042");
        json["AliveInterval"]!.GetValue<int>().Should().Be(21);
        json.AsObject().ContainsKey("DeviceType ").Should().BeTrue();
    }

    [Fact]
    public void TryReadFrame_needs_whole_payload()
    {
        var fx = Fixture();

        DvripCodec.TryReadFrame(fx.AsSpan(0, fx.Length - 1), out _, out var consumed).Should().BeFalse();
        consumed.Should().Be(0);

        var twice = fx.Concat(fx).ToArray();
        DvripCodec.TryReadFrame(twice, out _, out var first).Should().BeTrue();
        first.Should().Be(148);
        DvripCodec.TryReadFrame(twice.AsSpan(first), out var second, out var c2).Should().BeTrue();
        c2.Should().Be(148);
        second.MsgId.Should().Be(1001);
    }

    [Fact]
    public void TryReadFrame_rejects_bad_magic()
    {
        var fx = Fixture();
        fx[0] = 0x00;

        var act = () => DvripCodec.TryReadFrame(fx, out _, out _);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void ParseJson_strips_control_bytes_and_tail()
    {
        var bytes = new List<byte>(System.Text.Encoding.UTF8.GetBytes("{\"Ret\":100"));
        bytes.Add(0x01);
        bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(",\"Name\":\"X\"}"));
        bytes.AddRange(new byte[] { 0x0A, 0x00 });

        DvripCodec.ParseJson(bytes.ToArray())!["Ret"]!.GetValue<int>().Should().Be(100);
        DvripCodec.ParseJson(new byte[] { 0xFF, 0xD8, 0xFF }).Should().BeNull();
    }
}

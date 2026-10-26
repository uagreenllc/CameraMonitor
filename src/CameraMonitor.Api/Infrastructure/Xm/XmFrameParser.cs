using System.Buffers.Binary;

namespace CameraMonitor.Api.Infrastructure.Xm;

public enum XmFrameType : byte { IFrame = 0xFC, PFrame = 0xFD, Audio = 0xFA, Info = 0xF9, Jpeg = 0xFE }

public sealed record XmFrameInfo(XmFrameType Type, DateTime? CameraTime, int Fps, int Width, int Height);

/// <summary>Strips Xiongmai frame headers from a raw DVRIP media stream, emitting the H.264 Annex-B payloads.</summary>
public sealed class XmFrameParser
{
    private readonly byte[] _header = new byte[16];
    private int _headerLen;
    private long _payloadLeft;
    private bool _emit;

    public XmFrameInfo? FirstIFrame { get; private set; }
    public int VideoFrames { get; private set; }

    public static DateTime UnpackTime(uint v) =>
        new((int)(v >> 26) + 2000, (int)((v >> 22) & 0xF), (int)((v >> 17) & 0x1F),
            (int)((v >> 12) & 0x1F), (int)((v >> 6) & 0x3F), (int)(v & 0x3F), DateTimeKind.Unspecified);

    private static int HeaderSize(byte type) => type is 0xFC or 0xFE ? 16 : 8;

    public void Feed(ReadOnlySpan<byte> data, Stream annexB)
    {
        while (!data.IsEmpty)
        {
            if (_payloadLeft > 0)
            {
                var n = (int)Math.Min(_payloadLeft, data.Length);
                if (_emit) annexB.Write(data[..n]);
                _payloadLeft -= n;
                data = data[n..];
                continue;
            }

            // Collect header bytes: first 4 (magic) decide the total size.
            var need = _headerLen < 4 ? 4 : HeaderSize(_header[3]);
            var take = Math.Min(need - _headerLen, data.Length);
            data[..take].CopyTo(_header.AsSpan(_headerLen));
            _headerLen += take;
            data = data[take..];
            if (_headerLen < 4) return;
            if (_header[0] != 0 || _header[1] != 0 || _header[2] != 1 || _header[3] is not (0xFC or 0xFD or 0xFA or 0xF9 or 0xFE))
            {
                _headerLen = 0;
                throw new InvalidDataException("Unknown Xiongmai frame magic");
            }
            if (_headerLen < HeaderSize(_header[3])) continue;
            StartFrame();
            _headerLen = 0;
        }
    }

    private void StartFrame()
    {
        var h = _header.AsSpan();
        var type = (XmFrameType)h[3];
        switch (type)
        {
            case XmFrameType.IFrame:
            case XmFrameType.Jpeg:
                _payloadLeft = BinaryPrimitives.ReadUInt32LittleEndian(h[12..]);
                _emit = type == XmFrameType.IFrame;
                if (_emit)
                {
                    VideoFrames++;
                    if (FirstIFrame is null)
                    {
                        DateTime? time = null;
                        try { time = UnpackTime(BinaryPrimitives.ReadUInt32LittleEndian(h[8..])); }
                        catch (ArgumentOutOfRangeException) { /* camera clock garbage: leave null, Task 9 decides */ }
                        FirstIFrame = new XmFrameInfo(type, time, h[5], h[6] * 8, h[7] * 8);
                    }
                }
                break;
            case XmFrameType.PFrame:
                _payloadLeft = BinaryPrimitives.ReadUInt32LittleEndian(h[4..]);
                _emit = true;
                VideoFrames++;
                break;
            default: // audio, info: 2-byte length at offset 6
                _payloadLeft = BinaryPrimitives.ReadUInt16LittleEndian(h[6..]);
                _emit = false;
                break;
        }
    }
}

public sealed class XmStripStream(XmFrameParser parser, Stream annexB) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => annexB.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => parser.Feed(buffer.AsSpan(offset, count), annexB);
    public override void Write(ReadOnlySpan<byte> buffer) => parser.Feed(buffer, annexB);
}

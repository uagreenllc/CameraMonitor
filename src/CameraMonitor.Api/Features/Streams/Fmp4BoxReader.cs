using System.Buffers.Binary;
using System.Text;

namespace CameraMonitor.Api.Features.Streams;

/// <summary>One moof+mdat pair (plus any box between them).</summary>
public sealed record Fmp4Fragment(byte[] Data, bool Keyframe);

/// <summary>Splits a fragmented-MP4 byte stream into an init segment (ftyp+moov) and moof+mdat fragments.</summary>
public sealed class Fmp4BoxReader(Action<byte[]> onInit, Action<Fmp4Fragment> onFragment)
{
    private const int MaxBoxSize = 64 * 1024 * 1024;   // bounds buffering of one incomplete box

    private byte[] _buf = new byte[64 * 1024];
    private int _len;
    private readonly List<byte[]> _initBoxes = [];
    private readonly List<byte[]> _pending = [];   // moof (+ boxes before mdat) of the fragment being assembled
    private bool _inFragment;

    public byte[]? Init { get; private set; }

    public void Feed(ReadOnlySpan<byte> data)
    {
        if (_len + data.Length > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + data.Length));
        data.CopyTo(_buf.AsSpan(_len));
        _len += data.Length;

        var pos = 0;
        while (_len - pos >= 8)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(_buf.AsSpan(pos));
            if (size is 0 or 1) throw new InvalidDataException($"Unsupported MP4 box size {size}");
            if (size < 8 || size > MaxBoxSize) throw new InvalidDataException($"Invalid MP4 box size {size}");
            if (_len - pos < size) break;
            var type = Encoding.ASCII.GetString(_buf, pos + 4, 4);
            Handle(type, _buf.AsSpan(pos, (int)size).ToArray());
            pos += (int)size;
        }

        if (pos > 0)
        {
            Buffer.BlockCopy(_buf, pos, _buf, 0, _len - pos);
            _len -= pos;
        }
    }

    private void Handle(string type, byte[] box)
    {
        if (type == "moof")
        {
            if (Init is null)
            {
                Init = _initBoxes.SelectMany(b => b).ToArray();
                onInit(Init);
            }
            _pending.Clear();
            _pending.Add(box);
            _inFragment = true;
        }
        else if (Init is null)
        {
            _initBoxes.Add(box);
        }
        else if (_inFragment && type == "mdat")
        {
            _pending.Add(box);
            onFragment(new Fmp4Fragment(_pending.SelectMany(b => b).ToArray(), StartsWithKeyframe(box.AsSpan(8))));
            _pending.Clear();
            _inFragment = false;
        }
        else if (_inFragment)
        {
            _pending.Add(box);
        }
        // else: mfra, sidx, free... outside moof..mdat are dropped
    }

    // ponytail: H.264 only, the cameras are H.264
    public static bool StartsWithKeyframe(ReadOnlySpan<byte> mdatPayload)
    {
        var p = 0;
        while (mdatPayload.Length - p >= 5)
        {
            var nalLen = BinaryPrimitives.ReadUInt32BigEndian(mdatPayload[p..]);
            var nalType = mdatPayload[p + 4] & 0x1F;
            if (nalType == 5) return true;
            if (nalType == 1) return false;
            if (nalLen > int.MaxValue - 4 - p) return false;
            p += 4 + (int)nalLen;
        }
        return false;
    }

    public static string? CodecString(ReadOnlySpan<byte> init)
    {
        var i = init.IndexOf("avcC"u8);
        if (i < 0 || init.Length < i + 8) return null;
        return "avc1." + Convert.ToHexString(init.Slice(i + 5, 3)).ToLowerInvariant();
    }
}

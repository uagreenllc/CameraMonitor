using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CameraMonitor.Api.Infrastructure.Xm;

public readonly record struct DvripFrame(byte Version, uint Session, uint Sequence, byte Total, byte Current, ushort MsgId, ReadOnlyMemory<byte> Payload);

/// <summary>DVRIP (Xiongmai "Sofia", port 34567) framing and payloads; spec: third_party/python-dvr/dvrip.py.</summary>
public static class DvripCodec
{
    public const int HeaderSize = 20;

    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string SofiaHash(string password)
    {
        var md5 = MD5.HashData(Encoding.UTF8.GetBytes(password));
        return string.Create(8, md5, static (chars, h) =>
        {
            for (var i = 0; i < 8; i++)
            {
                chars[i] = Alphabet[(h[2 * i] + h[2 * i + 1]) % 62];
            }
        });
    }

    public static byte[] EncodeJson(uint session, uint sequence, ushort msgId, JsonNode body)
    {
        var json = Encoding.UTF8.GetBytes(body.ToJsonString(JsonOptions));
        var payloadLength = json.Length + 2;
        var result = new byte[HeaderSize + payloadLength];
        result[0] = 0xFF;
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), session);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(14), msgId);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16), (uint)payloadLength);
        json.CopyTo(result, HeaderSize);
        result[^2] = 0x0A; // "\n\0" tail; result[^1] is already 0
        return result;
    }

    public static bool TryReadFrame(ReadOnlySpan<byte> buffer, out DvripFrame frame, out int consumed)
    {
        frame = default;
        consumed = 0;
        if (buffer.Length == 0)
        {
            return false;
        }

        if (buffer[0] != 0xFF)
        {
            throw new InvalidDataException($"Bad DVRIP magic 0x{buffer[0]:X2}.");
        }

        if (buffer.Length < HeaderSize)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(buffer[16..]);
        if (length > int.MaxValue - HeaderSize)
        {
            throw new InvalidDataException($"DVRIP payload length {length} is implausible.");
        }

        var total = HeaderSize + (int)length;
        if (buffer.Length < total)
        {
            return false;
        }

        frame = new DvripFrame(
            buffer[1],
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[8..]),
            buffer[12],
            buffer[13],
            BinaryPrimitives.ReadUInt16LittleEndian(buffer[14..]),
            buffer.Slice(HeaderSize, (int)length).ToArray());
        consumed = total;
        return true;
    }

    public static JsonNode? ParseJson(ReadOnlySpan<byte> payload)
    {
        var clean = new List<byte>(payload.Length);
        foreach (var b in payload)
        {
            if (b >= 0x20 || b is 0x09 or 0x0A or 0x0D)
            {
                clean.Add(b);
            }
        }

        try
        {
            return JsonNode.Parse(clean.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

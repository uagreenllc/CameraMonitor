using System.Buffers.Binary;

namespace CameraMonitor.Tests.Xm;

public static class XmTestStream
{
    public static byte[] IFrame(DateTime t, int fps, int w, int h, byte[] payload)
    {
        var packed = (uint)(t.Second | t.Minute << 6 | t.Hour << 12 | t.Day << 17 | t.Month << 22 | (t.Year - 2000) << 26);
        var b = new byte[16 + payload.Length];
        b[2] = 1; b[3] = 0xFC; b[4] = 2; b[5] = (byte)fps; b[6] = (byte)(w / 8); b[7] = (byte)(h / 8);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), packed);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), (uint)payload.Length);
        payload.CopyTo(b, 16);
        return b;
    }

    public static byte[] PFrame(byte[] payload)
    {
        var b = new byte[8 + payload.Length];
        b[2] = 1; b[3] = 0xFD;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(b, 8);
        return b;
    }

    public static byte[] Audio(byte[] payload)
    {
        var b = new byte[8 + payload.Length];
        b[2] = 1; b[3] = 0xFA; b[4] = 0x0E; b[5] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), (ushort)payload.Length);
        payload.CopyTo(b, 8);
        return b;
    }
}

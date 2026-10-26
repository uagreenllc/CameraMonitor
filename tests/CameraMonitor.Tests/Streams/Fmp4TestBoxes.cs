namespace CameraMonitor.Tests.Streams;

internal static class Fmp4TestBoxes
{
    public static byte[] Box(string type, params byte[] payload)
    {
        var size = payload.Length + 8;
        var b = new byte[size];
        b[0] = (byte)(size >> 24);
        b[1] = (byte)(size >> 16);
        b[2] = (byte)(size >> 8);
        b[3] = (byte)size;
        for (var i = 0; i < 4; i++) b[4 + i] = (byte)type[i];
        payload.CopyTo(b, 8);
        return b;
    }
}

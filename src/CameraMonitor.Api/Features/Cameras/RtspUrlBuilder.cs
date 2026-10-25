using System.Text.RegularExpressions;

namespace CameraMonitor.Api.Features.Cameras;

public static partial class RtspUrlBuilder
{
    public static string Build(CameraConfig cam, LensConfig lens, string password) =>
        lens.RtspUrl ?? $"rtsp://{cam.Host}:554/user={cam.User}&password={password}&channel={lens.Channel}&stream=0.sdp?real_stream";

    /// <summary>Masks the password in both XM query style and rtsp://user:pass@ style, for logs.</summary>
    public static string Redact(string rtspUrl) =>
        UserInfo().Replace(XmPassword().Replace(rtspUrl, "password=***"), "://$1:***@");

    [GeneratedRegex("password=[^&]*")]
    private static partial Regex XmPassword();

    [GeneratedRegex("://([^:/@]+):[^@/]*@")]
    private static partial Regex UserInfo();
}

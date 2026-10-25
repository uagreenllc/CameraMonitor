namespace CameraMonitor.Api.Features.Streams;

public static class FfmpegArgs
{
    public static string NormalizePath(string path)
    {
        var p = path.Replace('\\', '/').TrimEnd('/');
        if (p.Contains(':'))
            throw new ArgumentException(
                $"Path '{path}' must not contain ':' (ffmpeg tee syntax); use a relative path, e.g. Server:DataDir=./data on Windows.",
                nameof(path));
        return p;
    }

    public static IReadOnlyList<string> Recording(
        string rtspUrl, string recDir, string liveDir, int segmentSeconds, int hlsSeconds, bool recordAudio)
    {
        var rec = NormalizePath(recDir);
        var live = NormalizePath(liveDir);

        var args = new List<string>
        {
            "-nostdin", "-hide_banner", "-loglevel", "warning", "-rtsp_transport", "tcp", "-i", rtspUrl,
            "-map", "0:v", "-c:v", "copy",
        };
        if (recordAudio) args.AddRange(["-map", "0:a?", "-c:a", "aac", "-b:a", "64k"]);
        else args.Add("-an");

        args.AddRange(["-progress", "pipe:1", "-f", "tee",
            $"[f=hls:hls_time={segmentSeconds}:hls_segment_type=mpegts:hls_list_size=3:hls_flags=omit_endlist+program_date_time:strftime=1:strftime_mkdir=1:hls_segment_filename={rec}/%Y%m%d/%H/%H%M%S.ts]{rec}/archive.m3u8"
            + $"|[f=hls:hls_time={hlsSeconds}:hls_list_size=6:hls_flags=delete_segments+independent_segments+program_date_time:onfail=ignore]{live}/index.m3u8"]);
        return args;
    }
}

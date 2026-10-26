using CameraMonitor.Api.Features.Streams;

namespace CameraMonitor.Tests.Streams;

public class FfmpegArgsTests
{
    [Fact]
    public void Recording_builds_expected_tee_spec()
    {
        var args = FfmpegArgs.Recording("rtsp://h/x", "/data/rec/a/b", "/run/live/a/b", 10, 2, false);

        args.Should().Equal(
            "-nostdin", "-hide_banner", "-loglevel", "warning", "-rtsp_transport", "tcp", "-i", "rtsp://h/x",
            "-map", "0:v", "-c:v", "copy", "-an",
            "-progress", "pipe:1", "-f", "tee",
            "[f=hls:hls_time=10:hls_segment_type=mpegts:hls_list_size=3:hls_flags=omit_endlist+program_date_time:strftime=1:strftime_mkdir=1:hls_segment_filename=/data/rec/a/b/%Y%m%d/%H/%H%M%S.ts]/data/rec/a/b/archive.m3u8"
            + "|[f=hls:hls_time=2:hls_list_size=6:hls_flags=delete_segments+independent_segments+program_date_time:onfail=ignore]/run/live/a/b/index.m3u8");
        args.Should().NotContain("-c:a");
    }

    [Fact]
    public void LowLatency_builds_fmp4_pipe_args() =>
        FfmpegArgs.LowLatency("rtsp://x/y").Should().Equal(
            "-nostdin", "-hide_banner", "-loglevel", "warning", "-rtsp_transport", "tcp", "-i", "rtsp://x/y",
            "-map", "0:v", "-c:v", "copy", "-an", "-f", "mp4",
            "-movflags", "frag_keyframe+empty_moov+default_base_moof", "-frag_duration", "200000", "-min_frag_duration", "100000",
            "pipe:1");

    [Fact]
    public void Recording_with_audio_adds_aac()
    {
        var args = FfmpegArgs.Recording("rtsp://h/x", "rec", "live", 10, 2, true);

        args.Should().ContainInOrder("-map", "0:a?", "-c:a", "aac", "-b:a", "64k");
        args.Should().NotContain("-an");
    }

    [Fact]
    public void NormalizePath_converts_backslashes()
        => FfmpegArgs.NormalizePath(@"data\rec\a\").Should().Be("data/rec/a");

    [Fact]
    public void NormalizePath_rejects_colon()
        => FluentActions.Invoking(() => FfmpegArgs.NormalizePath("D:/data"))
            .Should().Throw<ArgumentException>().WithMessage("*relative*");
}

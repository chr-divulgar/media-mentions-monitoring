namespace MediaOpsCore.Workers.Operations;

internal sealed record TvVideoRecordingPlan(string OutputPath, TimeSpan Duration);

/// <summary>
/// Pure planning for TV video (RQ-011): one MP4 per source per clock hour, in the same day/source
/// folder the audio capture writes its .opus to — so the retention purge covers it unchanged.
/// </summary>
internal static class TvVideoRecording
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    // A recording that would last only seconds before the hour turns is merged into the next hour
    // instead of producing a near-empty file.
    private static readonly TimeSpan MinimumRecording = TimeSpan.FromMinutes(1);

    public static TvVideoRecordingPlan Plan(string outputRootPath, string media, string sourceId, DateTimeOffset now)
    {
        var nextHour = InProcessFfmpegAudioCapturePlugin.AlignWindow(now, Hour) + Hour;
        var duration = nextHour - now;
        if (duration < MinimumRecording)
        {
            duration += Hour;
        }

        var directory = Path.Combine(
            outputRootPath,
            InProcessFfmpegAudioCapturePlugin.BuildMediaDirectoryName(media),
            now.ToString("yyyy"),
            now.ToString("MM"),
            now.ToString("dd"),
            sourceId);

        return new TvVideoRecordingPlan(Path.Combine(directory, $"{sourceId}_{now:yyyy-MM-dd_HH-mm-ss}.mp4"), duration);
    }

    // Stream copy, no re-encoding: CPU stays near zero and quality is the source's own. Fragmented
    // MP4 keeps everything written so far playable if the process dies mid-hour. YouTube live serves
    // video and audio as two separate HLS streams, so there may be two inputs to mux; HLS carries
    // ADTS AAC, which MP4 only accepts after aac_adtstoasc (ffmpeg 5 does not insert it by itself).
    public static IReadOnlyList<string> FfmpegArguments(IReadOnlyList<string> inputUrls, TimeSpan duration, string outputPath)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y" };
        foreach (var url in inputUrls)
        {
            args.AddRange(["-i", url]);
        }

        args.AddRange(["-t", ((int)Math.Ceiling(duration.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (inputUrls.Count > 1)
        {
            args.AddRange(["-map", "0:v:0", "-map", "1:a:0"]);
        }

        args.AddRange(["-c", "copy", "-bsf:a", "aac_adtstoasc", "-movflags", "+frag_keyframe+empty_moov+default_base_moof", "-f", "mp4", outputPath]);
        return args;
    }

    // Separate best video (height-capped to bound disk use, ~0.25 GB/h measured at 480p) + best
    // audio, which is what YouTube live actually offers; a muxed single stream as fallback.
    public static string YtdlpVideoFormat(int maxHeight) =>
        $"bestvideo[height<={maxHeight}]+bestaudio/best[height<={maxHeight}]/best";
}

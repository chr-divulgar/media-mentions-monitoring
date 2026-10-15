using System.Text;
using FFmpeg.AutoGen;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Turns a closed TV recording into a regular MP4, in-process and without re-encoding. The hour being recorded is a
/// fragmented MP4 (so a crash keeps everything up to the last fragment), but its header only describes the first
/// fragment: players read a duration of about a second and do not allow moving through it. Copying the packets into a
/// regular MP4 with the index up front (faststart) gives it its real duration and lets it be sought at once.
/// </summary>
internal static unsafe class Mp4Finalizer
{
    // The converted file's streams may differ from the original's by this much (the muxer rounds the last frame).
    private const double LengthToleranceSeconds = 1.0;

    /// <summary>Whether the file's header marks it as fragmented (its moov box carries an mvex box).</summary>
    public static bool IsFragmented(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var header = new byte[16];
        while (file.Position + 8 <= file.Length)
        {
            var start = file.Position;
            if (file.Read(header, 0, 8) < 8)
            {
                return false;
            }

            long size = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
            var type = Encoding.ASCII.GetString(header, 4, 4);
            if (size == 1)
            {
                if (file.Read(header, 8, 8) < 8)
                {
                    return false;
                }

                size = 0;
                for (var i = 8; i < 16; i++)
                {
                    size = (size << 8) | header[i];
                }
            }
            else if (size == 0)
            {
                size = file.Length - start; // the last box runs to the end of the file
            }

            if (size < 8)
            {
                return false; // not an MP4
            }

            if (type == "moov")
            {
                var body = new byte[Math.Min(size, 16 * 1024 * 1024)];
                file.Position = start;
                file.ReadExactly(body, 0, body.Length);
                return Encoding.Latin1.GetString(body).Contains("mvex", StringComparison.Ordinal);
            }

            file.Position = start + size;
        }

        return false;
    }

    /// <summary>
    /// Replaces the fragmented file by a regular copy of itself. False when there was nothing to do (not fragmented,
    /// unreadable) or the copy did not check out: the original is then left exactly as it was.
    /// </summary>
    public static bool Finalize(string path, CancellationToken cancellationToken)
    {
        if (!IsFragmented(path))
        {
            return false;
        }

        var temporary = path + ".finalizing";
        try
        {
            Copy(path, temporary, cancellationToken);
            if (!SameContent(path, temporary))
            {
                return false;
            }

            var written = File.GetLastWriteTimeUtc(path);
            File.Move(temporary, path, overwrite: true);
            File.SetLastWriteTimeUtc(path, written); // a quiet file stays "closed" for the upload's idle check
            return true;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static bool SameContent(string original, string copy)
    {
        var a = Mp4Info.Read(original, scanStreams: true);
        var b = Mp4Info.Read(copy, scanStreams: true);
        return a.HasVideo == b.HasVideo
            && a.HasAudio == b.HasAudio
            && Math.Abs(a.VideoDurationSeconds - b.VideoDurationSeconds) <= LengthToleranceSeconds
            && Math.Abs(a.AudioDurationSeconds - b.AudioDurationSeconds) <= LengthToleranceSeconds
            && Math.Abs(b.DurationSeconds - Math.Max(b.VideoDurationSeconds, b.AudioStartSeconds + b.AudioDurationSeconds)) <= LengthToleranceSeconds + 1.0;
    }

    private static void Copy(string inputPath, string outputPath, CancellationToken cancellationToken)
    {
        InProcessFfmpegAudioCapturePlugin.EnsureFfmpegInitialized();

        AVFormatContext* input = null;
        AVFormatContext* output = null;
        var packet = ffmpeg.av_packet_alloc();
        var wroteHeader = false;
        try
        {
            ffmpeg.avformat_open_input(&input, inputPath, null, null).ThrowIfError("avformat_open_input");
            ffmpeg.avformat_find_stream_info(input, null).ThrowIfError("avformat_find_stream_info");

            ffmpeg.avformat_alloc_output_context2(&output, null, "mp4", outputPath).ThrowIfError("avformat_alloc_output_context2");
            var map = new int[input->nb_streams];
            var next = 0;
            for (var i = 0; i < map.Length; i++)
            {
                var stream = input->streams[i];
                var type = stream->codecpar->codec_type;
                if (type is not (AVMediaType.AVMEDIA_TYPE_VIDEO or AVMediaType.AVMEDIA_TYPE_AUDIO))
                {
                    map[i] = -1;
                    continue;
                }

                var outStream = ffmpeg.avformat_new_stream(output, null);
                if (outStream is null)
                {
                    throw new InvalidOperationException("avformat_new_stream failed.");
                }

                ffmpeg.avcodec_parameters_copy(outStream->codecpar, stream->codecpar).ThrowIfError("avcodec_parameters_copy");
                outStream->codecpar->codec_tag = 0; // let the MP4 muxer pick its own tag
                outStream->time_base = stream->time_base;
                map[i] = next++;
            }

            ffmpeg.avio_open(&output->pb, outputPath, ffmpeg.AVIO_FLAG_WRITE).ThrowIfError("avio_open");
            AVDictionary* muxOptions = null;
            ffmpeg.av_dict_set(&muxOptions, "movflags", "+faststart", 0);
            var headerResult = ffmpeg.avformat_write_header(output, &muxOptions);
            ffmpeg.av_dict_free(&muxOptions);
            headerResult.ThrowIfError("avformat_write_header");
            wroteHeader = true;

            while (ffmpeg.av_read_frame(input, packet) >= 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var index = packet->stream_index;
                if (map[index] < 0)
                {
                    ffmpeg.av_packet_unref(packet);
                    continue;
                }

                ffmpeg.av_packet_rescale_ts(packet, input->streams[index]->time_base, output->streams[map[index]]->time_base);
                packet->stream_index = map[index];
                packet->pos = -1;
                _ = ffmpeg.av_interleaved_write_frame(output, packet);
            }

            ffmpeg.av_write_trailer(output).ThrowIfError("av_write_trailer");
            wroteHeader = false;
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
            if (output is not null)
            {
                if (wroteHeader)
                {
                    ffmpeg.av_write_trailer(output);
                }

                if (output->pb is not null)
                {
                    ffmpeg.avio_closep(&output->pb);
                }

                ffmpeg.avformat_free_context(output);
            }

            ffmpeg.avformat_close_input(&input);
        }
    }
}

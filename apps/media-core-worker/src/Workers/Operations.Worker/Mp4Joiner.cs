using FFmpeg.AutoGen;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Joins clips of consecutive TV recordings into one MP4, in-process and without re-encoding (the editor's window can
/// cross from one hourly file into the next). Each clip is written at its own offset from the first, so a hole between
/// two recordings stays a hole of the same length and nothing after it moves out of step with the clock. The result is a
/// regular MP4 with the index up front. Only clips that share their codecs, picture size and audio format can be one
/// file; otherwise nothing is written.
/// </summary>
internal static unsafe class Mp4Joiner
{
    private readonly record struct Format(
        AVCodecID VideoCodec, int Width, int Height, AVCodecID AudioCodec, int SampleRate, int Channels, bool HasVideo, bool HasAudio);

    // False (and no output file) when the clips do not match or cannot be read.
    public static bool TryJoin(IReadOnlyList<(string Path, double OffsetSeconds)> clips, string outputPath)
    {
        if (clips.Count == 0)
        {
            return false;
        }

        InProcessFfmpegAudioCapturePlugin.EnsureFfmpegInitialized();

        try
        {
            var expected = Describe(clips[0].Path);
            if (!expected.HasVideo || clips.Skip(1).Any(clip => Describe(clip.Path) != expected))
            {
                return false;
            }

            Write(clips, outputPath);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            TryDelete(outputPath);
            return false;
        }
    }

    private static Format Describe(string path)
    {
        AVFormatContext* input = null;
        try
        {
            ffmpeg.avformat_open_input(&input, path, null, null).ThrowIfError("avformat_open_input");
            ffmpeg.avformat_find_stream_info(input, null).ThrowIfError("avformat_find_stream_info");
            var video = BestStream(input, AVMediaType.AVMEDIA_TYPE_VIDEO);
            var audio = BestStream(input, AVMediaType.AVMEDIA_TYPE_AUDIO);
            return new Format(
                video is null ? AVCodecID.AV_CODEC_ID_NONE : video->codecpar->codec_id,
                video is null ? 0 : video->codecpar->width,
                video is null ? 0 : video->codecpar->height,
                audio is null ? AVCodecID.AV_CODEC_ID_NONE : audio->codecpar->codec_id,
                audio is null ? 0 : audio->codecpar->sample_rate,
                audio is null ? 0 : audio->codecpar->ch_layout.nb_channels,
                video is not null,
                audio is not null);
        }
        finally
        {
            ffmpeg.avformat_close_input(&input);
        }
    }

    private static AVStream* BestStream(AVFormatContext* input, AVMediaType type)
    {
        var index = ffmpeg.av_find_best_stream(input, type, -1, -1, null, 0);
        return index < 0 ? null : input->streams[index];
    }

    private static void Write(IReadOnlyList<(string Path, double OffsetSeconds)> clips, string outputPath)
    {
        AVFormatContext* output = null;
        var packet = ffmpeg.av_packet_alloc();
        var wroteHeader = false;
        try
        {
            ffmpeg.avformat_alloc_output_context2(&output, null, "mp4", outputPath).ThrowIfError("avformat_alloc_output_context2");

            // The streams are the first clip's (video, then audio when there is one); the others match them.
            AVFormatContext* first = null;
            try
            {
                ffmpeg.avformat_open_input(&first, clips[0].Path, null, null).ThrowIfError("avformat_open_input");
                ffmpeg.avformat_find_stream_info(first, null).ThrowIfError("avformat_find_stream_info");
                AddStream(output, BestStream(first, AVMediaType.AVMEDIA_TYPE_VIDEO));
                var audio = BestStream(first, AVMediaType.AVMEDIA_TYPE_AUDIO);
                if (audio is not null)
                {
                    AddStream(output, audio);
                }
            }
            finally
            {
                ffmpeg.avformat_close_input(&first);
            }

            ffmpeg.avio_open(&output->pb, outputPath, ffmpeg.AVIO_FLAG_WRITE).ThrowIfError("avio_open");
            AVDictionary* muxOptions = null;
            ffmpeg.av_dict_set(&muxOptions, "movflags", "+faststart", 0);
            var headerResult = ffmpeg.avformat_write_header(output, &muxOptions);
            ffmpeg.av_dict_free(&muxOptions);
            headerResult.ThrowIfError("avformat_write_header");
            wroteHeader = true;

            foreach (var (path, offsetSeconds) in clips)
            {
                CopyClip(path, offsetSeconds, output, packet);
            }

            ffmpeg.av_write_trailer(output).ThrowIfError("av_write_trailer");
            wroteHeader = false;
        }
        catch
        {
            TryDelete(outputPath);
            throw;
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
        }
    }

    // The clip's video and audio, moved by its offset from the first clip, onto the output's streams (video 0, audio 1).
    private static void CopyClip(string path, double offsetSeconds, AVFormatContext* output, AVPacket* packet)
    {
        AVFormatContext* input = null;
        try
        {
            ffmpeg.avformat_open_input(&input, path, null, null).ThrowIfError("avformat_open_input");
            ffmpeg.avformat_find_stream_info(input, null).ThrowIfError("avformat_find_stream_info");

            var map = new int[input->nb_streams];
            Array.Fill(map, -1);
            var video = ffmpeg.av_find_best_stream(input, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            var audio = ffmpeg.av_find_best_stream(input, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            if (video >= 0)
            {
                map[video] = 0;
            }

            if (audio >= 0 && output->nb_streams > 1)
            {
                map[audio] = 1;
            }

            while (ffmpeg.av_read_frame(input, packet) >= 0)
            {
                var index = packet->stream_index;
                if (index >= map.Length || map[index] < 0)
                {
                    ffmpeg.av_packet_unref(packet);
                    continue;
                }

                var inStream = input->streams[index];
                var outStream = output->streams[map[index]];
                var shift = (long)Math.Round(offsetSeconds / ffmpeg.av_q2d(inStream->time_base));
                if (packet->pts != ffmpeg.AV_NOPTS_VALUE) packet->pts += shift;
                if (packet->dts != ffmpeg.AV_NOPTS_VALUE) packet->dts += shift;

                ffmpeg.av_packet_rescale_ts(packet, inStream->time_base, outStream->time_base);
                packet->stream_index = map[index];
                packet->pos = -1;
                _ = ffmpeg.av_interleaved_write_frame(output, packet);
            }
        }
        finally
        {
            ffmpeg.avformat_close_input(&input);
        }
    }

    private static void AddStream(AVFormatContext* output, AVStream* source)
    {
        var stream = ffmpeg.avformat_new_stream(output, null);
        if (stream is null)
        {
            throw new InvalidOperationException("avformat_new_stream failed.");
        }

        ffmpeg.avcodec_parameters_copy(stream->codecpar, source->codecpar).ThrowIfError("avcodec_parameters_copy");
        stream->codecpar->codec_tag = 0; // let the MP4 muxer pick its own tag
        stream->time_base = source->time_base;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Left for the temp folder's cleanup.
        }
    }
}

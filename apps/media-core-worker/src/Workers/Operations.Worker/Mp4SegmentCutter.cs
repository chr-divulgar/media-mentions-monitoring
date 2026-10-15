using FFmpeg.AutoGen;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Where a cut clip starts inside the recording it was taken from (a keyframe, so possibly earlier
/// than the start that was asked for) and how long it is.
/// </summary>
internal sealed record Mp4ClipResult(double StartSeconds, double DurationSeconds);

/// <summary>What a finished MP4 contains; used to verify clips and report their real length.</summary>
internal sealed record Mp4Info(double DurationSeconds, bool HasVideo, bool HasAudio, bool FirstVideoPacketIsKeyframe)
{
    public int VideoWidth { get; init; }

    public int VideoHeight { get; init; }

    public string? AudioCodec { get; init; }

    public int AudioChannels { get; init; }

    public int AudioSampleRate { get; init; }

    // Only filled in when the whole file is scanned (scanStreams): where each stream starts and how long it lasts.
    public double VideoStartSeconds { get; init; }

    public double VideoDurationSeconds { get; init; }

    public double AudioStartSeconds { get; init; }

    public double AudioDurationSeconds { get; init; }

    public static unsafe Mp4Info Read(string path, bool scanStreams = false)
    {
        InProcessFfmpegAudioCapturePlugin.EnsureFfmpegInitialized();

        AVFormatContext* input = null;
        var packet = ffmpeg.av_packet_alloc();
        try
        {
            ffmpeg.avformat_open_input(&input, path, null, null).ThrowIfError("avformat_open_input");
            ffmpeg.avformat_find_stream_info(input, null).ThrowIfError("avformat_find_stream_info");

            var videoIndex = ffmpeg.av_find_best_stream(input, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            var audioIndex = ffmpeg.av_find_best_stream(input, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            var duration = input->duration == ffmpeg.AV_NOPTS_VALUE ? 0 : input->duration / (double)ffmpeg.AV_TIME_BASE;

            var firstIsKey = false;
            var seenVideo = false;
            double videoStart = double.MaxValue, videoEnd = 0, audioStart = double.MaxValue, audioEnd = 0;
            while ((videoIndex >= 0 || scanStreams) && ffmpeg.av_read_frame(input, packet) >= 0)
            {
                var index = packet->stream_index;
                var isVideo = index == videoIndex;
                var isKey = (packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;

                if (scanStreams && (isVideo || index == audioIndex) && packet->pts != ffmpeg.AV_NOPTS_VALUE)
                {
                    var stream = input->streams[index];
                    var start = packet->pts * ffmpeg.av_q2d(stream->time_base);
                    var end = start + Math.Max(0, packet->duration) * ffmpeg.av_q2d(stream->time_base);
                    if (isVideo)
                    {
                        videoStart = Math.Min(videoStart, start);
                        videoEnd = Math.Max(videoEnd, end);
                    }
                    else
                    {
                        audioStart = Math.Min(audioStart, start);
                        audioEnd = Math.Max(audioEnd, end);
                    }
                }

                ffmpeg.av_packet_unref(packet);
                if (isVideo && !seenVideo)
                {
                    seenVideo = true;
                    firstIsKey = isKey;
                }

                if (seenVideo && !scanStreams)
                {
                    break;
                }
            }

            var video = videoIndex >= 0 ? input->streams[videoIndex]->codecpar : null;
            var audio = audioIndex >= 0 ? input->streams[audioIndex]->codecpar : null;
            return new Mp4Info(duration, videoIndex >= 0, audioIndex >= 0, firstIsKey)
            {
                VideoWidth = video is null ? 0 : video->width,
                VideoHeight = video is null ? 0 : video->height,
                AudioCodec = audio is null ? null : ffmpeg.avcodec_get_name(audio->codec_id),
                AudioChannels = audio is null ? 0 : audio->ch_layout.nb_channels,
                AudioSampleRate = audio is null ? 0 : audio->sample_rate,
                VideoStartSeconds = videoStart == double.MaxValue ? 0 : videoStart,
                VideoDurationSeconds = videoEnd - (videoStart == double.MaxValue ? 0 : videoStart),
                AudioStartSeconds = audioStart == double.MaxValue ? 0 : audioStart,
                AudioDurationSeconds = audioEnd - (audioStart == double.MaxValue ? 0 : audioStart),
            };
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
            ffmpeg.avformat_close_input(&input);
        }
    }
}

/// <summary>
/// Cuts a time window out of an MP4 recording, in-process, with the FFmpeg libraries the worker
/// already ships — no ffmpeg.exe. Stream copy (no decoding or encoding), so it is fast and lossless:
/// the clip therefore starts at the video keyframe at or before the requested start, while the end is
/// exact to the frame. The output is a regular MP4 with the index up front (faststart) so a browser
/// can play and seek it right away.
///
/// The recordings are fragmented MP4, and the one being written right now has no index at its end, so
/// seeking is not reliable. Instead the file is read from its start, keeping only the latest group of
/// pictures in memory: the moment a keyframe past the requested start shows up, the buffered group is
/// the one that holds the start.
/// </summary>
internal static unsafe class Mp4SegmentCutter
{
    // How far past the last packet of the recording a start may be and still count as "inside it".
    private const double StartToleranceSeconds = 1.0;

    private struct Buffered
    {
        public AVPacket* Packet;
        public int Stream;
        public double Seconds;
    }

    // Null when the requested start is beyond the end of the recording (nothing to cut).
    public static Mp4ClipResult? Cut(
        string inputPath, double startSeconds, double endSeconds, string outputPath, CancellationToken cancellationToken)
    {
        InProcessFfmpegAudioCapturePlugin.EnsureFfmpegInitialized();

        AVFormatContext* input = null;
        AVFormatContext* output = null;
        var buffer = new List<Buffered>();
        AVPacket* carry = null;
        var packet = ffmpeg.av_packet_alloc();
        var wroteHeader = false;

        try
        {
            ffmpeg.avformat_open_input(&input, inputPath, null, null).ThrowIfError("avformat_open_input");
            ffmpeg.avformat_find_stream_info(input, null).ThrowIfError("avformat_find_stream_info");

            var videoIndex = ffmpeg.av_find_best_stream(input, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            var audioIndex = ffmpeg.av_find_best_stream(input, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            if (videoIndex < 0)
            {
                throw new InvalidOperationException($"'{inputPath}' has no video stream.");
            }

            var origin = input->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : input->start_time / (double)ffmpeg.AV_TIME_BASE;

            // Phase 1: scan up to the first video keyframe past the requested start, keeping the group
            // of pictures (and the audio) that holds the start.
            var keySeconds = double.NaN;
            var lastVideoSeconds = double.NegativeInfinity;
            while (ffmpeg.av_read_frame(input, packet) >= 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var stream = packet->stream_index;
                if (stream != videoIndex && stream != audioIndex)
                {
                    ffmpeg.av_packet_unref(packet);
                    continue;
                }

                var seconds = PacketSeconds(input, packet) - origin;
                if (stream == videoIndex)
                {
                    lastVideoSeconds = Math.Max(lastVideoSeconds, seconds);
                    if ((packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0)
                    {
                        if (seconds > startSeconds + 1e-6 && !double.IsNaN(keySeconds))
                        {
                            carry = ffmpeg.av_packet_alloc();
                            ffmpeg.av_packet_move_ref(carry, packet);
                            break;
                        }

                        // A keyframe at or before the start: it opens a newer group, the older one goes.
                        DropBefore(buffer, seconds);
                        keySeconds = seconds;
                    }
                    else if (double.IsNaN(keySeconds))
                    {
                        ffmpeg.av_packet_unref(packet); // before the first keyframe: undecodable
                        continue;
                    }
                }

                Keep(buffer, packet, stream, seconds);
            }

            if (double.IsNaN(keySeconds) || startSeconds > lastVideoSeconds + StartToleranceSeconds)
            {
                return null;
            }

            // Phase 2: write the buffered group, then keep copying until the end of the window.
            ffmpeg.avformat_alloc_output_context2(&output, null, "mp4", outputPath).ThrowIfError("avformat_alloc_output_context2");
            ffmpeg.av_opt_set(output, "avoid_negative_ts", "make_zero", ffmpeg.AV_OPT_SEARCH_CHILDREN);

            var outVideo = AddStream(output, input->streams[videoIndex]);
            var outAudio = audioIndex >= 0 ? AddStream(output, input->streams[audioIndex]) : -1;

            ffmpeg.avio_open(&output->pb, outputPath, ffmpeg.AVIO_FLAG_WRITE).ThrowIfError("avio_open");
            AVDictionary* muxOptions = null;
            ffmpeg.av_dict_set(&muxOptions, "movflags", "+faststart", 0);
            var headerResult = ffmpeg.avformat_write_header(output, &muxOptions);
            ffmpeg.av_dict_free(&muxOptions);
            headerResult.ThrowIfError("avformat_write_header");
            wroteHeader = true;

            var videoBase = BaseTimestamp(input->streams[videoIndex], keySeconds + origin);
            var audioBase = audioIndex >= 0 ? BaseTimestamp(input->streams[audioIndex], keySeconds + origin) : 0;
            var clipEnd = keySeconds;

            var videoDone = false;
            var audioDone = audioIndex < 0;

            foreach (var item in buffer)
            {
                var past = item.Seconds > endSeconds;
                if (item.Stream == videoIndex ? past : past || item.Seconds < keySeconds)
                {
                    FreePacket(item.Packet);
                    continue;
                }

                WritePacket(input, output, item.Packet, item.Stream == videoIndex, outVideo, outAudio, videoBase, audioBase, item.Seconds, ref clipEnd);
                FreePacket(item.Packet);
            }

            buffer.Clear();

            AVPacket* current = carry;
            carry = null;
            while (!(videoDone && audioDone))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (current is null)
                {
                    if (ffmpeg.av_read_frame(input, packet) < 0)
                    {
                        break;
                    }

                    current = packet;
                }

                var stream = current->stream_index;
                var seconds = PacketSeconds(input, current) - origin;
                if (stream == videoIndex)
                {
                    if (seconds > endSeconds)
                    {
                        videoDone = true;
                    }
                    else
                    {
                        WritePacket(input, output, current, isVideo: true, outVideo, outAudio, videoBase, audioBase, seconds, ref clipEnd);
                    }
                }
                else if (stream == audioIndex)
                {
                    if (seconds > endSeconds)
                    {
                        audioDone = true;
                    }
                    else if (seconds >= keySeconds)
                    {
                        WritePacket(input, output, current, isVideo: false, outVideo, outAudio, videoBase, audioBase, seconds, ref clipEnd);
                    }
                }

                if (current == packet)
                {
                    ffmpeg.av_packet_unref(packet);
                }
                else
                {
                    FreePacket(current);
                }

                current = null;
            }

            ffmpeg.av_write_trailer(output);
            wroteHeader = false;
            return new Mp4ClipResult(keySeconds, Math.Max(0, clipEnd - keySeconds));
        }
        finally
        {
            foreach (var item in buffer)
            {
                FreePacket(item.Packet);
            }

            if (carry is not null)
            {
                FreePacket(carry);
            }

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

    // Rebases the packet so the clip starts at zero, and hands it to the muxer (which takes its data).
    private static void WritePacket(
        AVFormatContext* input, AVFormatContext* output, AVPacket* source, bool isVideo,
        int outVideo, int outAudio, long videoBase, long audioBase, double seconds, ref double clipEnd)
    {
        var inStream = input->streams[source->stream_index];
        var outStream = output->streams[isVideo ? outVideo : outAudio];
        var shift = isVideo ? videoBase : audioBase;
        if (source->pts != ffmpeg.AV_NOPTS_VALUE) source->pts -= shift;
        if (source->dts != ffmpeg.AV_NOPTS_VALUE) source->dts -= shift;

        var durationSeconds = source->duration * ffmpeg.av_q2d(inStream->time_base);
        ffmpeg.av_packet_rescale_ts(source, inStream->time_base, outStream->time_base);
        source->stream_index = outStream->index;
        source->pos = -1;
        if (ffmpeg.av_interleaved_write_frame(output, source) >= 0 && isVideo)
        {
            clipEnd = Math.Max(clipEnd, seconds + durationSeconds);
        }
    }

    private static double PacketSeconds(AVFormatContext* input, AVPacket* packet)
    {
        var timestamp = packet->pts != ffmpeg.AV_NOPTS_VALUE ? packet->pts : packet->dts;
        return timestamp == ffmpeg.AV_NOPTS_VALUE ? 0 : timestamp * ffmpeg.av_q2d(input->streams[packet->stream_index]->time_base);
    }

    // The timestamp in the stream's own time base that corresponds to `seconds` of the container.
    private static long BaseTimestamp(AVStream* stream, double seconds) =>
        (long)Math.Round(seconds / ffmpeg.av_q2d(stream->time_base));

    private static int AddStream(AVFormatContext* output, AVStream* source)
    {
        var stream = ffmpeg.avformat_new_stream(output, null);
        if (stream is null)
        {
            throw new InvalidOperationException("avformat_new_stream failed.");
        }

        ffmpeg.avcodec_parameters_copy(stream->codecpar, source->codecpar).ThrowIfError("avcodec_parameters_copy");
        stream->codecpar->codec_tag = 0; // let the MP4 muxer pick its own tag
        stream->time_base = source->time_base;
        return stream->index;
    }

    // Takes the packet's data (the reader's own packet is reused for the next read).
    private static void Keep(List<Buffered> buffer, AVPacket* packet, int stream, double seconds)
    {
        var copy = ffmpeg.av_packet_alloc();
        ffmpeg.av_packet_move_ref(copy, packet);
        buffer.Add(new Buffered { Packet = copy, Stream = stream, Seconds = seconds });
    }

    // A new keyframe starts a new group: whatever is older than it (the previous group's video and
    // the audio from before it) is no longer needed.
    private static void DropBefore(List<Buffered> buffer, double keySeconds)
    {
        for (var i = buffer.Count - 1; i >= 0; i--)
        {
            if (buffer[i].Seconds < keySeconds)
            {
                FreePacket(buffer[i].Packet);
                buffer.RemoveAt(i);
            }
        }
    }

    private static void FreePacket(AVPacket* packet)
    {
        var p = packet;
        ffmpeg.av_packet_free(&p);
    }
}

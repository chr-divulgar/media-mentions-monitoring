using FFmpeg.AutoGen;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Copies a live stream into an MP4 for a fixed duration, in-process, with the FFmpeg libraries the
/// worker already ships (runtimes/win-x64/native) — the same ones the audio capture uses, so there
/// is no ffmpeg.exe to install on the host. The video is always stream-copied (no decoding or encoding, so
/// no CPU); the audio is copied too unless TvAudioSettings is given, in which case TvAudioReencoder stores
/// it as a small mono AAC track instead of YouTube's 128 kbps stereo.
///
/// YouTube live serves video and audio as two separate HLS streams, so there can be two inputs:
/// the best video stream is taken from the first and the best audio stream from the last. They are opened
/// together, and all share one origin (the earliest start): HLS starts each list at its own latest 5 s segment, so
/// the audio can begin one segment after the video, and rebasing each input by its own start would erase that real
/// delay and leave the sound ahead of the picture for the whole hour.
/// </summary>
internal static unsafe class InProcessMp4Recorder
{
    // Starts further apart than this are not one stream's segments but unrelated clocks: each is rebased on its own.
    private const long MaxSharedOriginGapMicroseconds = 30_000_000;

    // A start gap above this is worth a log line: it is a real delay between picture and sound at the start.
    private const long ReportedGapMicroseconds = 500_000;

    private const string ReadTimeoutMicroseconds = "30000000";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0 Safari/537.36";

    // Returns null when the recording reached its planned duration (or was cancelled), otherwise
    // why it stopped early. Throws only for setup failures (bad input, no stream, cannot write).
    public static string? Record(
        IReadOnlyList<string> inputUrls, TimeSpan duration, string outputPath, CancellationToken cancellationToken,
        TvAudioSettings? audio = null, Action<string>? log = null)
    {
        if (inputUrls.Count == 0)
        {
            throw new ArgumentException("At least one input URL is required.", nameof(inputUrls));
        }

        InProcessFfmpegAudioCapturePlugin.EnsureFfmpegInitialized();

        var inputs = new AVFormatContext*[inputUrls.Count];
        var offsets = new long[inputUrls.Count];
        // input index -> (input stream index -> output stream index), -1 when not recorded
        var streamMaps = new int[inputUrls.Count][];
        AVFormatContext* output = null;
        var packet = ffmpeg.av_packet_alloc();
        TvAudioReencoder? reencoder = null;
        var reencoderInput = -1;

        try
        {
            // Opened together so they usually land in the same segment; a failure closes whatever did open.
            var opened = new nint[inputUrls.Count];
            try
            {
                Parallel.For(0, inputUrls.Count, i => opened[i] = (nint)OpenInput(inputUrls[i]));
            }
            catch
            {
                foreach (var handle in opened.Where(h => h != 0))
                {
                    var open = (AVFormatContext*)handle;
                    ffmpeg.avformat_close_input(&open);
                }

                throw;
            }

            for (var i = 0; i < inputUrls.Count; i++)
            {
                inputs[i] = (AVFormatContext*)opened[i];
                streamMaps[i] = Enumerable.Repeat(-1, (int)inputs[i]->nb_streams).ToArray();
            }

            SetOffsets(inputs, offsets, inputUrls.Count > 1 ? log : null);

            ffmpeg.avformat_alloc_output_context2(&output, null, "mp4", outputPath).ThrowIfError("avformat_alloc_output_context2");

            var nextOutputIndex = 0;
            MapBestStream(inputs, streamMaps, output, AVMediaType.AVMEDIA_TYPE_VIDEO, preferInput: 0, ref nextOutputIndex);
            if (audio is not null)
            {
                reencoder = TvAudioReencoder.TryCreate(inputs, offsets, output, audio, preferInput: inputs.Length - 1, out reencoderInput);
                if (reencoder is not null)
                {
                    nextOutputIndex++;
                }
            }

            // No re-encoding asked for, or the audio cannot be decoded here: copy it as it comes.
            if (reencoder is null)
            {
                MapBestStream(inputs, streamMaps, output, AVMediaType.AVMEDIA_TYPE_AUDIO, preferInput: inputs.Length - 1, ref nextOutputIndex);
            }

            if (nextOutputIndex == 0)
            {
                throw new InvalidOperationException("The stream has neither video nor audio.");
            }

            ffmpeg.avio_open(&output->pb, outputPath, ffmpeg.AVIO_FLAG_WRITE).ThrowIfError("avio_open");

            // Fragmented MP4: each fragment is flushed at a keyframe, so a recording cut off by a
            // crash stays playable up to its last fragment. No empty_moov: the moov is written with
            // the first fragment, once the muxer has the AAC config from the (automatically
            // inserted) aac_adtstoasc filter — HLS carries ADTS AAC, which MP4 cannot store as-is.
            AVDictionary* muxOptions = null;
            ffmpeg.av_dict_set(&muxOptions, "movflags", "frag_keyframe+default_base_moof", 0);
            var headerResult = ffmpeg.avformat_write_header(output, &muxOptions);
            ffmpeg.av_dict_free(&muxOptions);
            headerResult.ThrowIfError("avformat_write_header");

            var stopReason = CopyPackets(inputs, offsets, streamMaps, output, packet, duration, cancellationToken, reencoder, reencoderInput);
            reencoder?.Finish();
            ffmpeg.av_write_trailer(output);
            return stopReason;
        }
        finally
        {
            reencoder?.Dispose();
            ffmpeg.av_packet_free(&packet);
            for (var i = 0; i < inputs.Length; i++)
            {
                var input = inputs[i];
                if (input is not null)
                {
                    ffmpeg.avformat_close_input(&input);
                }
            }

            if (output is not null)
            {
                if (output->pb is not null)
                {
                    ffmpeg.avio_closep(&output->pb);
                }

                ffmpeg.avformat_free_context(output);
            }
        }
    }

    // One origin for every input: the earliest start. The one that began later keeps its real delay.
    private static void SetOffsets(AVFormatContext*[] inputs, long[] offsets, Action<string>? log)
    {
        var starts = new long[inputs.Length];
        for (var i = 0; i < inputs.Length; i++)
        {
            starts[i] = inputs[i]->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : inputs[i]->start_time;
        }

        var gap = starts.Max() - starts.Min();
        var shared = gap <= MaxSharedOriginGapMicroseconds;
        for (var i = 0; i < inputs.Length; i++)
        {
            offsets[i] = -(shared ? starts.Min() : starts[i]);
        }

        if (shared && gap > ReportedGapMicroseconds)
        {
            log?.Invoke($"video and audio started {gap / 1_000_000.0:0.00} s apart; the later one keeps its delay");
        }
    }

    private static AVFormatContext* OpenInput(string url)
    {
        AVFormatContext* input = null;
        AVDictionary* options = null;
        ffmpeg.av_dict_set(&options, "rw_timeout", ReadTimeoutMicroseconds, 0);
        ffmpeg.av_dict_set(&options, "user_agent", UserAgent, 0);
        ffmpeg.av_dict_set(&options, "live_start_index", "-1", 0); // start at the live edge, not the DVR start
        ffmpeg.av_dict_set(&options, "reconnect", "1", 0);
        ffmpeg.av_dict_set(&options, "reconnect_streamed", "1", 0);

        var openResult = ffmpeg.avformat_open_input(&input, url, null, &options);
        ffmpeg.av_dict_free(&options);
        openResult.ThrowIfError("avformat_open_input");

        // null options: a single dictionary here would be read as a per-stream array and crash on
        // multi-stream inputs (see the same note in InProcessFfmpegAudioCapturePlugin).
        var infoResult = ffmpeg.avformat_find_stream_info(input, null);
        if (infoResult < 0)
        {
            ffmpeg.avformat_close_input(&input);
            infoResult.ThrowIfError("avformat_find_stream_info");
        }

        return input;
    }

    private static void MapBestStream(
        AVFormatContext*[] inputs, int[][] streamMaps, AVFormatContext* output, AVMediaType type, int preferInput, ref int nextOutputIndex)
    {
        // Preferred input first, then any other that has the stream type.
        var order = new[] { preferInput }.Concat(Enumerable.Range(0, inputs.Length).Where(i => i != preferInput));
        foreach (var i in order)
        {
            var index = ffmpeg.av_find_best_stream(inputs[i], type, -1, -1, null, 0);
            if (index < 0)
            {
                continue;
            }

            var inStream = inputs[i]->streams[index];
            var outStream = ffmpeg.avformat_new_stream(output, null);
            if (outStream is null)
            {
                throw new InvalidOperationException("avformat_new_stream failed.");
            }

            ffmpeg.avcodec_parameters_copy(outStream->codecpar, inStream->codecpar).ThrowIfError("avcodec_parameters_copy");
            outStream->codecpar->codec_tag = 0; // let the MP4 muxer pick its own tag
            outStream->time_base = inStream->time_base;
            streamMaps[i][index] = nextOutputIndex++;
            return;
        }
    }

    private static string? CopyPackets(
        AVFormatContext*[] inputs, long[] offsets, int[][] streamMaps, AVFormatContext* output, AVPacket* packet,
        TimeSpan duration, CancellationToken cancellationToken, TvAudioReencoder? reencoder, int reencoderInput)
    {
        var limitSeconds = duration.TotalSeconds;
        var wallClockLimit = DateTime.UtcNow + duration + TimeSpan.FromMinutes(2);
        // Last written timestamp per input, in seconds: always read from the input that is behind,
        // so audio and video from two separate connections stay interleaved.
        var position = new double[inputs.Length];
        var ended = new bool[inputs.Length];

        while (!cancellationToken.IsCancellationRequested)
        {
            var next = -1;
            for (var i = 0; i < inputs.Length; i++)
            {
                if (!ended[i] && (next < 0 || position[i] < position[next]))
                {
                    next = i;
                }
            }

            if (next < 0)
            {
                return "el stream terminó antes de completar la hora";
            }

            if (position.Where((_, i) => !ended[i]).All(p => p >= limitSeconds) || DateTime.UtcNow > wallClockLimit)
            {
                return null;
            }

            var readResult = ffmpeg.av_read_frame(inputs[next], packet);
            if (readResult < 0)
            {
                ended[next] = true;
                if (readResult != ffmpeg.AVERROR_EOF)
                {
                    return $"lectura interrumpida (FFmpeg {readResult})";
                }

                continue;
            }

            if (reencoder is not null && next == reencoderInput && packet->stream_index == reencoder.InputStreamIndex)
            {
                // The audio is decoded and re-encoded rather than copied; its position still drives which input
                // is read next, so it stays interleaved with the video.
                var audioStream = inputs[next]->streams[packet->stream_index];
                var audioStamp = packet->dts != ffmpeg.AV_NOPTS_VALUE ? packet->dts : packet->pts;
                if (audioStamp != ffmpeg.AV_NOPTS_VALUE)
                {
                    var audioOffset = ffmpeg.av_rescale_q(offsets[next], ffmpeg.av_get_time_base_q(), audioStream->time_base);
                    position[next] = (audioStamp + audioOffset) * ffmpeg.av_q2d(audioStream->time_base);
                }

                reencoder.Write(packet);
                ffmpeg.av_packet_unref(packet);
                continue;
            }

            var outputIndex = packet->stream_index < streamMaps[next].Length ? streamMaps[next][packet->stream_index] : -1;
            if (outputIndex < 0)
            {
                ffmpeg.av_packet_unref(packet);
                continue;
            }

            var inStream = inputs[next]->streams[packet->stream_index];
            var outStream = output->streams[outputIndex];
            var offset = ffmpeg.av_rescale_q(offsets[next], ffmpeg.av_get_time_base_q(), inStream->time_base);
            if (packet->pts != ffmpeg.AV_NOPTS_VALUE) packet->pts += offset;
            if (packet->dts != ffmpeg.AV_NOPTS_VALUE) packet->dts += offset;

            var timestamp = packet->dts != ffmpeg.AV_NOPTS_VALUE ? packet->dts : packet->pts;
            if (timestamp != ffmpeg.AV_NOPTS_VALUE)
            {
                position[next] = timestamp * ffmpeg.av_q2d(inStream->time_base);
            }

            ffmpeg.av_packet_rescale_ts(packet, inStream->time_base, outStream->time_base);
            packet->stream_index = outputIndex;
            packet->pos = -1;

            // Takes ownership of the packet's data; a rejected packet (e.g. a timestamp glitch at a
            // segment boundary) is dropped rather than ending the whole hour.
            _ = ffmpeg.av_interleaved_write_frame(output, packet);
        }

        return null; // cancelled: the trailer is still written, so the partial hour is kept
    }
}

using FFmpeg.AutoGen;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Joins several Opus files that cover consecutive spans of the same rotation window into a single
/// valid Ogg Opus file, by remuxing their packets into one continuous stream (stream copy — the
/// audio is never decoded or re-encoded, so this is cheap and lossless).
///
/// This replaces raw byte concatenation ("Ogg chaining"). Chaining produces a file whose second
/// segment starts its own Ogg bitstream mid-file, and while that is legal Ogg, FFmpeg's demuxer
/// does not reliably follow the chain: it logs "failed to create or replace stream" and everything
/// past the splice decodes as garbage. Since every consumer of these recordings (the transcription
/// pipeline, the Alerts audio-edit endpoint, ffprobe, the browser) goes through that demuxer, a
/// chained file is effectively truncated at the splice point — real audio on disk that nothing can
/// read back. Remuxing into one bitstream avoids the problem entirely.
/// </summary>
internal static unsafe class OpusFileJoiner
{
    public static void Join(IReadOnlyList<string> orderedInputPaths, string outputPath)
    {
        if (orderedInputPaths.Count == 0)
        {
            throw new ArgumentException("At least one input file is required.", nameof(orderedInputPaths));
        }

        InProcessFfmpegAudioCapturePlugin.EnsureFfmpegInitialized();

        AVFormatContext* outputContext = null;
        AVStream* outputStream = null;
        AVPacket* packet = null;

        try
        {
            packet = ffmpeg.av_packet_alloc();
            if (packet is null)
            {
                throw new InvalidOperationException("Unable to allocate FFmpeg packet for the join.");
            }

            // The output stream is described by the first input's codec parameters (OpusHead
            // extradata included) — every file here was produced by the same encoder settings.
            var outputTimeBase = CopyStreamLayout(orderedInputPaths[0], outputPath, ref outputContext, ref outputStream);

            long ptsOffset = 0;
            foreach (var inputPath in orderedInputPaths)
            {
                ptsOffset = AppendPackets(inputPath, outputContext, outputStream, packet, outputTimeBase, ptsOffset);
            }

            ffmpeg.av_write_trailer(outputContext).ThrowIfError("av_write_trailer(join)");
        }
        finally
        {
            if (packet is not null)
            {
                ffmpeg.av_packet_free(&packet);
            }

            if (outputContext is not null)
            {
                if (outputContext->pb is not null)
                {
                    ffmpeg.avio_closep(&outputContext->pb);
                }

                ffmpeg.avformat_free_context(outputContext);
            }
        }
    }

    private static AVRational CopyStreamLayout(
        string firstInputPath, string outputPath, ref AVFormatContext* outputContext, ref AVStream* outputStream)
    {
        AVFormatContext* inputContext = null;
        try
        {
            ffmpeg.avformat_open_input(&inputContext, firstInputPath, null, null)
                .ThrowIfError("avformat_open_input(join_first)");
            ffmpeg.avformat_find_stream_info(inputContext, null).ThrowIfError("avformat_find_stream_info(join_first)");

            var audioStreamIndex = ffmpeg.av_find_best_stream(
                inputContext, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            if (audioStreamIndex < 0)
            {
                throw new InvalidOperationException($"No audio stream found in '{firstInputPath}'.");
            }

            var inputStream = inputContext->streams[audioStreamIndex];

            AVFormatContext* context = null;
            ffmpeg.avformat_alloc_output_context2(&context, null, "opus", outputPath)
                .ThrowIfError("avformat_alloc_output_context2(join)");
            outputContext = context;

            outputStream = ffmpeg.avformat_new_stream(outputContext, null);
            if (outputStream is null)
            {
                throw new InvalidOperationException("Unable to create the joined output stream.");
            }

            ffmpeg.avcodec_parameters_copy(outputStream->codecpar, inputStream->codecpar)
                .ThrowIfError("avcodec_parameters_copy(join)");
            outputStream->codecpar->codec_tag = 0;
            outputStream->time_base = inputStream->time_base;

            ffmpeg.avio_open(&outputContext->pb, outputPath, ffmpeg.AVIO_FLAG_WRITE).ThrowIfError("avio_open(join)");
            ffmpeg.avformat_write_header(outputContext, null).ThrowIfError("avformat_write_header(join)");

            return outputStream->time_base;
        }
        finally
        {
            if (inputContext is not null)
            {
                ffmpeg.avformat_close_input(&inputContext);
            }
        }
    }

    /// <returns>The pts offset the next input should start at, in output time-base units.</returns>
    private static long AppendPackets(
        string inputPath,
        AVFormatContext* outputContext,
        AVStream* outputStream,
        AVPacket* packet,
        AVRational outputTimeBase,
        long ptsOffset)
    {
        AVFormatContext* inputContext = null;
        try
        {
            if (ffmpeg.avformat_open_input(&inputContext, inputPath, null, null) < 0)
            {
                // Nothing to append from this one — keep whatever the previous inputs contributed.
                return ptsOffset;
            }

            if (ffmpeg.avformat_find_stream_info(inputContext, null) < 0)
            {
                return ptsOffset;
            }

            var audioStreamIndex = ffmpeg.av_find_best_stream(
                inputContext, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            if (audioStreamIndex < 0)
            {
                return ptsOffset;
            }

            var inputTimeBase = inputContext->streams[audioStreamIndex]->time_base;

            // Each input carries its own timeline, and not every packet an Ogg stream yields has a
            // usable timestamp (the demuxer derives them from per-page granule positions, so some
            // come through unset, and the first one of an Opus stream sits slightly negative
            // because of the encoder's pre-skip). Rather than pass those straight to the muxer,
            // which rejects them, lay every packet down end to end from where the previous input
            // finished — the timeline the joined file needs is contiguous by construction anyway.
            var nextPts = ptsOffset;
            long firstPts = ffmpeg.AV_NOPTS_VALUE;
            long packetDuration = 0;

            while (true)
            {
                ffmpeg.av_packet_unref(packet);
                if (ffmpeg.av_read_frame(inputContext, packet) < 0)
                {
                    // EOF, or a torn tail packet in a file whose session died mid-write.
                    break;
                }

                if (packet->stream_index != audioStreamIndex)
                {
                    continue;
                }

                ffmpeg.av_packet_rescale_ts(packet, inputTimeBase, outputTimeBase);

                if (packet->duration > 0)
                {
                    packetDuration = packet->duration;
                }

                var pts = nextPts;
                if (packet->pts != ffmpeg.AV_NOPTS_VALUE)
                {
                    if (firstPts == ffmpeg.AV_NOPTS_VALUE)
                    {
                        firstPts = packet->pts;
                    }

                    // Keep any real gap this input has, but never let a timestamp walk backwards —
                    // the muxer requires them strictly increasing.
                    pts = Math.Max(nextPts, packet->pts - firstPts + ptsOffset);
                }

                packet->pts = pts;
                packet->dts = pts;
                packet->duration = packetDuration;
                packet->stream_index = outputStream->index;
                packet->pos = -1;

                nextPts = pts + packetDuration;

                var writeResult = ffmpeg.av_interleaved_write_frame(outputContext, packet);
                if (writeResult < 0)
                {
                    throw new InvalidOperationException(
                        $"av_interleaved_write_frame(join) failed with FFmpeg error code {writeResult} " +
                        $"(pts={packet->pts}, dts={packet->dts}, duration={packet->duration}, " +
                        $"streamIndex={packet->stream_index}, offset={ptsOffset}).");
                }
            }

            return nextPts;
        }
        finally
        {
            if (inputContext is not null)
            {
                ffmpeg.avformat_close_input(&inputContext);
            }
        }
    }
}

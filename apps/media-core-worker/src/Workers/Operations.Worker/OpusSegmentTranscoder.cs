using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Decodes, cuts, resamples and re-encodes a time window spanning one or more already-closed
/// hourly .opus files into mp3 or wav bytes — entirely in-process via FFmpeg.AutoGen, mirroring
/// the demux/decode/resample/encode API calls CaptureSession's live pipeline already uses in
/// InProcessFfmpegAudioCapturePlugin.cs (its packet-read loop, CreateOpusEncoderContext,
/// EncodeBufferedSamples), but generic over the target sample rate/bitrate/format the Alerts
/// audio-edit flow needs rather than fixed to the live pipeline's own 16kHz mono opus settings.
///
/// Only ever reads files the caller has already established are closed (a session's own
/// av_write_trailer + avio_closep, run in CaptureSession's finally block, has already finalized
/// them). Reading the still-open current hour with a second unrelated open of the same file is
/// unsafe — Ogg is a page-based container, and landing on a page mid-write produces either a torn
/// read or a just-flushed tail with no consistent duration — so that case is rejected upstream by
/// ClosedHourAudioSegmentReader before this class is ever called.
/// </summary>
internal static unsafe class OpusSegmentTranscoder
{
    private const int OutputChannels = 1;

    public static byte[] Transcode(
        IReadOnlyList<string> orderedFilePaths,
        double windowStartOffsetSeconds,
        double durationSeconds,
        int targetSampleRateHz,
        int bitrateKbps,
        string format)
    {
        if (orderedFilePaths.Count == 0)
        {
            throw new ArgumentException("At least one source file is required.", nameof(orderedFilePaths));
        }

        if (durationSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), "Duration must be positive.");
        }

        // Idempotent — cheap to call even if a capture session already initialized this process's
        // FFmpeg bindings, and this endpoint can be hit before any session has started.
        InProcessFfmpegAudioCapturePlugin.EnsureFfmpegInitialized();

        var tempOutputPath = Path.Combine(Path.GetTempPath(), $"audio-segment-{Guid.NewGuid():N}.{format}");
        try
        {
            WriteSegment(
                orderedFilePaths, windowStartOffsetSeconds, durationSeconds, targetSampleRateHz, bitrateKbps,
                format, tempOutputPath);
            return File.ReadAllBytes(tempOutputPath);
        }
        finally
        {
            if (File.Exists(tempOutputPath))
            {
                File.Delete(tempOutputPath);
            }
        }
    }

    private static void WriteSegment(
        IReadOnlyList<string> orderedFilePaths,
        double windowStartOffsetSeconds,
        double durationSeconds,
        int targetSampleRateHz,
        int bitrateKbps,
        string format,
        string outputPath)
    {
        AVFormatContext* outputContext = null;
        AVCodecContext* encoderContext = null;
        AVStream* outputStream = null;
        AVPacket* outputPacket = null;
        AVFrame* encoderFrame = null;

        try
        {
            var encoder = FindEncoder(format);
            var sampleFormat = SelectSampleFormat(format);
            var bytesPerSample = ffmpeg.av_get_bytes_per_sample(sampleFormat);
            if (bytesPerSample <= 0)
            {
                throw new InvalidOperationException($"Unexpected sample format for '{format}' encoder.");
            }

            encoderContext = ffmpeg.avcodec_alloc_context3(encoder);
            if (encoderContext is null)
            {
                throw new InvalidOperationException($"Unable to allocate {format} encoder context.");
            }

            encoderContext->sample_rate = targetSampleRateHz;
            ffmpeg.av_channel_layout_default(&encoderContext->ch_layout, OutputChannels);
            encoderContext->sample_fmt = sampleFormat;
            encoderContext->time_base = new AVRational { num = 1, den = targetSampleRateHz };
            encoderContext->bit_rate = bitrateKbps * 1000L;

            ffmpeg.avcodec_open2(encoderContext, encoder, null).ThrowIfError("avcodec_open2(segment_encoder)");

            outputContext = OpenOutputContext(outputPath, format, encoderContext, ref outputStream);

            outputPacket = ffmpeg.av_packet_alloc();
            encoderFrame = ffmpeg.av_frame_alloc();
            if (outputPacket is null || encoderFrame is null)
            {
                throw new InvalidOperationException("Unable to allocate FFmpeg packet/frame for segment output.");
            }

            // frame_size == 0 means the codec accepts any chunk size (true for PCM/wav) — encode
            // each resampled chunk as it arrives rather than batching to a fixed size.
            var frameSize = encoderContext->frame_size > 0 ? encoderContext->frame_size : 0;

            var pcm = new PcmAccumulator();
            var encoderSampleCursor = 0L;
            var remainingSeconds = durationSeconds;
            var skipSeconds = windowStartOffsetSeconds;

            foreach (var filePath in orderedFilePaths)
            {
                if (remainingSeconds <= 0)
                {
                    break;
                }

                DecodeFile(
                    filePath, targetSampleRateHz, sampleFormat, bytesPerSample,
                    ref skipSeconds, ref remainingSeconds,
                    pcm, frameSize, encoderContext, encoderFrame, outputContext, outputStream, outputPacket,
                    ref encoderSampleCursor);
            }

            // Flush whatever's left in the PCM accumulator (shorter than a full encoder frame),
            // then the encoder's own internal delay buffer.
            EncodePending(
                pcm, frameSize, bytesPerSample, encoderContext, encoderFrame, outputContext, outputStream,
                outputPacket, ref encoderSampleCursor, flushFinal: true);

            // A source file that couldn't be opened/decoded at all (missing, corrupt, not really
            // an Ogg stream) makes DecodeFile return early having produced nothing, rather than
            // throwing — see its own comment. Left unchecked, that silently yields a "successful"
            // but empty mp3/wav: a well-formed container with a valid header and zero audio, which
            // is exactly the "segment looks fine but is empty" failure this endpoint exists to
            // prevent. Catch it here instead of only at the raw open_input level.
            if (encoderSampleCursor == 0)
            {
                throw new InvalidOperationException(
                    "No audio could be decoded from the requested window's source file(s).");
            }

            FlushEncoder(encoderContext, outputContext, outputStream, outputPacket);

            ffmpeg.av_write_trailer(outputContext).ThrowIfError("av_write_trailer(segment)");
        }
        finally
        {
            if (encoderFrame is not null)
            {
                ffmpeg.av_frame_free(&encoderFrame);
            }

            if (outputPacket is not null)
            {
                ffmpeg.av_packet_free(&outputPacket);
            }

            if (outputContext is not null)
            {
                if (outputContext->pb is not null)
                {
                    ffmpeg.avio_closep(&outputContext->pb);
                }

                ffmpeg.avformat_free_context(outputContext);
            }

            if (encoderContext is not null)
            {
                var ctxToFree = encoderContext;
                ffmpeg.avcodec_free_context(&ctxToFree);
            }
        }
    }

    // Decodes one closed .opus file, resamples to the target rate/format, discards audio before
    // skipSeconds (decremented as it's consumed — carries over from a previous file if the window
    // start fell exactly on this file's boundary), and encodes up to remainingSeconds worth of
    // the rest. Tolerates a missing/unreadable file or a torn trailing packet by simply stopping —
    // this is best-effort extraction of what exists, not a hard requirement that every byte of the
    // requested window be present.
    private static void DecodeFile(
        string filePath,
        int targetSampleRateHz,
        AVSampleFormat targetSampleFormat,
        int bytesPerSample,
        ref double skipSeconds,
        ref double remainingSeconds,
        PcmAccumulator pcm,
        int frameSize,
        AVCodecContext* encoderContext,
        AVFrame* encoderFrame,
        AVFormatContext* outputContext,
        AVStream* outputStream,
        AVPacket* outputPacket,
        ref long encoderSampleCursor)
    {
        AVFormatContext* inputContext = null;
        AVCodecContext* decoderContext = null;
        SwrContext* swrContext = null;
        AVPacket* inputPacket = null;
        AVFrame* inputFrame = null;
        AVFrame* resampledFrame = null;

        try
        {
            if (ffmpeg.avformat_open_input(&inputContext, filePath, null, null) < 0)
            {
                return;
            }

            if (ffmpeg.avformat_find_stream_info(inputContext, null) < 0)
            {
                return;
            }

            AVCodec* decoder = null;
            var audioStreamIndex = ffmpeg.av_find_best_stream(
                inputContext, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, &decoder, 0);
            if (audioStreamIndex < 0 || decoder is null)
            {
                return;
            }

            decoderContext = ffmpeg.avcodec_alloc_context3(decoder);
            if (decoderContext is null)
            {
                return;
            }

            ffmpeg.avcodec_parameters_to_context(decoderContext, inputContext->streams[audioStreamIndex]->codecpar)
                .ThrowIfError("avcodec_parameters_to_context(segment)");
            ffmpeg.avcodec_open2(decoderContext, decoder, null).ThrowIfError("avcodec_open2(segment_decoder)");

            swrContext = ffmpeg.swr_alloc();
            if (swrContext is null)
            {
                throw new InvalidOperationException("Unable to allocate resampler context.");
            }

            AVChannelLayout inputLayout = decoderContext->ch_layout;
            if (inputLayout.nb_channels <= 0)
            {
                ffmpeg.av_channel_layout_default(&inputLayout, 1);
            }

            AVChannelLayout outputLayout = default;
            ffmpeg.av_channel_layout_default(&outputLayout, OutputChannels);

            ffmpeg.av_opt_set_chlayout(swrContext, "in_chlayout", &inputLayout, 0).ThrowIfError("av_opt_set_chlayout(in,segment)");
            ffmpeg.av_opt_set_chlayout(swrContext, "out_chlayout", &outputLayout, 0).ThrowIfError("av_opt_set_chlayout(out,segment)");
            ffmpeg.av_opt_set_int(swrContext, "in_sample_rate", decoderContext->sample_rate, 0).ThrowIfError("av_opt_set_int(in_rate,segment)");
            ffmpeg.av_opt_set_int(swrContext, "out_sample_rate", targetSampleRateHz, 0).ThrowIfError("av_opt_set_int(out_rate,segment)");
            ffmpeg.av_opt_set_sample_fmt(swrContext, "in_sample_fmt", decoderContext->sample_fmt, 0).ThrowIfError("av_opt_set_sample_fmt(in,segment)");
            ffmpeg.av_opt_set_sample_fmt(swrContext, "out_sample_fmt", targetSampleFormat, 0).ThrowIfError("av_opt_set_sample_fmt(out,segment)");
            ffmpeg.swr_init(swrContext).ThrowIfError("swr_init(segment)");

            inputPacket = ffmpeg.av_packet_alloc();
            inputFrame = ffmpeg.av_frame_alloc();
            resampledFrame = ffmpeg.av_frame_alloc();
            if (inputPacket is null || inputFrame is null || resampledFrame is null)
            {
                throw new InvalidOperationException("Unable to allocate FFmpeg packet/frame for segment input.");
            }

            while (remainingSeconds > 0)
            {
                ffmpeg.av_packet_unref(inputPacket);
                var readResult = ffmpeg.av_read_frame(inputContext, inputPacket);
                if (readResult < 0)
                {
                    // EOF, or a torn tail packet — either way, nothing more to read from this file.
                    break;
                }

                if (inputPacket->stream_index != audioStreamIndex)
                {
                    continue;
                }

                if (ffmpeg.avcodec_send_packet(decoderContext, inputPacket) < 0)
                {
                    continue;
                }

                while (true)
                {
                    var decodeResult = ffmpeg.avcodec_receive_frame(decoderContext, inputFrame);
                    if (decodeResult < 0)
                    {
                        break;
                    }

                    var outSamples = checked((int)ffmpeg.av_rescale_rnd(
                        ffmpeg.swr_get_delay(swrContext, decoderContext->sample_rate) + inputFrame->nb_samples,
                        targetSampleRateHz,
                        decoderContext->sample_rate,
                        AVRounding.AV_ROUND_UP));

                    ffmpeg.av_frame_unref(resampledFrame);
                    resampledFrame->nb_samples = outSamples;
                    resampledFrame->format = (int)targetSampleFormat;
                    resampledFrame->sample_rate = targetSampleRateHz;
                    ffmpeg.av_channel_layout_default(&resampledFrame->ch_layout, OutputChannels);
                    ffmpeg.av_frame_get_buffer(resampledFrame, 0).ThrowIfError("av_frame_get_buffer(segment)");

                    var converted = ffmpeg.swr_convert_frame(swrContext, resampledFrame, inputFrame);
                    ffmpeg.av_frame_unref(inputFrame);
                    if (converted < 0)
                    {
                        continue;
                    }

                    var producedSamples = resampledFrame->nb_samples;
                    var producedSeconds = producedSamples / (double)targetSampleRateHz;
                    int appendedSamples;

                    if (skipSeconds > 0)
                    {
                        if (skipSeconds >= producedSeconds)
                        {
                            skipSeconds -= producedSeconds;
                            continue;
                        }

                        var skipSamples = Math.Clamp(
                            (int)Math.Round(skipSeconds * targetSampleRateHz), 0, producedSamples);
                        appendedSamples = producedSamples - skipSamples;
                        AppendSamplesRange(pcm, resampledFrame, skipSamples, appendedSamples, bytesPerSample);
                        skipSeconds = 0;
                    }
                    else
                    {
                        appendedSamples = producedSamples;
                        AppendSamples(pcm, resampledFrame, appendedSamples, bytesPerSample);
                    }

                    // ponytail: the very last chunk written can overshoot the requested duration
                    // by less than one resampled frame (well under 100ms for typical Opus frame
                    // sizes) rather than being trimmed to the exact sample — negligible for a
                    // preview/edit tool, not worth the extra bookkeeping to fix.
                    remainingSeconds -= appendedSamples / (double)targetSampleRateHz;

                    EncodePending(
                        pcm, frameSize, bytesPerSample, encoderContext, encoderFrame, outputContext, outputStream,
                        outputPacket, ref encoderSampleCursor, flushFinal: false);

                    if (remainingSeconds <= 0)
                    {
                        break;
                    }
                }

                if (remainingSeconds <= 0)
                {
                    break;
                }
            }
        }
        finally
        {
            if (resampledFrame is not null)
            {
                ffmpeg.av_frame_free(&resampledFrame);
            }

            if (inputFrame is not null)
            {
                ffmpeg.av_frame_free(&inputFrame);
            }

            if (inputPacket is not null)
            {
                ffmpeg.av_packet_free(&inputPacket);
            }

            if (swrContext is not null)
            {
                var swrToFree = swrContext;
                ffmpeg.swr_free(&swrToFree);
            }

            if (decoderContext is not null)
            {
                var decoderToFree = decoderContext;
                ffmpeg.avcodec_free_context(&decoderToFree);
            }

            if (inputContext is not null)
            {
                ffmpeg.avformat_close_input(&inputContext);
            }
        }
    }

    // Encodes as many full frame_size-sized chunks as are buffered (or, for a variable-frame-size
    // codec like PCM/wav, whatever is buffered right now); with flushFinal also encodes the
    // trailing partial frame. Mirrors CaptureSession.EncodeBufferedSamples's batching, generalized
    // over bytesPerSample instead of the live pipeline's fixed 16-bit constant.
    private static void EncodePending(
        PcmAccumulator pcm,
        int frameSize,
        int bytesPerSample,
        AVCodecContext* encoderContext,
        AVFrame* encoderFrame,
        AVFormatContext* outputContext,
        AVStream* outputStream,
        AVPacket* outputPacket,
        ref long encoderSampleCursor,
        bool flushFinal)
    {
        var bytesPerFrame = frameSize > 0 ? frameSize * bytesPerSample : 0;

        while (true)
        {
            int samplesToEncode;
            if (frameSize > 0)
            {
                if (pcm.Length >= bytesPerFrame)
                {
                    samplesToEncode = frameSize;
                }
                else if (flushFinal && pcm.Length > 0)
                {
                    samplesToEncode = pcm.Length / bytesPerSample;
                }
                else
                {
                    break;
                }
            }
            else if (pcm.Length > 0)
            {
                samplesToEncode = pcm.Length / bytesPerSample;
            }
            else
            {
                break;
            }

            if (samplesToEncode <= 0)
            {
                break;
            }

            var bytesToEncode = samplesToEncode * bytesPerSample;

            ffmpeg.av_frame_unref(encoderFrame);
            encoderFrame->nb_samples = samplesToEncode;
            encoderFrame->format = (int)encoderContext->sample_fmt;
            encoderFrame->sample_rate = encoderContext->sample_rate;
            ffmpeg.av_channel_layout_default(&encoderFrame->ch_layout, OutputChannels);
            ffmpeg.av_frame_get_buffer(encoderFrame, 0).ThrowIfError("av_frame_get_buffer(segment_encode)");
            encoderFrame->pts = encoderSampleCursor;
            encoderSampleCursor += samplesToEncode;

            pcm.CopyToAndConsume((IntPtr)encoderFrame->data[0], bytesToEncode);

            ffmpeg.avcodec_send_frame(encoderContext, encoderFrame).ThrowIfError("avcodec_send_frame(segment)");

            while (true)
            {
                ffmpeg.av_packet_unref(outputPacket);
                var result = ffmpeg.avcodec_receive_packet(encoderContext, outputPacket);
                if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF)
                {
                    break;
                }

                result.ThrowIfError("avcodec_receive_packet(segment)");
                outputPacket->stream_index = outputStream->index;
                ffmpeg.av_packet_rescale_ts(outputPacket, encoderContext->time_base, outputStream->time_base);
                ffmpeg.av_interleaved_write_frame(outputContext, outputPacket).ThrowIfError("av_interleaved_write_frame(segment)");
            }
        }
    }

    private static void FlushEncoder(
        AVCodecContext* encoderContext, AVFormatContext* outputContext, AVStream* outputStream, AVPacket* outputPacket)
    {
        ffmpeg.avcodec_send_frame(encoderContext, null).ThrowIfError("avcodec_send_frame(segment_flush)");

        while (true)
        {
            ffmpeg.av_packet_unref(outputPacket);
            var result = ffmpeg.avcodec_receive_packet(encoderContext, outputPacket);
            if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF)
            {
                break;
            }

            result.ThrowIfError("avcodec_receive_packet(segment_flush)");
            outputPacket->stream_index = outputStream->index;
            ffmpeg.av_packet_rescale_ts(outputPacket, encoderContext->time_base, outputStream->time_base);
            ffmpeg.av_interleaved_write_frame(outputContext, outputPacket).ThrowIfError("av_interleaved_write_frame(segment_flush)");
        }
    }

    private static AVFormatContext* OpenOutputContext(
        string outputPath, string format, AVCodecContext* encoderContext, ref AVStream* outputStream)
    {
        AVFormatContext* context = null;
        ffmpeg.avformat_alloc_output_context2(&context, null, format, outputPath)
            .ThrowIfError("avformat_alloc_output_context2(segment)");

        outputStream = ffmpeg.avformat_new_stream(context, null);
        if (outputStream is null)
        {
            throw new InvalidOperationException("Unable to create output stream for segment.");
        }

        ffmpeg.avcodec_parameters_from_context(outputStream->codecpar, encoderContext)
            .ThrowIfError("avcodec_parameters_from_context(segment)");
        outputStream->time_base = encoderContext->time_base;
        ffmpeg.avio_open(&context->pb, outputPath, ffmpeg.AVIO_FLAG_WRITE).ThrowIfError("avio_open(segment)");
        ffmpeg.avformat_write_header(context, null).ThrowIfError("avformat_write_header(segment)");
        return context;
    }

    private static AVCodec* FindEncoder(string format)
    {
        var encoder = format switch
        {
            "mp3" => ffmpeg.avcodec_find_encoder_by_name("libmp3lame"),
            "wav" => ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_PCM_S16LE),
            _ => throw new ArgumentException($"Unsupported audio format '{format}'.", nameof(format)),
        };

        if (encoder is null)
        {
            throw new InvalidOperationException($"No encoder available for format '{format}'.");
        }

        return encoder;
    }

    // AVCodec.sample_fmts (querying which formats a codec supports at runtime) is obsolete in
    // this FFmpeg.AutoGen version in favor of avcodec_get_supported_config, which isn't worth
    // wiring up for two fixed, well-known codecs: pcm_s16le only ever supports S16, and
    // libmp3lame's FFmpeg wrapper has declared S16P support in every FFmpeg release this worker
    // targets. avcodec_open2 validates the choice regardless, so a wrong guess fails loudly rather
    // than silently. av_get_bytes_per_sample (not a hardcoded constant) is what makes the rest of
    // this class correct for whichever of the two is actually in use.
    private static AVSampleFormat SelectSampleFormat(string format) =>
        format == "wav" ? AVSampleFormat.AV_SAMPLE_FMT_S16 : AVSampleFormat.AV_SAMPLE_FMT_S16P;

    private static void AppendSamples(PcmAccumulator pcm, AVFrame* frame, int sampleCount, int bytesPerSample) =>
        pcm.Append((IntPtr)frame->data[0], 0, sampleCount * OutputChannels * bytesPerSample);

    private static void AppendSamplesRange(
        PcmAccumulator pcm, AVFrame* frame, int skipSamples, int sampleCount, int bytesPerSample) =>
        pcm.Append(
            (IntPtr)frame->data[0], skipSamples * OutputChannels * bytesPerSample,
            sampleCount * OutputChannels * bytesPerSample);

    // Minimal growable PCM byte buffer for this one-shot extraction — CaptureSession.PcmByteQueue
    // (its live-pipeline equivalent) adds ring-buffer shrink behavior tuned for a long-running
    // session, which a bounded, single-request extraction doesn't need.
    private sealed class PcmAccumulator
    {
        private byte[] buffer = new byte[64 * 1024];
        private int length;

        public int Length => length;

        public void Append(IntPtr source, int byteOffset, int byteCount)
        {
            if (byteCount <= 0)
            {
                return;
            }

            EnsureCapacity(length + byteCount);
            Marshal.Copy(source + byteOffset, buffer, length, byteCount);
            length += byteCount;
        }

        public void CopyToAndConsume(IntPtr destination, int byteCount)
        {
            Marshal.Copy(buffer, 0, destination, byteCount);
            Array.Copy(buffer, byteCount, buffer, 0, length - byteCount);
            length -= byteCount;
        }

        private void EnsureCapacity(int required)
        {
            if (required <= buffer.Length)
            {
                return;
            }

            var newSize = buffer.Length;
            while (newSize < required)
            {
                newSize *= 2;
            }

            Array.Resize(ref buffer, newSize);
        }
    }
}

using FFmpeg.AutoGen;

namespace MediaOpsCore.Workers.Operations;

/// <summary>How the audio of a TV recording is stored: mono, at this sample rate and AAC bitrate.</summary>
internal sealed record TvAudioSettings(int Kbps, int SampleRateHz);

/// <summary>
/// Re-encodes the audio of a live stream into a small mono AAC-LC track while InProcessMp4Recorder copies
/// the video untouched. YouTube serves its audio as 128 kbps stereo, a third of every file, and the speech
/// recognition that reads these recordings works as well from mono at 16 kHz (checked on real channels:
/// the transcription of 24 to 48 kbps matched the original audio 99 %). Entirely in-process with the FFmpeg
/// libraries the worker already ships: decode, resample to mono, buffer into the encoder's frame size,
/// encode, and hand each packet to the output muxer.
///
/// AAC stays the codec, rather than the smaller Opus, because the clip of an alert note is sent to WhatsApp
/// and played in a browser: both expect H.264 + AAC in an MP4.
/// </summary>
internal sealed unsafe class TvAudioReencoder : IDisposable
{
    // A jump in the stream's own timestamps larger than this is a hole (or a repeat) in the source, not jitter.
    private const double DiscontinuitySeconds = 0.25;

    private readonly AVFormatContext* output;
    private readonly AVStream* inputStream;
    private readonly AVStream* outputStream;
    private readonly long inputOffsetInStreamTimeBase;
    private readonly int sampleRate;
    private readonly int outputChannels = 1;

    private AVCodecContext* decoder;
    private AVCodecContext* encoder;
    private SwrContext* resampler;
    private AVAudioFifo* fifo;
    private AVFrame* decodedFrame;
    private AVFrame* resampledFrame;
    private AVFrame* encoderFrame;
    private AVPacket* encodedPacket;

    // Presentation time, in samples, of the first sample still waiting in the buffer; -1 until the first frame.
    private long nextPts = -1;
    private bool disposed;

    public int InputStreamIndex { get; }

    public int OutputStreamIndex { get; }

    private TvAudioReencoder(AVFormatContext* output, AVStream* inputStream, long inputOffsetMicroseconds, TvAudioSettings settings)
    {
        this.output = output;
        this.inputStream = inputStream;
        InputStreamIndex = inputStream->index;
        sampleRate = settings.SampleRateHz;
        inputOffsetInStreamTimeBase = ffmpeg.av_rescale_q(inputOffsetMicroseconds, ffmpeg.av_get_time_base_q(), inputStream->time_base);

        try
        {
            OpenDecoder();
            OpenEncoder(settings);

            // The output stream is created last so it follows the video stream, as when the audio was copied.
            outputStream = ffmpeg.avformat_new_stream(output, null);
            if (outputStream is null)
            {
                throw new InvalidOperationException("avformat_new_stream failed for the audio track.");
            }

            ffmpeg.avcodec_parameters_from_context(outputStream->codecpar, encoder).ThrowIfError("avcodec_parameters_from_context(tv audio)");
            outputStream->time_base = encoder->time_base;
            OutputStreamIndex = (int)outputStream->index;

            OpenResampler();
            fifo = ffmpeg.av_audio_fifo_alloc(AVSampleFormat.AV_SAMPLE_FMT_FLTP, outputChannels, Math.Max(1, encoder->frame_size) * 4);
            decodedFrame = ffmpeg.av_frame_alloc();
            resampledFrame = ffmpeg.av_frame_alloc();
            encoderFrame = ffmpeg.av_frame_alloc();
            encodedPacket = ffmpeg.av_packet_alloc();
            if (fifo is null || decodedFrame is null || resampledFrame is null || encoderFrame is null || encodedPacket is null)
            {
                throw new InvalidOperationException("Unable to allocate the audio re-encoding buffers.");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    // The audio stream to re-encode: the preferred input's best audio stream, else any other input's. Null when
    // the streams carry no audio or the decoder cannot be opened — the caller then copies the audio as before.
    public static TvAudioReencoder? TryCreate(
        AVFormatContext*[] inputs, long[] offsetsMicroseconds, AVFormatContext* output, TvAudioSettings settings, int preferInput, out int inputIndex)
    {
        inputIndex = -1;
        var order = new[] { preferInput }.Concat(Enumerable.Range(0, inputs.Length).Where(i => i != preferInput));
        foreach (var i in order)
        {
            var index = ffmpeg.av_find_best_stream(inputs[i], AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            if (index < 0)
            {
                continue;
            }

            try
            {
                var reencoder = new TvAudioReencoder(output, inputs[i]->streams[index], offsetsMicroseconds[i], settings);
                inputIndex = i;
                return reencoder;
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
            {
                return null;
            }
        }

        return null;
    }

    private void OpenDecoder()
    {
        var codec = ffmpeg.avcodec_find_decoder(inputStream->codecpar->codec_id);
        if (codec is null)
        {
            throw new InvalidOperationException("No decoder for the stream's audio codec.");
        }

        decoder = ffmpeg.avcodec_alloc_context3(codec);
        if (decoder is null)
        {
            throw new InvalidOperationException("Unable to allocate the audio decoder.");
        }

        ffmpeg.avcodec_parameters_to_context(decoder, inputStream->codecpar).ThrowIfError("avcodec_parameters_to_context(tv audio)");
        decoder->pkt_timebase = inputStream->time_base;
        ffmpeg.avcodec_open2(decoder, codec, null).ThrowIfError("avcodec_open2(tv audio decoder)");
    }

    private void OpenEncoder(TvAudioSettings settings)
    {
        var codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);
        if (codec is null)
        {
            throw new InvalidOperationException("This FFmpeg build has no AAC encoder.");
        }

        encoder = ffmpeg.avcodec_alloc_context3(codec);
        if (encoder is null)
        {
            throw new InvalidOperationException("Unable to allocate the audio encoder.");
        }

        encoder->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        encoder->sample_rate = settings.SampleRateHz;
        ffmpeg.av_channel_layout_default(&encoder->ch_layout, outputChannels);
        encoder->bit_rate = settings.Kbps * 1000L;
        encoder->time_base = new AVRational { num = 1, den = settings.SampleRateHz };

        // MP4 keeps the codec configuration in its header, not in every packet.
        if ((output->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
        {
            encoder->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
        }

        ffmpeg.avcodec_open2(encoder, codec, null).ThrowIfError("avcodec_open2(tv audio encoder)");
    }

    private void OpenResampler()
    {
        resampler = ffmpeg.swr_alloc();
        if (resampler is null)
        {
            throw new InvalidOperationException("Unable to allocate the audio resampler.");
        }

        AVChannelLayout inputLayout = decoder->ch_layout;
        if (inputLayout.nb_channels <= 0)
        {
            ffmpeg.av_channel_layout_default(&inputLayout, 1);
        }

        AVChannelLayout outputLayout = default;
        ffmpeg.av_channel_layout_default(&outputLayout, outputChannels);

        ffmpeg.av_opt_set_chlayout(resampler, "in_chlayout", &inputLayout, 0).ThrowIfError("av_opt_set_chlayout(in,tv audio)");
        ffmpeg.av_opt_set_chlayout(resampler, "out_chlayout", &outputLayout, 0).ThrowIfError("av_opt_set_chlayout(out,tv audio)");
        ffmpeg.av_opt_set_int(resampler, "in_sample_rate", decoder->sample_rate, 0).ThrowIfError("av_opt_set_int(in_rate,tv audio)");
        ffmpeg.av_opt_set_int(resampler, "out_sample_rate", sampleRate, 0).ThrowIfError("av_opt_set_int(out_rate,tv audio)");
        ffmpeg.av_opt_set_sample_fmt(resampler, "in_sample_fmt", decoder->sample_fmt, 0).ThrowIfError("av_opt_set_sample_fmt(in,tv audio)");
        ffmpeg.av_opt_set_sample_fmt(resampler, "out_sample_fmt", AVSampleFormat.AV_SAMPLE_FMT_FLTP, 0).ThrowIfError("av_opt_set_sample_fmt(out,tv audio)");
        ffmpeg.swr_init(resampler).ThrowIfError("swr_init(tv audio)");
    }

    /// <summary>One packet of the audio stream as read from the input (not yet rebased).</summary>
    public void Write(AVPacket* packet)
    {
        if (ffmpeg.avcodec_send_packet(decoder, packet) < 0)
        {
            return; // a damaged packet is skipped; the next one decodes on
        }

        DrainDecoder();
    }

    /// <summary>Flushes everything still buffered; call once, before the trailer is written.</summary>
    public void Finish()
    {
        _ = ffmpeg.avcodec_send_packet(decoder, null);
        DrainDecoder();
        EncodeBuffered(flushAll: true);
        SendToEncoder(null);
    }

    private void DrainDecoder()
    {
        while (ffmpeg.avcodec_receive_frame(decoder, decodedFrame) >= 0)
        {
            Process(decodedFrame);
            ffmpeg.av_frame_unref(decodedFrame);
        }
    }

    private void Process(AVFrame* frame)
    {
        // Where on the recording's timeline this frame starts, in samples at the output rate.
        long? framePts = null;
        var stamp = frame->best_effort_timestamp;
        if (stamp != ffmpeg.AV_NOPTS_VALUE)
        {
            var seconds = (stamp + inputOffsetInStreamTimeBase) * ffmpeg.av_q2d(inputStream->time_base);
            framePts = Math.Max(0, (long)Math.Round(seconds * sampleRate));
        }

        var produced = Resample(frame);
        if (produced <= 0)
        {
            return;
        }

        if (nextPts < 0)
        {
            nextPts = framePts ?? 0;
        }
        else if (framePts is { } actual)
        {
            // The buffered samples already account for the time up to here; compare with what the stream says.
            var expected = nextPts + ffmpeg.av_audio_fifo_size(fifo);
            var tolerance = (long)(DiscontinuitySeconds * sampleRate);
            if (actual > expected + tolerance)
            {
                // A hole in the source: finish what is buffered, then carry on from the new position.
                EncodeBuffered(flushAll: true);
                nextPts = actual;
            }
            else if (actual < expected - tolerance)
            {
                return; // the stream repeated itself; the audio it already gave is kept
            }
        }

        _ = ffmpeg.av_audio_fifo_write(fifo, (void**)resampledFrame->extended_data, resampledFrame->nb_samples);
        EncodeBuffered(flushAll: false);
    }

    // Converts one decoded frame to mono at the output rate into resampledFrame; returns the samples produced.
    private int Resample(AVFrame* frame)
    {
        var maxSamples = checked((int)ffmpeg.av_rescale_rnd(
            ffmpeg.swr_get_delay(resampler, frame->sample_rate) + frame->nb_samples, sampleRate, frame->sample_rate, AVRounding.AV_ROUND_UP));

        ffmpeg.av_frame_unref(resampledFrame);
        resampledFrame->nb_samples = maxSamples;
        resampledFrame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        resampledFrame->sample_rate = sampleRate;
        ffmpeg.av_channel_layout_default(&resampledFrame->ch_layout, outputChannels);
        ffmpeg.av_frame_get_buffer(resampledFrame, 0).ThrowIfError("av_frame_get_buffer(tv audio resample)");

        return ffmpeg.swr_convert_frame(resampler, resampledFrame, frame) < 0 ? 0 : resampledFrame->nb_samples;
    }

    // Encodes every full frame the buffer holds; with flushAll also the short one left at the end.
    private void EncodeBuffered(bool flushAll)
    {
        var frameSize = encoder->frame_size > 0 ? encoder->frame_size : 1024;
        while (true)
        {
            var available = ffmpeg.av_audio_fifo_size(fifo);
            if (available < frameSize && !(flushAll && available > 0))
            {
                return;
            }

            var count = Math.Min(frameSize, available);
            ffmpeg.av_frame_unref(encoderFrame);
            encoderFrame->nb_samples = count;
            encoderFrame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
            encoderFrame->sample_rate = sampleRate;
            ffmpeg.av_channel_layout_default(&encoderFrame->ch_layout, outputChannels);
            ffmpeg.av_frame_get_buffer(encoderFrame, 0).ThrowIfError("av_frame_get_buffer(tv audio encode)");

            if (ffmpeg.av_audio_fifo_read(fifo, (void**)encoderFrame->extended_data, count) < count)
            {
                return;
            }

            encoderFrame->pts = nextPts;
            nextPts += count;
            SendToEncoder(encoderFrame);
        }
    }

    private void SendToEncoder(AVFrame* frame)
    {
        if (ffmpeg.avcodec_send_frame(encoder, frame) < 0)
        {
            return;
        }

        while (ffmpeg.avcodec_receive_packet(encoder, encodedPacket) >= 0)
        {
            encodedPacket->stream_index = OutputStreamIndex;
            ffmpeg.av_packet_rescale_ts(encodedPacket, encoder->time_base, outputStream->time_base);

            // Takes ownership of the packet's data; a rejected packet is dropped rather than ending the hour.
            _ = ffmpeg.av_interleaved_write_frame(output, encodedPacket);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        if (encodedPacket is not null)
        {
            var packet = encodedPacket;
            ffmpeg.av_packet_free(&packet);
            encodedPacket = null;
        }

        FreeFrame(ref encoderFrame);
        FreeFrame(ref resampledFrame);
        FreeFrame(ref decodedFrame);

        if (fifo is not null)
        {
            ffmpeg.av_audio_fifo_free(fifo);
            fifo = null;
        }

        if (resampler is not null)
        {
            var swr = resampler;
            ffmpeg.swr_free(&swr);
            resampler = null;
        }

        if (encoder is not null)
        {
            var context = encoder;
            ffmpeg.avcodec_free_context(&context);
            encoder = null;
        }

        if (decoder is not null)
        {
            var context = decoder;
            ffmpeg.avcodec_free_context(&context);
            decoder = null;
        }
    }

    private static void FreeFrame(ref AVFrame* frame)
    {
        if (frame is null)
        {
            return;
        }

        var toFree = frame;
        ffmpeg.av_frame_free(&toFree);
        frame = null;
    }
}

using FFmpeg.AutoGen;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Decodes the video of a clip from its keyframe and encodes (libx264) only what falls inside the requested window, so
/// a clip can start and end on the exact picture asked for. Copying packets cannot do that: it has to begin at the
/// keyframe before the start (up to two seconds earlier on YouTube's streams). Used by Mp4SegmentCutter in exact mode
/// for the clip a note keeps; the wide window the editor plays is still a plain copy.
/// </summary>
internal sealed unsafe class VideoClipEncoder : IDisposable
{
    private readonly AVFormatContext* output;
    private readonly AVStream* inputStream;
    private readonly AVStream* outputStream;
    private readonly double clipStartSeconds;
    private readonly double clipEndSeconds;
    private readonly double timeBase;

    private AVCodecContext* decoder;
    private AVCodecContext* encoder;
    private AVFrame* frame;
    private AVPacket* encoded;
    private bool disposed;

    private VideoClipEncoder(AVFormatContext* output, AVFormatContext* input, AVStream* inputStream, double clipStartSeconds, double clipEndSeconds)
    {
        this.output = output;
        this.inputStream = inputStream;
        this.clipStartSeconds = clipStartSeconds;
        this.clipEndSeconds = clipEndSeconds;
        timeBase = ffmpeg.av_q2d(inputStream->time_base);

        try
        {
            OpenDecoder();
            OpenEncoder(input);

            outputStream = ffmpeg.avformat_new_stream(output, null);
            if (outputStream is null)
            {
                throw new InvalidOperationException("avformat_new_stream failed for the clip's video.");
            }

            ffmpeg.avcodec_parameters_from_context(outputStream->codecpar, encoder).ThrowIfError("avcodec_parameters_from_context(clip video)");
            outputStream->time_base = encoder->time_base;
            outputStream->avg_frame_rate = encoder->framerate;
            OutputStreamIndex = outputStream->index;

            frame = ffmpeg.av_frame_alloc();
            encoded = ffmpeg.av_packet_alloc();
            if (frame is null || encoded is null)
            {
                throw new InvalidOperationException("Unable to allocate the clip's video buffers.");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public int OutputStreamIndex { get; }

    // Where the last encoded picture ends, in seconds from the start of the clip.
    public double EndSeconds { get; private set; }

    /// <summary>
    /// A clip's video encoder. clipStartSeconds and clipEndSeconds are on the input stream's own clock (what its packets
    /// are stamped with). Null when this build has no H.264 encoder, so the caller can copy instead.
    /// </summary>
    public static VideoClipEncoder? TryCreate(
        AVFormatContext* output, AVFormatContext* input, AVStream* inputStream, double clipStartSeconds, double clipEndSeconds)
    {
        if (ffmpeg.avcodec_find_encoder_by_name("libx264") is null)
        {
            return null;
        }

        return new VideoClipEncoder(output, input, inputStream, clipStartSeconds, clipEndSeconds);
    }

    private void OpenDecoder()
    {
        var codec = ffmpeg.avcodec_find_decoder(inputStream->codecpar->codec_id);
        if (codec is null)
        {
            throw new InvalidOperationException("No decoder for the recording's video codec.");
        }

        decoder = ffmpeg.avcodec_alloc_context3(codec);
        if (decoder is null)
        {
            throw new InvalidOperationException("Unable to allocate the video decoder.");
        }

        ffmpeg.avcodec_parameters_to_context(decoder, inputStream->codecpar).ThrowIfError("avcodec_parameters_to_context(clip video)");
        decoder->pkt_timebase = inputStream->time_base;
        ffmpeg.avcodec_open2(decoder, codec, null).ThrowIfError("avcodec_open2(clip video decoder)");
    }

    private void OpenEncoder(AVFormatContext* input)
    {
        var codec = ffmpeg.avcodec_find_encoder_by_name("libx264");
        encoder = ffmpeg.avcodec_alloc_context3(codec);
        if (encoder is null)
        {
            throw new InvalidOperationException("Unable to allocate the video encoder.");
        }

        var rate = ffmpeg.av_guess_frame_rate(input, inputStream, null);
        if (rate.num <= 0 || rate.den <= 0)
        {
            rate = new AVRational { num = 30, den = 1 };
        }

        encoder->width = decoder->width;
        encoder->height = decoder->height;
        encoder->pix_fmt = decoder->pix_fmt == AVPixelFormat.AV_PIX_FMT_NONE ? AVPixelFormat.AV_PIX_FMT_YUV420P : decoder->pix_fmt;
        encoder->sample_aspect_ratio = decoder->sample_aspect_ratio;
        // The input's own clock, so a picture keeps its exact stamp whatever the frame rate does.
        encoder->time_base = inputStream->time_base;
        encoder->framerate = rate;
        encoder->gop_size = Math.Max(1, (int)Math.Round(ffmpeg.av_q2d(rate) * 2));
        // No reordering: every picture leaves the encoder in the order it came in, with its own stamp.
        encoder->max_b_frames = 0;

        if ((output->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
        {
            encoder->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
        }

        // Quality close to the source's: the note's clip is the company's own copy of what was broadcast.
        ffmpeg.av_opt_set(encoder->priv_data, "preset", "veryfast", 0);
        ffmpeg.av_opt_set(encoder->priv_data, "crf", "20", 0);
        ffmpeg.avcodec_open2(encoder, codec, null).ThrowIfError("avcodec_open2(clip video encoder)");
    }

    /// <summary>One packet of the video as read from the recording, in file order. False once the end of the window is passed.</summary>
    public bool Write(AVPacket* packet)
    {
        if (ffmpeg.avcodec_send_packet(decoder, packet) < 0)
        {
            return true; // a damaged packet is skipped; the next one decodes on
        }

        return DrainDecoder();
    }

    /// <summary>Flushes the decoder and the encoder; call once, before the trailer.</summary>
    public void Finish()
    {
        _ = ffmpeg.avcodec_send_packet(decoder, null);
        _ = DrainDecoder();
        SendToEncoder(null);
    }

    // False when a picture past the end of the window came out (nothing after it is wanted).
    private bool DrainDecoder()
    {
        var more = true;
        while (ffmpeg.avcodec_receive_frame(decoder, frame) >= 0)
        {
            var stamp = frame->best_effort_timestamp;
            if (stamp != ffmpeg.AV_NOPTS_VALUE)
            {
                var seconds = stamp * timeBase;
                // Half a frame of slack on the start: the picture that lands on the asked instant is kept.
                if (seconds >= clipStartSeconds - 0.5 * FrameSeconds && seconds < clipEndSeconds)
                {
                    EncodeFrame(stamp);
                }
                else if (seconds >= clipEndSeconds)
                {
                    more = false;
                }
            }

            ffmpeg.av_frame_unref(frame);
        }

        return more;
    }

    private double FrameSeconds => encoder->framerate.num > 0 ? encoder->framerate.den / (double)encoder->framerate.num : 1.0 / 30;

    private void EncodeFrame(long inputStamp)
    {
        var startStamp = (long)Math.Round(clipStartSeconds / timeBase);
        frame->pts = Math.Max(0, inputStamp - startStamp);
        frame->pict_type = AVPictureType.AV_PICTURE_TYPE_NONE;
        SendToEncoder(frame);
        EndSeconds = Math.Max(EndSeconds, frame->pts * timeBase + FrameSeconds);
    }

    private void SendToEncoder(AVFrame* picture)
    {
        if (ffmpeg.avcodec_send_frame(encoder, picture) < 0)
        {
            return;
        }

        while (ffmpeg.avcodec_receive_packet(encoder, encoded) >= 0)
        {
            encoded->stream_index = OutputStreamIndex;
            ffmpeg.av_packet_rescale_ts(encoded, encoder->time_base, outputStream->time_base);

            // Takes ownership of the packet's data; a rejected packet is dropped rather than ending the clip.
            _ = ffmpeg.av_interleaved_write_frame(output, encoded);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        if (encoded is not null)
        {
            var packet = encoded;
            ffmpeg.av_packet_free(&packet);
            encoded = null;
        }

        if (frame is not null)
        {
            var picture = frame;
            ffmpeg.av_frame_free(&picture);
            frame = null;
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
}

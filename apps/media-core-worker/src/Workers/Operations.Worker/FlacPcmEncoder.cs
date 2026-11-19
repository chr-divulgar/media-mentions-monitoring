using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using MediaOpsCore.Modules.Capture.Application;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Encodes PCM (s16le, mono, 16 kHz: see <see cref="SpeechAudio"/>) into a FLAC stream with the FFmpeg libraries the
/// worker already ships, for the speech engines that take FLAC. Moved out of the capture session so every
/// <see cref="ISpeechToText"/> adapter can use it.
/// </summary>
internal static unsafe class FlacPcmEncoder
{
    private const int AudioSampleRate = SpeechAudio.SampleRate;
    private const int AudioChannels = SpeechAudio.Channels;
    private const int AudioBytesPerSample = SpeechAudio.BytesPerSample;

    public static byte[] Encode(byte[] pcmSnapshot)
    {
        if (pcmSnapshot.Length == 0)
        {
            return [];
        }

        // Idempotent: the native libraries are loaded once, whoever asks first (the capture plugin or an engine adapter).
        InProcessFfmpegAudioCapturePlugin.EnsureFfmpegInitialized();

        AVFormatContext* outputContext = null;
        AVCodecContext* encoderContext = null;
        AVStream* outputStream = null;
        AVFrame* frame = null;
        AVPacket* packet = null;
        byte* dynamicBuffer = null;
        byte[]? encodedBytes = null;

        try
        {
            AVCodec* encoder = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_FLAC);
            if (encoder is null)
            {
                throw new InvalidOperationException("FLAC encoder not found.");
            }

            ffmpeg.avformat_alloc_output_context2(&outputContext, null, "flac", null).ThrowIfError("avformat_alloc_output_context2(flac)");

            encoderContext = ffmpeg.avcodec_alloc_context3(encoder);
            if (encoderContext is null)
            {
                throw new InvalidOperationException("Unable to allocate FLAC encoder context.");
            }

            encoderContext->sample_rate = AudioSampleRate;
            encoderContext->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_S16;
            encoderContext->time_base = new AVRational { num = 1, den = AudioSampleRate };

            try
            {
                ffmpeg.av_channel_layout_default(&encoderContext->ch_layout, AudioChannels);
            }
            catch (NotSupportedException exception)
            {
                throw new InvalidOperationException("av_channel_layout_default(flac_encoder) is not supported by the loaded FFmpeg bindings.", exception);
            }

            if ((outputContext->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
            {
                encoderContext->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
            }

            ffmpeg.avcodec_open2(encoderContext, encoder, null).ThrowIfError("avcodec_open2(flac_encoder)");

            outputStream = ffmpeg.avformat_new_stream(outputContext, null);
            if (outputStream is null)
            {
                throw new InvalidOperationException("Unable to create FLAC output stream.");
            }

            ffmpeg.avcodec_parameters_from_context(outputStream->codecpar, encoderContext).ThrowIfError("avcodec_parameters_from_context(flac)");
            outputStream->time_base = encoderContext->time_base;

            ffmpeg.avio_open_dyn_buf(&outputContext->pb).ThrowIfError("avio_open_dyn_buf(flac)");
            outputContext->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;
            ffmpeg.avformat_write_header(outputContext, null).ThrowIfError("avformat_write_header(flac)");

            frame = ffmpeg.av_frame_alloc();
            packet = ffmpeg.av_packet_alloc();
            if (frame is null || packet is null)
            {
                throw new InvalidOperationException("Unable to allocate FLAC frame or packet.");
            }

            var frameSamples = encoderContext->frame_size > 0 ? encoderContext->frame_size : 4096;
            var bytesPerSample = AudioChannels * AudioBytesPerSample;
            var offset = 0;
            var pts = 0L;

            while (offset < pcmSnapshot.Length)
            {
                var availableSamples = (pcmSnapshot.Length - offset) / bytesPerSample;
                if (availableSamples <= 0)
                {
                    break;
                }

                var samplesToEncode = Math.Min(frameSamples, availableSamples);
                var bytesToEncode = samplesToEncode * bytesPerSample;

                ffmpeg.av_frame_unref(frame);
                frame->nb_samples = samplesToEncode;
                frame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_S16;
                frame->sample_rate = AudioSampleRate;
                frame->pts = pts;
                pts += samplesToEncode;

                try
                {
                    ffmpeg.av_channel_layout_default(&frame->ch_layout, AudioChannels);
                }
                catch (NotSupportedException exception)
                {
                    throw new InvalidOperationException("av_channel_layout_default(flac_frame) is not supported by the loaded FFmpeg bindings.", exception);
                }

                ffmpeg.av_frame_get_buffer(frame, 0).ThrowIfError("av_frame_get_buffer(flac)");
                Marshal.Copy(pcmSnapshot, offset, (IntPtr)frame->data[0], bytesToEncode);
                offset += bytesToEncode;

                ffmpeg.avcodec_send_frame(encoderContext, frame).ThrowIfError("avcodec_send_frame(flac)");
                WriteAvailableFlacPackets(outputContext, outputStream, encoderContext, packet);
            }

            ffmpeg.avcodec_send_frame(encoderContext, null).ThrowIfError("avcodec_send_frame(flac_flush)");
            WriteAvailableFlacPackets(outputContext, outputStream, encoderContext, packet);

            ffmpeg.av_write_trailer(outputContext).ThrowIfError("av_write_trailer(flac)");

            var dynamicSize = ffmpeg.avio_close_dyn_buf(outputContext->pb, &dynamicBuffer);
            outputContext->pb = null;
            if (dynamicSize < 0)
            {
                throw new InvalidOperationException($"avio_close_dyn_buf(flac) failed with FFmpeg error code {dynamicSize}.");
            }

            encodedBytes = new byte[dynamicSize];
            if (dynamicSize > 0)
            {
                Marshal.Copy((IntPtr)dynamicBuffer, encodedBytes, 0, dynamicSize);
            }

        }
        finally
        {
            if (dynamicBuffer is not null)
            {
                ffmpeg.av_free(dynamicBuffer);
            }

            if (packet is not null)
            {
                ffmpeg.av_packet_free(&packet);
            }

            if (frame is not null)
            {
                ffmpeg.av_frame_free(&frame);
            }

            if (outputContext is not null)
            {
                if (outputContext->pb is not null)
                {
                    ffmpeg.avio_context_free(&outputContext->pb);
                }

                ffmpeg.avformat_free_context(outputContext);
            }

            if (encoderContext is not null)
            {
                AVCodecContext* encoderToFree = encoderContext;
                ffmpeg.avcodec_free_context(&encoderToFree);
            }
        }

        return encodedBytes ?? [];
    }

    private static void WriteAvailableFlacPackets(AVFormatContext* outputContext, AVStream* outputStream, AVCodecContext* encoderContext, AVPacket* packet)
    {
        while (true)
        {
            ffmpeg.av_packet_unref(packet);
            var result = ffmpeg.avcodec_receive_packet(encoderContext, packet);
            if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF)
            {
                break;
            }

            result.ThrowIfError("avcodec_receive_packet(flac)");
            packet->stream_index = outputStream->index;
            ffmpeg.av_packet_rescale_ts(packet, encoderContext->time_base, outputStream->time_base);
            ffmpeg.av_interleaved_write_frame(outputContext, packet).ThrowIfError("av_interleaved_write_frame(flac)");
        }
    }
}

using System.Buffers.Binary;
using System.Text;
using MediaOpsCore.Modules.Capture.Application;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Reads the WAV files the portal cuts for transcription as PCM. Only the format the speech port takes is accepted
/// (<see cref="SpeechAudio"/>: 16 kHz, mono, 16-bit PCM): anything else is refused by name, not sent to an engine
/// as noise. The path comes from a request, so the file must also really be a WAV.
/// </summary>
public sealed class WavPcmFileReader : IPcmFileReader
{
    public async Task<byte[]> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return ExtractPcm(bytes);
    }

    private static byte[] ExtractPcm(byte[] wav)
    {
        if (wav.Length < 12 || Tag(wav, 0) != "RIFF" || Tag(wav, 8) != "WAVE")
        {
            throw new InvalidDataException("The file is not a WAV.");
        }

        var formatChecked = false;
        var position = 12;
        while (position + 8 <= wav.Length)
        {
            var tag = Tag(wav, position);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(position + 4));
            var body = position + 8;

            if (tag == "fmt " && body + 16 <= wav.Length)
            {
                var audioFormat = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body));
                var channels = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body + 2));
                var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(body + 4));
                var bits = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body + 14));
                if (audioFormat != 1 || channels != SpeechAudio.Channels || sampleRate != SpeechAudio.SampleRate || bits != SpeechAudio.BytesPerSample * 8)
                {
                    throw new InvalidDataException(
                        $"The WAV must be 16 kHz, mono, 16-bit PCM (it is format {audioFormat}, {sampleRate} Hz, {channels} channel(s), {bits} bits).");
                }

                formatChecked = true;
            }
            else if (tag == "data")
            {
                if (!formatChecked)
                {
                    throw new InvalidDataException("The WAV has audio before its format.");
                }

                // A data size larger than the file (a stream that was never closed) means: what is there.
                var length = (int)Math.Min(size, (uint)(wav.Length - body));
                length -= length % SpeechAudio.BytesPerSample;
                return wav.AsSpan(body, length).ToArray();
            }

            // Chunks are padded to an even size.
            var next = (long)body + size + (size % 2);
            if (next > wav.Length)
            {
                break;
            }

            position = (int)next;
        }

        throw new InvalidDataException("The WAV has no audio data.");
    }

    private static string Tag(byte[] bytes, int offset) => Encoding.ASCII.GetString(bytes, offset, 4);
}

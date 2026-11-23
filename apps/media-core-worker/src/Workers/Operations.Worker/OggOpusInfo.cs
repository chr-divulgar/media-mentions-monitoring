using System.Buffers.Binary;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// What an Ogg Opus file says about itself, read from its page headers without decoding anything: the channels and
/// input rate from the OpusHead, and how long it lasts from the granule position of the last page of each stream
/// (counted in 48 kHz samples after the pre-skip). A file that was chained after a restart holds several streams, which
/// are added up. Walking the headers of an hour file (~14k pages) takes milliseconds, which is what makes it
/// possible to tell the bitrate of 25,000 files before converting any.
/// </summary>
public sealed record OggOpusInfo(int Channels, int InputSampleRate, TimeSpan Duration)
{
    private const double OpusClockHz = 48000.0;

    /// <summary>Null when the file is missing, cannot be read, or is not Ogg Opus. A truncated file gives what is complete.</summary>
    public static OggOpusInfo? Read(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, FileOptions.SequentialScan);
            return Read(stream);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static OggOpusInfo? Read(Stream stream)
    {
        var header = new byte[27];
        var table = new byte[255];
        int channels = 0, inputRate = 0, preSkip = 0;
        var lastGranule = -1L;
        var seconds = 0.0;
        var inStream = false;
        var isOpus = false;

        while (ReadExactly(stream, header, 27))
        {
            if (header[0] != 'O' || header[1] != 'g' || header[2] != 'g' || header[3] != 'S')
            {
                break; // not Ogg, or sync lost in a damaged file: keep what was read
            }

            var beginsStream = (header[5] & 0x02) != 0;
            var granule = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(6));
            var segments = header[26];
            if (!ReadExactly(stream, table, segments))
            {
                break;
            }

            var body = 0;
            for (var i = 0; i < segments; i++)
            {
                body += table[i];
            }

            if (beginsStream)
            {
                if (inStream && lastGranule >= 0)
                {
                    seconds += Math.Max(0, lastGranule - preSkip) / OpusClockHz;
                }

                lastGranule = -1;
                inStream = true;

                var payload = new byte[body];
                if (!ReadExactly(stream, payload, body))
                {
                    break;
                }

                if (body < 19 || !payload.AsSpan(0, 8).SequenceEqual("OpusHead"u8))
                {
                    return null; // an Ogg stream, but not Opus
                }

                channels = payload[9];
                preSkip = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(10));
                inputRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(12));
                isOpus = true;
                continue;
            }

            if (granule >= 0)
            {
                lastGranule = granule; // -1 means no packet ends on this page
            }

            if (stream.Seek(body, SeekOrigin.Current) > stream.Length)
            {
                break;
            }
        }

        if (!isOpus)
        {
            return null;
        }

        if (inStream && lastGranule >= 0)
        {
            seconds += Math.Max(0, lastGranule - preSkip) / OpusClockHz;
        }

        return new OggOpusInfo(channels, inputRate, TimeSpan.FromSeconds(seconds));
    }

    /// <summary>The bitrate of a file of this size that lasts as long as this one.</summary>
    public double AverageKbps(long sizeBytes) =>
        Duration <= TimeSpan.Zero ? 0 : sizeBytes * 8.0 / 1000.0 / Duration.TotalSeconds;

    private static bool ReadExactly(Stream stream, byte[] buffer, int count)
    {
        var read = 0;
        while (read < count)
        {
            var n = stream.Read(buffer, read, count - read);
            if (n <= 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }
}

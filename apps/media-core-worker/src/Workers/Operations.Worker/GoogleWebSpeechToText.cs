using System.Text;
using System.Text.Json;
using MediaOpsCore.Modules.Capture.Application;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Speech recognition through Google's web speech endpoint (the free one the Chromium browser uses). The only engine
/// behind <see cref="ISpeechToText"/> for now: for development. A licensed engine is another adapter of the port.
///
/// It turns the PCM into FLAC, asks, and says how the call ended as a status, so a rate limit or an outage is never
/// mistaken for silence.
/// </summary>
public sealed class GoogleWebSpeechToText : ISpeechToText, IDisposable
{
    private const string Endpoint = "http://www.google.com/speech-api/v2/recognize";

    private readonly string apiKey;
    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;
    private readonly Func<byte[], byte[]> encodeFlac;

    // httpClient and encodeFlac are there so tests can run without the network or the FFmpeg libraries.
    public GoogleWebSpeechToText(string apiKey, HttpClient? httpClient = null, Func<byte[], byte[]>? encodeFlac = null)
    {
        this.apiKey = apiKey;
        this.httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        ownsHttpClient = httpClient is null;
        this.encodeFlac = encodeFlac ?? FlacPcmEncoder.Encode;
    }

    /// <summary>
    /// GOOGLE_SPEECH_API_KEY from the environment (Program.cs loads the .env file into it at startup, before anything
    /// else is built).
    /// </summary>
    public static string ResolveApiKey()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("GOOGLE_SPEECH_API_KEY")?.Trim();
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        throw new InvalidOperationException(
            "Missing GOOGLE_SPEECH_API_KEY. Set the environment variable or create a .env file with GOOGLE_SPEECH_API_KEY=<value>.");
    }

    public async Task<SpeechRecognitionResult> RecognizeAsync(
        ReadOnlyMemory<byte> pcm, string language, CancellationToken cancellationToken = default)
    {
        if (pcm.IsEmpty)
        {
            return new SpeechRecognitionResult(SpeechRecognitionStatus.NoSpeech, string.Empty);
        }

        byte[] flac;
        try
        {
            flac = encodeFlac(pcm.ToArray());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new SpeechRecognitionResult(SpeechRecognitionStatus.InvalidAudio, string.Empty);
        }

        if (flac.Length < 4 || flac[0] != (byte)'f' || flac[1] != (byte)'L' || flac[2] != (byte)'a' || flac[3] != (byte)'C')
        {
            return new SpeechRecognitionResult(SpeechRecognitionStatus.InvalidAudio, string.Empty);
        }

        var url = Endpoint
            + $"?client=chromium&lang={Uri.EscapeDataString(language)}"
            + $"&key={Uri.EscapeDataString(apiKey)}&pFilter=0";

        using var content = new ByteArrayContent(flac);
        content.Headers.TryAddWithoutValidation("Content-Type", $"audio/x-flac; rate={SpeechAudio.SampleRate}");

        HttpResponseMessage response;
        try
        {
            response = await httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // the caller is shutting down: let it stop
        }
        catch (OperationCanceledException)
        {
            return new SpeechRecognitionResult(SpeechRecognitionStatus.Timeout, string.Empty); // HttpClient.Timeout elapsed
        }
        catch (HttpRequestException)
        {
            return new SpeechRecognitionResult(SpeechRecognitionStatus.NetworkError, string.Empty);
        }

        using (response)
        {
            // Rate limiting / quota is told apart from other server errors so it is never silently treated as "no speech".
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                return new SpeechRecognitionResult(SpeechRecognitionStatus.RateLimited, string.Empty);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new SpeechRecognitionResult(SpeechRecognitionStatus.HttpError, string.Empty);
            }

            byte[] bodyBytes;
            try
            {
                bodyBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return new SpeechRecognitionResult(SpeechRecognitionStatus.Timeout, string.Empty);
            }

            // Decoded as UTF-8 explicitly. The endpoint returns UTF-8 JSON but its response frequently omits (or
            // misreports) the charset, so ReadAsStringAsync falls back to ISO-8859-1 and mangles accents (señor → seÃ±or).
            var body = Encoding.UTF8.GetString(bodyBytes);
            foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    continue;
                }

                using (document)
                {
                    if (!document.RootElement.TryGetProperty("result", out var result) || result.GetArrayLength() == 0)
                    {
                        continue;
                    }

                    var alternatives = result[0].GetProperty("alternative");
                    if (alternatives.GetArrayLength() > 0 && alternatives[0].TryGetProperty("transcript", out var transcript))
                    {
                        var text = transcript.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            return new SpeechRecognitionResult(SpeechRecognitionStatus.Recognized, text);
                        }
                    }
                }
            }

            // 2xx with no transcript = the API ran and detected no speech (music / silence / non-Spanish).
            return new SpeechRecognitionResult(SpeechRecognitionStatus.NoSpeech, string.Empty);
        }
    }

    public void Dispose()
    {
        if (ownsHttpClient)
        {
            httpClient.Dispose();
        }
    }
}

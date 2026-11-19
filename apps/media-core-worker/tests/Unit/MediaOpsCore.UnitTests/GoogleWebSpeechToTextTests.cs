using System.Net;
using System.Text;
using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

// The one speech engine behind ISpeechToText today: Google's web speech endpoint. The adapter turns the port's PCM into
// FLAC, asks, and maps every way the call can end into a status, so a rate limit is never mistaken for silence.
public sealed class GoogleWebSpeechToTextTests
{
    private static readonly byte[] Pcm = new byte[32_000]; // 1 s of 16 kHz mono s16
    private static readonly byte[] FakeFlac = Encoding.ASCII.GetBytes("fLaC-fake-bytes");

    private sealed class FakeHandler(Func<HttpRequestMessage, byte[], HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(Uri Uri, string? ContentType, byte[] Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add((request.RequestUri!, request.Content?.Headers.ContentType?.ToString(), body));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) }; // no charset, like the real endpoint

    private static (GoogleWebSpeechToText Engine, FakeHandler Handler) Build(
        Func<HttpRequestMessage, byte[], HttpResponseMessage> respond, Func<byte[], byte[]>? encode = null)
    {
        var handler = new FakeHandler(respond);
        return (new GoogleWebSpeechToText("test-key", new HttpClient(handler), encode ?? (_ => FakeFlac)), handler);
    }

    [Fact]
    public async Task Sends_the_audio_as_flac_with_the_language_and_the_key_and_returns_the_transcript()
    {
        var (engine, handler) = Build((_, _) => Json("""
            {"result":[]}
            {"result":[{"alternative":[{"transcript":"hola mundo","confidence":0.9}],"final":true}],"result_index":0}
            """));

        var result = await engine.RecognizeAsync(Pcm, "es-CO");

        Assert.Equal(new SpeechRecognitionResult(SpeechRecognitionStatus.Recognized, "hola mundo"), result);
        var request = Assert.Single(handler.Requests);
        Assert.Contains("lang=es-CO", request.Uri.Query);
        Assert.Contains("key=test-key", request.Uri.Query);
        Assert.Equal("audio/x-flac; rate=16000", request.ContentType);
        Assert.Equal(FakeFlac, request.Body);
    }

    [Fact]
    public async Task Reads_accents_as_utf8_even_when_the_response_declares_no_charset()
    {
        var (engine, _) = Build((_, _) => Json("""{"result":[{"alternative":[{"transcript":"el señor Núñez"}]}]}"""));

        var result = await engine.RecognizeAsync(Pcm, "es-CO");

        Assert.Equal("el señor Núñez", result.Text);
    }

    [Fact]
    public async Task A_successful_answer_with_no_transcript_means_no_speech()
    {
        var (engine, _) = Build((_, _) => Json("""{"result":[]}"""));

        Assert.Equal(SpeechRecognitionStatus.NoSpeech, (await engine.RecognizeAsync(Pcm, "es-CO")).Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, SpeechRecognitionStatus.RateLimited)]
    [InlineData(HttpStatusCode.Forbidden, SpeechRecognitionStatus.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, SpeechRecognitionStatus.HttpError)]
    [InlineData(HttpStatusCode.BadRequest, SpeechRecognitionStatus.HttpError)]
    public async Task Maps_an_error_status_so_it_is_never_taken_for_silence(HttpStatusCode status, SpeechRecognitionStatus expected)
    {
        var (engine, _) = Build((_, _) => Json("{}", status));

        Assert.Equal(expected, (await engine.RecognizeAsync(Pcm, "es-CO")).Status);
    }

    [Fact]
    public async Task A_network_failure_is_a_network_error()
    {
        var (engine, _) = Build((_, _) => throw new HttpRequestException("no route"));

        Assert.Equal(SpeechRecognitionStatus.NetworkError, (await engine.RecognizeAsync(Pcm, "es-CO")).Status);
    }

    [Fact]
    public async Task The_clients_own_timeout_is_a_timeout()
    {
        var (engine, _) = Build((_, _) => throw new TaskCanceledException("HttpClient.Timeout elapsed"));

        Assert.Equal(SpeechRecognitionStatus.Timeout, (await engine.RecognizeAsync(Pcm, "es-CO")).Status);
    }

    [Fact]
    public async Task A_cancellation_from_the_caller_stops_the_call()
    {
        using var cts = new CancellationTokenSource();
        var (engine, _) = Build((_, _) =>
        {
            cts.Cancel();
            throw new TaskCanceledException();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.RecognizeAsync(Pcm, "es-CO", cts.Token));
    }

    [Fact]
    public async Task Empty_audio_is_no_speech_and_asks_nothing()
    {
        var (engine, handler) = Build((_, _) => Json("{}"));

        var result = await engine.RecognizeAsync(ReadOnlyMemory<byte>.Empty, "es-CO");

        Assert.Equal(SpeechRecognitionStatus.NoSpeech, result.Status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Audio_that_cannot_be_encoded_is_invalid_audio()
    {
        var (engine, handler) = Build((_, _) => Json("{}"), _ => throw new InvalidOperationException("encoder failed"));

        Assert.Equal(SpeechRecognitionStatus.InvalidAudio, (await engine.RecognizeAsync(Pcm, "es-CO")).Status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task The_real_encoder_makes_a_flac_stream_out_of_pcm()
    {
        // 1 s of silence through the FFmpeg libraries the worker ships.
        var flac = FlacPcmEncoder.Encode(Pcm);

        Assert.True(flac.Length > 4);
        Assert.Equal("fLaC", Encoding.ASCII.GetString(flac, 0, 4));
        await Task.CompletedTask;
    }
}

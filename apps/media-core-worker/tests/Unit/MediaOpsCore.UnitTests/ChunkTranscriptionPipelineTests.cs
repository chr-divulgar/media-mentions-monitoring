using MediaOpsCore.Modules.Alerting.Application;
using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Workers.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaOpsCore.UnitTests;

using Pipeline = InProcessFfmpegAudioCapturePlugin.ChunkTranscriptionPipeline;

// The live pipeline asks whatever engine sits behind ISpeechToText: its text goes to the hour's JSON and to alert
// detection, and a failed call is recorded as such, never as silence.
public sealed class ChunkTranscriptionPipelineTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 19, 10, 0, 0, TimeSpan.FromHours(-5));
    private readonly string directory = Path.Combine(Path.GetTempPath(), "pipeline-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FakeSpeechToText(Func<int, SpeechRecognitionResult> answer) : ISpeechToText
    {
        public int Calls { get; private set; }
        public List<int> WindowSizes { get; } = [];

        public Task<SpeechRecognitionResult> RecognizeAsync(ReadOnlyMemory<byte> pcm, string language, CancellationToken cancellationToken = default)
        {
            WindowSizes.Add(pcm.Length);
            return Task.FromResult(answer(++Calls));
        }
    }

    private sealed class RecordingAlerts : IDetectAlertsUseCase
    {
        public List<(string Platform, string Media, string Text)> Detected { get; } = [];

        public Task ExecuteAsync(string platform, string media, string filePath, string text, DateTimeOffset startTime, DateTimeOffset endTime, CancellationToken cancellationToken = default)
        {
            lock (Detected) Detected.Add((platform, media, text));
            return Task.CompletedTask;
        }

        public Task RetryPendingNotificationsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<WebArticleAlertOutcome> ExecuteWebArticleAsync(string outlet, string url, string title, string firstParagraph, DateTimeOffset publishedAt, string? matchText = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WebArticleAlertOutcome([], [], 0, 0));

        public Task<int> ExecuteUnreadableWebArticleAsync(string outlet, string url, string title, string reason, string? firstParagraph = null, string? matchText = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    // Disposing the pipeline cancels what is still queued (that is how it shuts down), so wait for the work first.
    private (string Json, RecordingAlerts Alerts) Run(FakeSpeechToText engine, Func<string, RecordingAlerts, bool> done, params byte[][] windows)
    {
        var json = Path.Combine(directory, "radio_10-00-00.json");
        var alerts = new RecordingAlerts();
        var pipeline = new Pipeline(NullLogger.Instance, alerts, engine);

        pipeline.Enqueue(new InProcessFfmpegAudioCapturePlugin.ChunkTranscriptionRequest(
            json, Start, Start.AddSeconds(20), windows, "src-1", "Caracol", "radio", Path.ChangeExtension(json, ".opus")));
        SpinWait.SpinUntil(() => done(json, alerts), TimeSpan.FromSeconds(5));
        Thread.Sleep(150); // anything that should not be written has had its chance
        pipeline.Dispose();

        return (json, alerts);
    }

    [Fact]
    public void The_text_the_engine_returns_goes_to_the_hours_json_and_to_alert_detection()
    {
        var engine = new FakeSpeechToText(_ => new(SpeechRecognitionStatus.Recognized, "hoy habla el presidente de Ecopetrol"));

        var (json, alerts) = Run(engine, (_, a) => a.Detected.Count > 0, new byte[64_000]);

        var written = File.ReadAllText(json);
        Assert.Contains("hoy habla el presidente de Ecopetrol", written);
        Assert.Contains("\"status\": \"recognized\"", written);
        Assert.Equal([("Caracol", "radio", "hoy habla el presidente de Ecopetrol")], alerts.Detected);
    }

    [Fact]
    public void Gives_the_engine_every_window_of_the_chunk_as_pcm_and_joins_the_texts()
    {
        var engine = new FakeSpeechToText(call => new(SpeechRecognitionStatus.Recognized, call == 1 ? "primera parte" : "segunda parte distinta"));

        var (json, _) = Run(engine, (j, a) => a.Detected.Count > 0, new byte[32_000], new byte[16_000]);

        Assert.Equal([32_000, 16_000], engine.WindowSizes);
        Assert.Contains("primera parte segunda parte distinta", File.ReadAllText(json));
    }

    [Fact]
    public void A_rate_limit_is_written_as_such_and_raises_no_alert()
    {
        var engine = new FakeSpeechToText(_ => new(SpeechRecognitionStatus.RateLimited, string.Empty));

        var (json, alerts) = Run(engine, (j, a) => File.Exists(j) && (a.Detected.Count > 0 || engine.Calls > 0), new byte[64_000]);

        var written = File.ReadAllText(json);
        Assert.Contains("\"status\": \"rate_limited\"", written);
        Assert.Contains("[límite de API]", written);
        Assert.Empty(alerts.Detected);
    }

    [Fact]
    public void Silence_writes_nothing_and_raises_no_alert()
    {
        var engine = new FakeSpeechToText(_ => new(SpeechRecognitionStatus.NoSpeech, string.Empty));

        var (json, alerts) = Run(engine, (_, _) => engine.Calls > 0, new byte[64_000]);

        Assert.DoesNotContain("recognized", File.Exists(json) ? File.ReadAllText(json) : string.Empty);
        Assert.Empty(alerts.Detected);
    }
}

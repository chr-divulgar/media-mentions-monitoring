using MediaOpsCore.Modules.Alerting.Application;
using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Workers.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaOpsCore.UnitTests;

using Pipeline = InProcessFfmpegAudioCapturePlugin.ChunkTranscriptionPipeline;

// The live pipeline asks whatever engine sits behind ISpeechToText: its text goes to the transcription store (the
// `transcription` collection) and to alert detection, and a failed call is recorded as such, never as silence.
public sealed class ChunkTranscriptionPipelineTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 19, 10, 0, 0, TimeSpan.FromHours(-5));
    private readonly string directory = Path.Combine(Path.GetTempPath(), "pipeline-" + Guid.NewGuid().ToString("N"));
    private string HourFile => Path.Combine(directory, "radio_2026-11-19_10-00-00.opus");

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

    private sealed class FakeStore : ITranscriptionStore
    {
        private readonly List<TranscriptionEntry> entries = [];
        public int LoadCalls { get; private set; }
        public int AppendCalls { get; private set; }
        // How many appends fail before one works (int.MaxValue: never works).
        public int FailAppends { get; set; }

        public IReadOnlyList<TranscriptionEntry> Entries
        {
            get
            {
                lock (entries) return entries.ToArray();
            }
        }

        public void Seed(TranscriptionEntry entry)
        {
            lock (entries) entries.Add(entry);
        }

        public Task<IReadOnlyList<TranscriptionEntry>> LoadHourAsync(string filePath, CancellationToken cancellationToken = default)
        {
            LoadCalls++;
            lock (entries) return Task.FromResult<IReadOnlyList<TranscriptionEntry>>(entries.Where(e => e.FilePath == filePath).OrderBy(e => e.StartTime).ToArray());
        }

        public Task AppendAsync(TranscriptionEntry entry, CancellationToken cancellationToken = default)
        {
            AppendCalls++;
            if (FailAppends > 0)
            {
                FailAppends--;
                throw new InvalidOperationException("mongo down");
            }

            lock (entries) entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task AppendManyAsync(IReadOnlyList<TranscriptionEntry> many, CancellationToken cancellationToken = default)
        {
            lock (entries) entries.AddRange(many);
            return Task.CompletedTask;
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

    private sealed class RecordingIncidents : ISourceIncidentRecorder
    {
        public List<(string SourceId, string Kind)> Recorded { get; } = [];

        public void Record(string sourceId, string kind, string? reason = null)
        {
            lock (Recorded) Recorded.Add((sourceId, kind));
        }
    }

    private (RecordingAlerts Alerts, RecordingIncidents Incidents) Run(
        FakeSpeechToText engine, FakeStore store, Func<RecordingAlerts, bool> done, params byte[][] windows)
    {
        var alerts = new RecordingAlerts();
        var incidents = new RecordingIncidents();
        var pipeline = new Pipeline(NullLogger.Instance, alerts, engine, store, incidents, delay: (_, _) => Task.CompletedTask);

        pipeline.Enqueue(new InProcessFfmpegAudioCapturePlugin.ChunkTranscriptionRequest(
            Start, Start.AddSeconds(20), windows, "src-1", "Caracol", "radio", HourFile));
        // Disposing the pipeline cancels what is still queued (that is how it shuts down), so wait for the work first.
        SpinWait.SpinUntil(() => done(alerts), TimeSpan.FromSeconds(5));
        Thread.Sleep(150); // anything that should not be saved has had its chance
        pipeline.Dispose();

        return (alerts, incidents);
    }

    [Fact]
    public void The_text_the_engine_returns_goes_to_the_transcription_store_and_to_alert_detection()
    {
        var engine = new FakeSpeechToText(_ => new(SpeechRecognitionStatus.Recognized, "hoy habla el presidente de Ecopetrol"));
        var store = new FakeStore();

        var (alerts, _) = Run(engine, store, a => a.Detected.Count > 0, new byte[64_000]);

        var saved = Assert.Single(store.Entries);
        Assert.Equal("hoy habla el presidente de Ecopetrol", saved.Text);
        Assert.Equal("recognized", saved.Status);
        Assert.Equal(("src-1", "Caracol", "radio", HourFile), (saved.SourceId, saved.Platform, saved.Media, saved.FilePath));
        Assert.Equal(Start, saved.StartTime);
        Assert.Equal(Start.AddSeconds(20), saved.EndTime);
        Assert.Equal([("Caracol", "radio", "hoy habla el presidente de Ecopetrol")], alerts.Detected);
    }

    [Fact]
    public void Nothing_is_written_to_disk_any_more_next_to_the_audio()
    {
        var engine = new FakeSpeechToText(_ => new(SpeechRecognitionStatus.Recognized, "hola mundo"));

        Run(engine, new FakeStore(), a => a.Detected.Count > 0, new byte[64_000]);

        Assert.False(Directory.Exists(directory), "the pipeline must not create a folder or a JSON for the transcription");
    }

    [Fact]
    public void Gives_the_engine_every_window_of_the_chunk_as_pcm_and_joins_the_texts()
    {
        var engine = new FakeSpeechToText(call => new(SpeechRecognitionStatus.Recognized, call == 1 ? "primera parte" : "segunda parte distinta"));
        var store = new FakeStore();

        Run(engine, store, a => a.Detected.Count > 0, new byte[32_000], new byte[16_000]);

        Assert.Equal([32_000, 16_000], engine.WindowSizes);
        Assert.Equal("primera parte segunda parte distinta", Assert.Single(store.Entries).Text);
    }

    [Fact]
    public void A_rate_limit_is_saved_as_such_and_raises_no_alert()
    {
        var engine = new FakeSpeechToText(_ => new(SpeechRecognitionStatus.RateLimited, string.Empty));
        var store = new FakeStore();

        var (alerts, _) = Run(engine, store, _ => store.Entries.Count > 0, new byte[64_000]);

        var saved = Assert.Single(store.Entries);
        Assert.Equal("rate_limited", saved.Status);
        Assert.Equal("[límite de API]", saved.Text);
        Assert.Empty(alerts.Detected);
    }

    [Fact]
    public void Silence_is_saved_with_its_status_so_a_gap_always_means_the_audio_was_never_sent()
    {
        var engine = new FakeSpeechToText(_ => new(SpeechRecognitionStatus.NoSpeech, string.Empty));
        var store = new FakeStore();

        var (alerts, _) = Run(engine, store, _ => store.Entries.Count > 0, new byte[64_000]);

        var saved = Assert.Single(store.Entries);
        Assert.Equal("no_speech", saved.Status);
        Assert.Equal(string.Empty, saved.Text);
        Assert.Empty(alerts.Detected);
    }

    [Fact]
    public void Strips_the_words_the_previous_chunk_already_had_even_after_a_restart()
    {
        // The hour was being transcribed before the service restarted: its earlier chunk is already in the store.
        var store = new FakeStore();
        store.Seed(new TranscriptionEntry("src-1", "Caracol", "radio", HourFile, Start.AddSeconds(-20), Start, "uno dos tres cuatro cinco seis siete ocho", "recognized"));
        var engine = new FakeSpeechToText(_ => new(SpeechRecognitionStatus.Recognized, "cinco seis siete ocho nueve diez once doce trece"));

        var (alerts, _) = Run(engine, store, a => a.Detected.Count > 0, new byte[64_000]);

        var added = store.Entries.Single(e => e.StartTime == Start);
        Assert.Equal("nueve diez once doce trece", added.Text);
        Assert.Equal(["nueve diez once doce trece"], alerts.Detected.Select(d => d.Text));
        Assert.Equal(1, store.LoadCalls);
    }

    [Fact]
    public void Asks_for_the_hour_once_and_keeps_it_in_memory_for_the_next_chunks()
    {
        var engine = new FakeSpeechToText(call => new(SpeechRecognitionStatus.Recognized, call == 1 ? "primer texto del bloque" : "segundo texto del bloque"));
        var store = new FakeStore();
        var alerts = new RecordingAlerts();
        var pipeline = new Pipeline(NullLogger.Instance, alerts, engine, store, null, delay: (_, _) => Task.CompletedTask);

        for (var i = 0; i < 2; i++)
        {
            pipeline.Enqueue(new InProcessFfmpegAudioCapturePlugin.ChunkTranscriptionRequest(
                Start.AddSeconds(20 * i), Start.AddSeconds(20 * (i + 1)), [new byte[64_000]], "src-1", "Caracol", "radio", HourFile));
        }

        SpinWait.SpinUntil(() => alerts.Detected.Count == 2, TimeSpan.FromSeconds(5));
        pipeline.Dispose();

        Assert.Equal(2, store.Entries.Count);
        Assert.Equal(1, store.LoadCalls);
    }

    [Fact]
    public void Tries_again_with_a_growing_wait_when_the_database_fails_and_then_saves()
    {
        var engine = new FakeSpeechToText(_ => new(SpeechRecognitionStatus.Recognized, "texto que se guarda"));
        var store = new FakeStore { FailAppends = 2 };

        var (alerts, incidents) = Run(engine, store, a => a.Detected.Count > 0, new byte[64_000]);

        Assert.Equal(3, store.AppendCalls);
        Assert.Single(store.Entries);
        Assert.Empty(incidents.Recorded);
        Assert.Single(alerts.Detected);
    }

    [Fact]
    public void When_the_database_never_answers_it_says_so_once_and_does_not_hold_back_the_alert()
    {
        var engine = new FakeSpeechToText(_ => new(SpeechRecognitionStatus.Recognized, "texto que no se pudo guardar"));
        var store = new FakeStore { FailAppends = int.MaxValue };

        var (alerts, incidents) = Run(engine, store, a => a.Detected.Count > 0, new byte[64_000]);

        Assert.Empty(store.Entries);
        Assert.Equal(4, store.AppendCalls); // the first try and three more
        Assert.Equal([("src-1", IncidentKinds.TranscriptionNotSaved)], incidents.Recorded);
        Assert.Single(alerts.Detected);
    }
}

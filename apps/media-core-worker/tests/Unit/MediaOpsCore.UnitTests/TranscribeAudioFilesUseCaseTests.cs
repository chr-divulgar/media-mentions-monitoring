using MediaOpsCore.Modules.Capture.Application;
using Xunit;

namespace MediaOpsCore.UnitTests;

// The portal's "transcribe" button: a few WAV chunks in, the text of each one out. The engine is whatever sits behind
// ISpeechToText; what is decided here is the order, the retries, the limit on parallel calls and what a failure is.
public sealed class TranscribeAudioFilesUseCaseTests
{
    // A file is "recognized" as the length of its PCM, so a fake engine can answer per file without knowing paths.
    private sealed class FakeReader(Dictionary<string, byte[]> files) : IPcmFileReader
    {
        public Task<byte[]> ReadAsync(string path, CancellationToken cancellationToken = default) =>
            files.TryGetValue(path, out var pcm) ? Task.FromResult(pcm) : throw new FileNotFoundException(path);
    }

    private sealed class FakeEngine(Func<int, int, SpeechRecognitionResult> answer) : ISpeechToText
    {
        private int running;
        public int MaxRunning { get; private set; }
        public Dictionary<int, int> CallsByLength { get; } = [];

        public async Task<SpeechRecognitionResult> RecognizeAsync(ReadOnlyMemory<byte> pcm, string language, CancellationToken cancellationToken = default)
        {
            int call;
            lock (CallsByLength)
            {
                call = CallsByLength[pcm.Length] = CallsByLength.GetValueOrDefault(pcm.Length) + 1;
                MaxRunning = Math.Max(MaxRunning, Interlocked.Increment(ref running));
            }

            try
            {
                await Task.Delay(15, cancellationToken);
                return answer(pcm.Length, call);
            }
            finally
            {
                Interlocked.Decrement(ref running);
            }
        }
    }

    private static (TranscribeAudioFilesUseCase Sut, List<TimeSpan> Delays) Build(FakeEngine engine, params (string Path, int Length)[] files)
    {
        var delays = new List<TimeSpan>();
        var reader = new FakeReader(files.ToDictionary(f => f.Path, f => new byte[f.Length]));
        return (new TranscribeAudioFilesUseCase(engine, reader, (delay, _) => { lock (delays) delays.Add(delay); return Task.CompletedTask; }), delays);
    }

    private static SpeechRecognitionResult Said(string text) => new(SpeechRecognitionStatus.Recognized, text);
    private static SpeechRecognitionResult Failed(SpeechRecognitionStatus status) => new(status, string.Empty);

    [Fact]
    public async Task Returns_the_text_of_every_file_in_the_order_of_the_paths()
    {
        var engine = new FakeEngine((length, _) => Said($"texto de {length}"));
        var (sut, _) = Build(engine, ("a.wav", 100), ("b.wav", 200), ("c.wav", 300));

        var chunks = await sut.TranscribeAsync(["c.wav", "a.wav", "b.wav"], "es-CO");

        Assert.Equal(["texto de 300", "texto de 100", "texto de 200"], chunks.Select(c => c.Text));
        Assert.All(chunks, chunk => Assert.Equal("recognized", chunk.Status));
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_is_an_audio_error_and_the_others_carry_on()
    {
        var engine = new FakeEngine((length, _) => Said($"texto de {length}"));
        var (sut, _) = Build(engine, ("a.wav", 100));

        var chunks = await sut.TranscribeAsync(["missing.wav", "a.wav"], "es-CO");

        Assert.Equal(new TranscriptionChunkResult("audio_error", ""), chunks[0]);
        Assert.Equal(new TranscriptionChunkResult("recognized", "texto de 100"), chunks[1]);
    }

    [Fact]
    public async Task Silence_is_a_result_not_a_failure_and_is_not_asked_again()
    {
        var engine = new FakeEngine((_, _) => Failed(SpeechRecognitionStatus.NoSpeech));
        var (sut, delays) = Build(engine, ("a.wav", 100));

        var chunks = await sut.TranscribeAsync(["a.wav"], "es-CO");

        Assert.Equal(new TranscriptionChunkResult("no_speech", ""), Assert.Single(chunks));
        Assert.Equal(1, engine.CallsByLength[100]);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Asks_again_with_a_growing_wait_when_the_engine_is_rate_limited_and_then_answers()
    {
        var engine = new FakeEngine((_, call) => call < 3 ? Failed(SpeechRecognitionStatus.RateLimited) : Said("por fin"));
        var (sut, delays) = Build(engine, ("a.wav", 100));

        var chunks = await sut.TranscribeAsync(["a.wav"], "es-CO");

        Assert.Equal(new TranscriptionChunkResult("recognized", "por fin"), Assert.Single(chunks));
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)], delays);
    }

    [Fact]
    public async Task Gives_up_after_three_retries_and_reports_why()
    {
        var engine = new FakeEngine((_, _) => Failed(SpeechRecognitionStatus.RateLimited));
        var (sut, delays) = Build(engine, ("a.wav", 100));

        var chunks = await sut.TranscribeAsync(["a.wav"], "es-CO");

        Assert.Equal(new TranscriptionChunkResult("rate_limited", ""), Assert.Single(chunks));
        Assert.Equal(4, engine.CallsByLength[100]);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(9)], delays);
    }

    [Theory]
    [InlineData(SpeechRecognitionStatus.HttpError, "api_error")]
    [InlineData(SpeechRecognitionStatus.Timeout, "timeout")]
    [InlineData(SpeechRecognitionStatus.NetworkError, "network_error")]
    public async Task Retries_every_kind_of_transient_failure(SpeechRecognitionStatus status, string label)
    {
        var engine = new FakeEngine((_, _) => Failed(status));
        var (sut, _) = Build(engine, ("a.wav", 100));

        var chunks = await sut.TranscribeAsync(["a.wav"], "es-CO");

        Assert.Equal(label, Assert.Single(chunks).Status);
        Assert.Equal(4, engine.CallsByLength[100]);
    }

    [Fact]
    public async Task Does_not_retry_audio_the_engine_calls_invalid()
    {
        var engine = new FakeEngine((_, _) => Failed(SpeechRecognitionStatus.InvalidAudio));
        var (sut, _) = Build(engine, ("a.wav", 100));

        var chunks = await sut.TranscribeAsync(["a.wav"], "es-CO");

        Assert.Equal("audio_error", Assert.Single(chunks).Status);
        Assert.Equal(1, engine.CallsByLength[100]);
    }

    [Fact]
    public async Task Never_asks_the_engine_for_more_than_three_files_at_once()
    {
        var engine = new FakeEngine((length, _) => Said("x"));
        var files = Enumerable.Range(1, 12).Select(i => ($"f{i}.wav", i * 10)).ToArray();
        var (sut, _) = Build(engine, files);

        await sut.TranscribeAsync(files.Select(f => f.Item1).ToArray(), "es-CO");

        Assert.InRange(engine.MaxRunning, 2, TranscribeAudioFilesUseCase.MaxConcurrency);
    }

    [Fact]
    public async Task Stops_when_the_caller_cancels()
    {
        var engine = new FakeEngine((_, _) => Said("x"));
        var (sut, _) = Build(engine, ("a.wav", 100));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.TranscribeAsync(["a.wav"], "es-CO", cts.Token));
    }
}

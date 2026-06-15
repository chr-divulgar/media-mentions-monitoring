using MediaOpsCore.BuildingBlocks.Application;
using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Modules.Capture.Domain;
using MediaOpsCore.Workers.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class YtdlpLiveStreamUrlResolverTests
{
    private const string ValidHlsUrl = "https://manifest.googlevideo.com/api/manifest/hls_playlist/expire/12345/id/abc.m3u8";
    private const string YoutubeChannelUrl = "https://www.youtube.com/@noticiascaracol/live";

    // ── CanResolve ──────────────────────────────────────────────────────────

    [Fact]
    public void CanResolve_returns_true_when_media_is_television_and_platform_is_youtube()
    {
        var sut = BuildResolver(new FakeProcessRunner());
        var source = MakeSource("television", "youtube");
        Assert.True(sut.CanResolve(source));
    }

    [Fact]
    public void CanResolve_returns_false_when_media_is_radio()
    {
        var sut = BuildResolver(new FakeProcessRunner());
        var source = MakeSource("radio", "BluRadio");
        Assert.False(sut.CanResolve(source));
    }

    [Fact]
    public void CanResolve_returns_false_when_media_is_television_but_platform_is_not_youtube()
    {
        var sut = BuildResolver(new FakeProcessRunner());
        var source = MakeSource("television", "twitch");
        Assert.False(sut.CanResolve(source));
    }

    [Theory]
    [InlineData("Television", "YouTube")]
    [InlineData("TELEVISION", "YOUTUBE")]
    [InlineData("television", "youtube")]
    public void CanResolve_is_case_insensitive(string media, string platform)
    {
        var sut = BuildResolver(new FakeProcessRunner());
        var source = MakeSource(media, platform);
        Assert.True(sut.CanResolve(source));
    }

    // ── TryResolveStreamUrlAsync — success ─────────────────────────────────

    [Fact]
    public async Task TryResolveStreamUrlAsync_returns_url_when_process_exits_zero_with_valid_url()
    {
        var fake = new FakeProcessRunner(exitCode: 0, stdout: ValidHlsUrl);
        var sut = BuildResolver(fake);
        var source = MakeSource();

        var result = await sut.TryResolveStreamUrlAsync(source);

        Assert.True(result.Succeeded);
        Assert.Equal(ValidHlsUrl, result.Url);
        Assert.Null(result.Failure);
    }

    [Fact]
    public async Task TryResolveStreamUrlAsync_takes_only_first_line_when_stdout_has_multiple_lines()
    {
        var fake = new FakeProcessRunner(exitCode: 0, stdout: $"{ValidHlsUrl}\nhttps://other.url/second");
        var sut = BuildResolver(fake);
        var result = await sut.TryResolveStreamUrlAsync(MakeSource());

        Assert.True(result.Succeeded);
        Assert.Equal(ValidHlsUrl, result.Url);
    }

    // ── TryResolveStreamUrlAsync — failures ────────────────────────────────

    [Fact]
    public async Task TryResolveStreamUrlAsync_returns_Unavailable_when_process_exits_nonzero()
    {
        var fake = new FakeProcessRunner(exitCode: 1, stdout: "", stderr: "Some network error");
        var sut = BuildResolver(fake);
        var result = await sut.TryResolveStreamUrlAsync(MakeSource());

        Assert.False(result.Succeeded);
        Assert.Equal(LiveStreamResolutionFailure.Unavailable, result.Failure);
    }

    [Fact]
    public async Task TryResolveStreamUrlAsync_returns_Unavailable_when_stdout_is_empty()
    {
        var fake = new FakeProcessRunner(exitCode: 0, stdout: "");
        var sut = BuildResolver(fake);
        var result = await sut.TryResolveStreamUrlAsync(MakeSource());

        Assert.False(result.Succeeded);
        Assert.Equal(LiveStreamResolutionFailure.Unavailable, result.Failure);
    }

    [Fact]
    public async Task TryResolveStreamUrlAsync_returns_Unavailable_when_stdout_is_not_absolute_url()
    {
        var fake = new FakeProcessRunner(exitCode: 0, stdout: "not-a-url");
        var sut = BuildResolver(fake);
        var result = await sut.TryResolveStreamUrlAsync(MakeSource());

        Assert.False(result.Succeeded);
        Assert.Equal(LiveStreamResolutionFailure.Unavailable, result.Failure);
    }

    // ── Auth failure classification ─────────────────────────────────────────

    [Theory]
    [InlineData("Sign in to confirm you're not a bot")]
    [InlineData("ERROR: This video is only available to signed-in users")]
    [InlineData("HTTP Error 403: Forbidden")]
    [InlineData("HTTP Error 429: Too Many Requests")]
    public async Task TryResolveStreamUrlAsync_returns_AuthRequired_when_stderr_contains_auth_pattern(string stderr)
    {
        var fake = new FakeProcessRunner(exitCode: 1, stderr: stderr);
        var sut = BuildResolver(fake);
        var result = await sut.TryResolveStreamUrlAsync(MakeSource());

        Assert.False(result.Succeeded);
        Assert.Equal(LiveStreamResolutionFailure.AuthRequired, result.Failure);
    }

    [Fact]
    public async Task TryResolveStreamUrlAsync_returns_Unavailable_when_stderr_is_generic_network_error()
    {
        var fake = new FakeProcessRunner(exitCode: 1, stderr: "Connection timed out after 30 seconds");
        var sut = BuildResolver(fake);
        var result = await sut.TryResolveStreamUrlAsync(MakeSource());

        Assert.False(result.Succeeded);
        Assert.Equal(LiveStreamResolutionFailure.Unavailable, result.Failure);
    }

    // ── Cookies ────────────────────────────────────────────────────────────

    [Fact]
    public async Task TryResolveStreamUrlAsync_does_not_pass_cookies_flag_when_option_is_null()
    {
        var fake = new FakeProcessRunner(exitCode: 0, stdout: ValidHlsUrl);
        var sut = BuildResolver(fake, cookiesFilePath: null);
        await sut.TryResolveStreamUrlAsync(MakeSource());

        Assert.DoesNotContain("--cookies", fake.LastArgs ?? []);
    }

    [Fact]
    public async Task TryResolveStreamUrlAsync_returns_AuthRequired_when_cookies_configured_but_file_not_found()
    {
        var fake = new FakeProcessRunner(exitCode: 0, stdout: ValidHlsUrl);
        var sut = BuildResolver(fake, cookiesFilePath: "/nonexistent/path/cookies.txt");
        var result = await sut.TryResolveStreamUrlAsync(MakeSource());

        Assert.False(result.Succeeded);
        Assert.Equal(LiveStreamResolutionFailure.AuthRequired, result.Failure);
    }

    [Fact]
    public async Task TryResolveStreamUrlAsync_passes_cookies_flag_when_file_exists()
    {
        var tempCookies = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempCookies, "# Netscape HTTP Cookie File");
            var fake = new FakeProcessRunner(exitCode: 0, stdout: ValidHlsUrl);
            var sut = BuildResolver(fake, cookiesFilePath: tempCookies);
            await sut.TryResolveStreamUrlAsync(MakeSource());

            Assert.Contains("--cookies", fake.LastArgs ?? []);
        }
        finally
        {
            File.Delete(tempCookies);
        }
    }

    // ── BinaryNotFound ─────────────────────────────────────────────────────

    [Fact]
    public async Task TryResolveStreamUrlAsync_returns_BinaryNotFound_when_binary_provider_throws()
    {
        var sut = BuildResolverWithThrowingProvider();
        var result = await sut.TryResolveStreamUrlAsync(MakeSource());

        Assert.False(result.Succeeded);
        Assert.Equal(LiveStreamResolutionFailure.BinaryNotFound, result.Failure);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static YtdlpLiveStreamUrlResolver BuildResolver(
        FakeProcessRunner fake,
        string? cookiesFilePath = null)
    {
        var options = new OperationsWorkerOptions
        {
            YtdlpResolutionTimeoutSeconds = 30,
            YoutubeCookiesFilePath = cookiesFilePath,
        };

        return new YtdlpLiveStreamUrlResolver(
            new FakeYtdlpBinaryProvider(),
            fake,
            options,
            NullLogger<YtdlpLiveStreamUrlResolver>.Instance);
    }

    private static YtdlpLiveStreamUrlResolver BuildResolverWithThrowingProvider()
    {
        var options = new OperationsWorkerOptions { YtdlpResolutionTimeoutSeconds = 30 };
        var fake = new FakeProcessRunner();
        return new YtdlpLiveStreamUrlResolver(
            new ThrowingYtdlpBinaryProvider(),
            fake,
            options,
            NullLogger<YtdlpLiveStreamUrlResolver>.Instance);
    }

    private static CaptureSource MakeSource(string media = "television", string platform = "youtube")
        => new(
            sourceId: "noticias-caracol-live",
            tenantId: "default",
            platform: platform,
            media: media,
            streamUrl: YoutubeChannelUrl);

    // ── Fakes ──────────────────────────────────────────────────────────────

    private sealed class FakeProcessRunner : IProcessRunner
    {
        private readonly int exitCode;
        private readonly string stdout;
        private readonly string stderr;

        public string[]? LastArgs { get; private set; }

        public FakeProcessRunner(int exitCode = 0, string stdout = "", string stderr = "")
        {
            this.exitCode = exitCode;
            this.stdout = stdout;
            this.stderr = stderr;
        }

        public Task<ProcessExecutionResult> RunAsync(ProcessCommand command, CancellationToken cancellationToken = default)
        {
            LastArgs = [.. command.Arguments];
            return Task.FromResult(new ProcessExecutionResult(exitCode, stdout, stderr, TimedOut: false));
        }
    }

    private sealed class FakeYtdlpBinaryProvider : IYtdlpBinaryProvider
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<(string Cmd, string[] Args)> GetCommandAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(("yt-dlp", Array.Empty<string>()));
    }

    private sealed class ThrowingYtdlpBinaryProvider : IYtdlpBinaryProvider
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<(string Cmd, string[] Args)> GetCommandAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("yt-dlp binary not available.");
    }
}

using MediaOpsCore.BuildingBlocks.Application;
using MediaOpsCore.Workers.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class YouTubeCookiesValidatorTests
{
    // expiration == 0 is the Netscape format's "session cookie" sentinel (cleared when the
    // browser closes) — it must never be read as "expired at the Unix epoch".
    [Fact]
    public async Task ValidateCookiesAsync_does_not_treat_a_session_cookie_as_expired()
    {
        var sut = BuildValidator();
        var content = NetscapeFile(("SESSION_TOKEN", expiration: 0));

        var result = await sut.ValidateCookiesAsync(cookiesFilePath: null, cookiesContent: content);

        Assert.True(result.IsValid);
        Assert.Null(result.EarliestExpiration);
    }

    [Fact]
    public async Task ValidateCookiesAsync_ignores_session_cookies_when_computing_earliest_expiration()
    {
        var sut = BuildValidator();
        var farFuture = DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds();
        var content = NetscapeFile(
            ("SESSION_TOKEN", expiration: 0),
            ("LOGIN_INFO", expiration: farFuture));

        var result = await sut.ValidateCookiesAsync(cookiesFilePath: null, cookiesContent: content);

        Assert.True(result.IsValid);
        Assert.NotNull(result.EarliestExpiration);
        Assert.True(result.EarliestExpiration > DateTime.UtcNow);
    }

    [Fact]
    public async Task ValidateCookiesAsync_still_reports_expired_when_a_persistent_cookie_is_past()
    {
        var sut = BuildValidator();
        var content = NetscapeFile(("LOGIN_INFO", expiration: 1));

        var result = await sut.ValidateCookiesAsync(cookiesFilePath: null, cookiesContent: content);

        Assert.False(result.IsValid);
        Assert.Equal(new DateTime(1970, 1, 1, 0, 0, 1, DateTimeKind.Utc), result.EarliestExpiration);
    }

    private static YouTubeCookiesValidator BuildValidator() =>
        new(new FakeYtdlpBinaryProvider(), new FakeProcessRunner(), NullLogger<YouTubeCookiesValidator>.Instance);

    private static string NetscapeFile(params (string Name, long expiration)[] cookies)
    {
        var lines = new List<string> { "# Netscape HTTP Cookie File" };
        lines.AddRange(cookies.Select(c => $".youtube.com\tTRUE\t/\tTRUE\t{c.expiration}\t{c.Name}\tvalue"));
        return string.Join('\n', lines);
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        public Task<ProcessExecutionResult> RunAsync(ProcessCommand command, CancellationToken cancellationToken = default)
            => Task.FromResult(new ProcessExecutionResult(0, "", "", TimedOut: false));
    }

    private sealed class FakeYtdlpBinaryProvider : IYtdlpBinaryProvider
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<(string Cmd, string[] Args)> GetCommandAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(("yt-dlp", Array.Empty<string>()));
    }
}

using MediaOpsCore.Workers.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class YouTubeCookiesAlertServiceTests : IDisposable
{
    private readonly string tempDir;
    private readonly string alertPath;
    private readonly YouTubeCookiesAlertService sut;

    public YouTubeCookiesAlertServiceTests()
    {
        tempDir = Path.Combine(Path.GetTempPath(), $"yt-alert-test-{Guid.NewGuid():N}");
        alertPath = Path.Combine(tempDir, "youtube-auth-required.flag");

        var options = new OperationsWorkerOptions
        {
            YoutubeCookiesAlertFilePath = alertPath,
        };

        sut = new YouTubeCookiesAlertService(options, NullLogger<YouTubeCookiesAlertService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(tempDir))
            Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public void AlertExists_returns_false_when_flag_file_does_not_exist()
    {
        Assert.False(sut.AlertExists());
    }

    [Fact]
    public void AlertExists_returns_true_when_flag_file_exists()
    {
        sut.WriteAlert("test-source", "some error");
        Assert.True(sut.AlertExists());
    }

    [Fact]
    public void WriteAlert_creates_flag_file_with_source_and_error_in_content()
    {
        sut.WriteAlert("noticias-caracol-live", "Sign in to confirm you're not a bot");

        Assert.True(File.Exists(alertPath));
        var content = File.ReadAllText(alertPath);
        Assert.Contains("noticias-caracol-live", content);
        Assert.Contains("Sign in to confirm you're not a bot", content);
        Assert.Contains("Action required", content);
    }

    [Fact]
    public void WriteAlert_overwrites_existing_flag_file()
    {
        sut.WriteAlert("source-a", "first error");
        var firstModified = File.GetLastWriteTimeUtc(alertPath);

        // Small delay so write times differ
        Thread.Sleep(10);
        sut.WriteAlert("source-b", "second error");

        var content = File.ReadAllText(alertPath);
        Assert.Contains("source-b", content);
        Assert.Contains("second error", content);
    }

    [Fact]
    public void ClearAlert_deletes_flag_file()
    {
        sut.WriteAlert("source", "error");
        sut.ClearAlert();
        Assert.False(File.Exists(alertPath));
    }

    [Fact]
    public void ClearAlert_does_not_throw_when_file_does_not_exist()
    {
        var ex = Record.Exception(() => sut.ClearAlert());
        Assert.Null(ex);
    }

    [Fact]
    public void AlertFilePath_matches_configured_path()
    {
        Assert.Equal(Path.GetFullPath(alertPath), sut.AlertFilePath);
    }
}

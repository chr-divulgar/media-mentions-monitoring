using MediaOpsCore.Workers.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class YouTubeCookiesHttpServiceTests : IDisposable
{
    private const string ValidNetscapeCookies =
        "# Netscape HTTP Cookie File\n" +
        ".youtube.com\tTRUE\t/\tTRUE\t9999999999\tSID\tabc123\n";

    private readonly string tempDir;
    private readonly string cookiesPath;

    public YouTubeCookiesHttpServiceTests()
    {
        tempDir = Path.Combine(Path.GetTempPath(), $"yt-http-test-{Guid.NewGuid():N}");
        cookiesPath = Path.Combine(tempDir, "youtube-cookies.txt");
    }

    public void Dispose()
    {
        if (Directory.Exists(tempDir))
            Directory.Delete(tempDir, recursive: true);
    }

    private YouTubeCookiesHttpService CreateSut(
        FakeValidator? validator = null,
        FakeAlertService? alertService = null,
        FakeReconciliationTrigger? trigger = null,
        string? configuredCookiesPath = null)
    {
        var options = new OperationsWorkerOptions
        {
            YoutubeCookiesFilePath = configuredCookiesPath ?? cookiesPath,
        };

        return new YouTubeCookiesHttpService(
            NullLogger<YouTubeCookiesHttpService>.Instance,
            options,
            validator ?? new FakeValidator(isValid: true),
            alertService ?? new FakeAlertService(),
            new FakeHealthSnapshotProvider(),
            trigger ?? new FakeReconciliationTrigger());
    }

    [Fact]
    public async Task ProcessCookiesSubmissionAsync_writes_file_clears_alert_and_triggers_reconciliation_when_valid()
    {
        var alertService = new FakeAlertService();
        var trigger = new FakeReconciliationTrigger();
        var sut = CreateSut(validator: new FakeValidator(isValid: true), alertService: alertService, trigger: trigger);

        var (statusCode, response) = await sut.ProcessCookiesSubmissionAsync(ValidNetscapeCookies, CancellationToken.None);

        Assert.Equal(200, statusCode);
        Assert.True(response.Success);
        Assert.True(File.Exists(cookiesPath));
        Assert.Equal(ValidNetscapeCookies, await File.ReadAllTextAsync(cookiesPath));
        Assert.True(alertService.ClearAlertCalled);

        // Reconciliation is fired via Task.Run — give it a moment to run.
        await trigger.Invoked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(trigger.Invoked.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ProcessCookiesSubmissionAsync_rejects_invalid_cookies_without_writing_file_or_clearing_alert()
    {
        var alertService = new FakeAlertService();
        var trigger = new FakeReconciliationTrigger();
        var sut = CreateSut(
            validator: new FakeValidator(isValid: false, message: "Cookies expired at 2020-01-01"),
            alertService: alertService,
            trigger: trigger);

        var (statusCode, response) = await sut.ProcessCookiesSubmissionAsync("garbage content", CancellationToken.None);

        Assert.Equal(422, statusCode);
        Assert.False(response.Success);
        Assert.Contains("expired", response.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(cookiesPath));
        Assert.False(alertService.ClearAlertCalled);
        Assert.False(trigger.WasInvoked);
    }

    [Fact]
    public async Task ProcessCookiesSubmissionAsync_returns_configuration_error_when_cookies_path_not_set()
    {
        var sut = CreateSut(configuredCookiesPath: "");

        var (statusCode, response) = await sut.ProcessCookiesSubmissionAsync(ValidNetscapeCookies, CancellationToken.None);

        Assert.Equal(500, statusCode);
        Assert.False(response.Success);
    }

    private sealed class FakeValidator(bool isValid, string message = "Valid") : IYouTubeCookiesValidator
    {
        public Task<CookiesValidationResult> ValidateCookiesAsync(string? cookiesFilePath, string? cookiesContent = null) =>
            Task.FromResult(new CookiesValidationResult(
                IsValid: isValid, FileExists: true, CookieCount: isValid ? 1 : 0,
                EarliestExpiration: null, HasYouTubeDomain: isValid, Message: message));

        public Task<bool> TestCookiesWithYtdlpAsync(string cookiesFilePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(isValid);
    }

    private sealed class FakeAlertService : IYouTubeCookiesAlertService
    {
        public bool ClearAlertCalled { get; private set; }
        public string AlertFilePath => "alert.flag";
        public bool AlertExists() => false;
        public void WriteAlert(string sourceId, string errorMessage) { }
        public void ClearAlert() => ClearAlertCalled = true;
    }

    private sealed class FakeReconciliationTrigger : IYouTubeReconciliationTrigger
    {
        public TaskCompletionSource Invoked { get; } = new();
        public bool WasInvoked { get; private set; }

        public Task<int> TriggerImmediateReconciliationAsync(CancellationToken cancellationToken = default)
        {
            WasInvoked = true;
            Invoked.TrySetResult();
            return Task.FromResult(0);
        }
    }

    private sealed class FakeHealthSnapshotProvider : IYouTubeHealthSnapshotProvider
    {
        public Task<YouTubeHealthSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new YouTubeHealthSnapshot(
                AuthAlertActive: false,
                CookiesValidation: new CookiesValidationResult(true, true, 1, null, true, "Valid"),
                ExcludedTvSourceIds: [],
                TotalTvSourceCount: 0,
                ActiveTvSourceCount: 0));
    }
}

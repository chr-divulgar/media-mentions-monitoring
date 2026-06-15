using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

public interface IYouTubeCookiesAlertService
{
    string AlertFilePath { get; }
    bool AlertExists();
    void WriteAlert(string sourceId, string errorMessage);
    void ClearAlert();
}

public sealed class YouTubeCookiesAlertService : IYouTubeCookiesAlertService
{
    private readonly ILogger<YouTubeCookiesAlertService> logger;

    public YouTubeCookiesAlertService(
        OperationsWorkerOptions options,
        ILogger<YouTubeCookiesAlertService> logger)
    {
        AlertFilePath = Path.IsPathRooted(options.YoutubeCookiesAlertFilePath)
            ? options.YoutubeCookiesAlertFilePath
            : Path.GetFullPath(options.YoutubeCookiesAlertFilePath);

        this.logger = logger;
    }

    public string AlertFilePath { get; }

    public bool AlertExists() => File.Exists(AlertFilePath);

    public void WriteAlert(string sourceId, string errorMessage)
    {
        try
        {
            var dir = Path.GetDirectoryName(AlertFilePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var content = $"""
                YouTube authentication required — cookies need to be renewed.

                Generated : {DateTimeOffset.UtcNow:O}
                Source    : {sourceId}
                Error     : {errorMessage}

                Action required:
                  1. Open your browser and log in to the YouTube monitoring account.
                  2. Install the "Get cookies.txt LOCALLY" Chrome/Edge extension.
                  3. Navigate to https://www.youtube.com and export cookies in Netscape format.
                  4. Replace the cookies file configured in worker-options.json (YtdlpResolver.youtubeCookiesFilePath).
                  5. DELETE THIS FILE ({Path.GetFileName(AlertFilePath)}) to signal that cookies are renewed.

                After deleting this file, the worker will automatically resume TV capture
                on the next scheduled reconciliation cycle (:00, :01, :30, or :59).
                """;

            File.WriteAllText(AlertFilePath, content);
            logger.LogError(
                "YouTube auth alert written to '{Path}'. TV capture suspended until cookies are renewed.",
                AlertFilePath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write YouTube cookies alert file to '{Path}'.", AlertFilePath);
        }
    }

    public void ClearAlert()
    {
        try
        {
            if (File.Exists(AlertFilePath))
            {
                File.Delete(AlertFilePath);
                logger.LogInformation("YouTube cookies alert cleared at '{Path}'.", AlertFilePath);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete YouTube cookies alert file at '{Path}'.", AlertFilePath);
        }
    }
}

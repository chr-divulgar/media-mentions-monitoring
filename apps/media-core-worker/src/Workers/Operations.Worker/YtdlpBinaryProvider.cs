using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MediaOpsCore.BuildingBlocks.Application;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

public interface IYtdlpBinaryProvider
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<(string Cmd, string[] Args)> GetCommandAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The worker carries its own yt-dlp: the official standalone binary for the OS it runs on, kept
/// in YtdlpBinDirectory and self-updated (`-U`) at startup — YouTube breaks old versions often.
/// Nothing has to be installed on the host (no Python, no PATH entry), so the same build runs on
/// this Windows box or a Linux container. A machine-wide yt-dlp is only a fallback for when the
/// download itself is impossible (offline first start).
/// </summary>
public sealed class YtdlpBinaryProvider : IYtdlpBinaryProvider
{
    private const string ReleaseBaseUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/";

    private readonly OperationsWorkerOptions options;
    private readonly IProcessRunner processRunner;
    private readonly ILogger<YtdlpBinaryProvider> logger;
    private readonly Func<string, string, CancellationToken, Task> download;

    private readonly SemaphoreSlim initLock = new(1, 1);
    private (string Cmd, string[] Args)? cachedCommand;

    public YtdlpBinaryProvider(
        OperationsWorkerOptions options,
        IProcessRunner processRunner,
        ILogger<YtdlpBinaryProvider> logger,
        Func<string, string, CancellationToken, Task>? download = null)
    {
        this.options = options;
        this.processRunner = processRunner;
        this.logger = logger;
        this.download = download ?? DownloadAsync;
    }

    // The official release asset for the current OS/architecture.
    public static string StandaloneAssetName()
    {
        if (OperatingSystem.IsWindows())
        {
            return "yt-dlp.exe";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "yt-dlp_macos";
        }

        return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "yt-dlp_linux_aarch64" : "yt-dlp_linux";
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await GetCommandAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<(string Cmd, string[] Args)> GetCommandAsync(CancellationToken cancellationToken = default)
    {
        if (cachedCommand.HasValue)
            return cachedCommand.Value;

        await initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (cachedCommand.HasValue)
                return cachedCommand.Value;

            cachedCommand = await ResolveAsync(cancellationToken).ConfigureAwait(false);
            return cachedCommand.Value;
        }
        finally
        {
            initLock.Release();
        }
    }

    private async Task<(string Cmd, string[] Args)> ResolveAsync(CancellationToken cancellationToken)
    {
        var binDir = Path.IsPathRooted(options.YtdlpBinDirectory)
            ? options.YtdlpBinDirectory
            : Path.GetFullPath(options.YtdlpBinDirectory);
        Directory.CreateDirectory(binDir);

        var asset = StandaloneAssetName();
        var local = Path.Combine(binDir, asset);

        // 1. Own copy: update it, then use it.
        if (File.Exists(local))
        {
            var update = await processRunner.RunAsync(new ProcessCommand(local, ["-U"]), cancellationToken).ConfigureAwait(false);
            if (!update.Succeeded)
            {
                logger.LogWarning("[YtdlpBinaryProvider] Self-update failed (keeping current version): {Error}", update.StandardError.Trim());
            }

            if (await WorksAsync(local, cancellationToken).ConfigureAwait(false))
            {
                logger.LogInformation("[YtdlpBinaryProvider] Using own yt-dlp at '{Path}'.", local);
                return (local, []);
            }
        }

        // 2. No working own copy: fetch the official binary for this OS (always the latest).
        try
        {
            var temp = local + ".download";
            await download(ReleaseBaseUrl + asset, temp, cancellationToken).ConfigureAwait(false);
            File.Move(temp, local, overwrite: true);
            TrySetExecutable(local);

            if (await WorksAsync(local, cancellationToken).ConfigureAwait(false))
            {
                logger.LogInformation("[YtdlpBinaryProvider] Downloaded yt-dlp ({Asset}) to '{Path}'.", asset, local);
                return (local, []);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "[YtdlpBinaryProvider] Could not download yt-dlp ({Asset}).", asset);
        }

        // 3. Last resort: a machine-wide install, if there happens to be one.
        if (await WorksAsync("yt-dlp", cancellationToken).ConfigureAwait(false))
        {
            logger.LogWarning("[YtdlpBinaryProvider] Falling back to the machine-wide yt-dlp on PATH.");
            return ("yt-dlp", []);
        }

        throw new InvalidOperationException(
            $"yt-dlp is not available: '{local}' could not be downloaded from {ReleaseBaseUrl}{asset} and none is on PATH.");
    }

    private async Task<bool> WorksAsync(string command, CancellationToken cancellationToken)
    {
        try
        {
            var check = await processRunner.RunAsync(new ProcessCommand(command, ["--version"]), cancellationToken).ConfigureAwait(false);
            return check.Succeeded;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Not installed: Process.Start throws instead of returning an exit code.
            return false;
        }
    }

    private static async Task DownloadAsync(string url, string destination, CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("User-Agent", "media-mentions-monitoring");
        await using var source = await http.GetStreamAsync(url, cancellationToken).ConfigureAwait(false);
        await using var target = File.Create(destination);
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
    }

    private static void TrySetExecutable(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            SetUnixExecutable(path);
        }
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static void SetUnixExecutable(string path)
    {
        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Non-fatal: the --version check right after reports whether it can run.
        }
    }
}

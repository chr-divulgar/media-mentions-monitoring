using System.Runtime.Versioning;
using MediaOpsCore.BuildingBlocks.Application;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

public interface IYtdlpBinaryProvider
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<(string Cmd, string[] Args)> GetCommandAsync(CancellationToken cancellationToken = default);
}

public sealed class YtdlpBinaryProvider : IYtdlpBinaryProvider
{
    private readonly OperationsWorkerOptions options;
    private readonly IProcessRunner processRunner;
    private readonly ILogger<YtdlpBinaryProvider> logger;

    private readonly SemaphoreSlim initLock = new(1, 1);
    private (string Cmd, string[] Args)? cachedCommand;

    public YtdlpBinaryProvider(
        OperationsWorkerOptions options,
        IProcessRunner processRunner,
        ILogger<YtdlpBinaryProvider> logger)
    {
        this.options = options;
        this.processRunner = processRunner;
        this.logger = logger;
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
        logger.LogInformation("[YtdlpBinaryProvider] Resolving yt-dlp binary...");

        // 1. Global PATH
        var globalCheck = await processRunner.RunAsync(
            new ProcessCommand("yt-dlp", ["--version"]), cancellationToken).ConfigureAwait(false);

        if (globalCheck.Succeeded)
        {
            logger.LogInformation("[YtdlpBinaryProvider] Found global yt-dlp {Version}. Running update check...",
                globalCheck.StandardOutput.Trim());

            await processRunner.RunAsync(
                new ProcessCommand("yt-dlp", ["-U"]), cancellationToken).ConfigureAwait(false);

            logger.LogInformation("[YtdlpBinaryProvider] yt-dlp resolved from PATH.");
            return ("yt-dlp", []);
        }

        var binDir = Path.IsPathRooted(options.YtdlpBinDirectory)
            ? options.YtdlpBinDirectory
            : Path.GetFullPath(options.YtdlpBinDirectory);

        Directory.CreateDirectory(binDir);

        // 2. Pre-existing binary in bin/
        var winBin = Path.Combine(binDir, "yt-dlp.exe");
        var unixBin = Path.Combine(binDir, "yt-dlp");

        foreach (var localPath in new[] { winBin, unixBin })
        {
            if (!File.Exists(localPath))
                continue;

            TrySetExecutable(localPath);

            var directCheck = await processRunner.RunAsync(
                new ProcessCommand(localPath, ["--version"]), cancellationToken).ConfigureAwait(false);

            if (directCheck.Succeeded)
            {
                logger.LogInformation("[YtdlpBinaryProvider] Using pre-existing binary at '{Path}'.", localPath);
                return (localPath, []);
            }

            // Try via python3
            var pythonCheck = await TryPythonCommandAsync(cancellationToken).ConfigureAwait(false);
            if (pythonCheck is not null)
            {
                var viaCheck = await processRunner.RunAsync(
                    new ProcessCommand(pythonCheck, [localPath, "--version"]), cancellationToken).ConfigureAwait(false);

                if (viaCheck.Succeeded)
                {
                    logger.LogInformation("[YtdlpBinaryProvider] Using '{Binary}' via {Python}.", localPath, pythonCheck);
                    return (pythonCheck, [localPath]);
                }
            }
        }

        // 3. Download python zipapp if python available
        var python = await TryPythonCommandAsync(cancellationToken).ConfigureAwait(false);
        if (python is not null)
        {
            var zipappPath = Path.Combine(binDir, "yt-dlp");
            logger.LogInformation("[YtdlpBinaryProvider] Downloading yt-dlp python zipapp to '{Path}'...", zipappPath);

            try
            {
                using var http = new HttpClient();
                http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120.0.0.0");
                var bytes = await http.GetByteArrayAsync(
                    "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp",
                    cancellationToken).ConfigureAwait(false);

                await File.WriteAllBytesAsync(zipappPath, bytes, cancellationToken).ConfigureAwait(false);

                TrySetExecutable(zipappPath);

                var verifyCheck = await processRunner.RunAsync(
                    new ProcessCommand(python, [zipappPath, "--version"]), cancellationToken).ConfigureAwait(false);

                if (verifyCheck.Succeeded)
                {
                    logger.LogInformation("[YtdlpBinaryProvider] yt-dlp zipapp downloaded and verified via {Python}.", python);
                    return (python, [zipappPath]);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[YtdlpBinaryProvider] Failed to download python zipapp. Trying standalone binary...");
            }
        }

        // 4. Download standalone Linux binary
        var linuxBin = Path.Combine(binDir, "yt-dlp_linux");
        logger.LogInformation("[YtdlpBinaryProvider] Downloading yt-dlp standalone Linux binary to '{Path}'...", linuxBin);

        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120.0.0.0");
            var bytes = await http.GetByteArrayAsync(
                "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux",
                cancellationToken).ConfigureAwait(false);

            await File.WriteAllBytesAsync(linuxBin, bytes, cancellationToken).ConfigureAwait(false);

            TrySetExecutable(linuxBin);

            var verifyCheck = await processRunner.RunAsync(
                new ProcessCommand(linuxBin, ["--version"]), cancellationToken).ConfigureAwait(false);

            if (verifyCheck.Succeeded)
            {
                logger.LogInformation("[YtdlpBinaryProvider] Standalone Linux binary downloaded and verified.");
                return (linuxBin, []);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[YtdlpBinaryProvider] Failed to download standalone Linux binary.");
        }

        throw new InvalidOperationException(
            "yt-dlp could not be found or downloaded. " +
            "Install it manually (https://github.com/yt-dlp/yt-dlp) or ensure python3 is available for automatic setup.");
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
        catch
        {
            // Non-fatal if chmod fails
        }
    }

    private async Task<string?> TryPythonCommandAsync(CancellationToken cancellationToken)
    {
        foreach (var cmd in new[] { "python3", "python" })
        {
            var check = await processRunner.RunAsync(
                new ProcessCommand(cmd, ["--version"]), cancellationToken).ConfigureAwait(false);
            if (check.Succeeded)
                return cmd;
        }

        return null;
    }
}

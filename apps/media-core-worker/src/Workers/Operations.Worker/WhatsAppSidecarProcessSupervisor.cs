using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Starts, health-checks, and restarts the Node.js/Baileys WhatsApp sidecar as a child process —
/// same "vendored runtime, no PATH fallback, fail fast if missing" contract as ffmpeg
/// (InProcessFfmpegAudioCapturePlugin.EnsureFfmpegInitialized/ResolveFfmpegRootPath), applied to
/// a whole Node.js process instead of native DLLs. Node.js itself is vendored under
/// native/win-x64/node/ (see the README there) and the sidecar script/node_modules under
/// sidecar-whatsapp/ — both copied to the publish output next to the worker's own executable.
/// </summary>
public sealed class WhatsAppSidecarProcessSupervisor : BackgroundService
{
    private static readonly TimeSpan RestartBackoff = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReadinessPollInterval = TimeSpan.FromSeconds(2);

    private readonly OperationsWorkerOptions options;
    private readonly HttpClient httpClient;
    private readonly ILogger<WhatsAppSidecarProcessSupervisor> logger;
    private Process? runningProcess;
    private bool started;

    public WhatsAppSidecarProcessSupervisor(
        OperationsWorkerOptions options,
        HttpClient httpClient,
        ILogger<WhatsAppSidecarProcessSupervisor> logger)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // Guards against a stray double-start; not expected in practice since host.StartAsync()
    // now starts every hosted service exactly once, early in Program.cs.
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (started)
        {
            return Task.CompletedTask;
        }

        started = true;
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string nodeExecutablePath;
        string sidecarScriptPath;
        try
        {
            nodeExecutablePath = ResolveNodeExecutablePath();
            sidecarScriptPath = ResolveSidecarScriptPath();
        }
        catch (InvalidOperationException exception)
        {
            logger.LogError(exception, "[WhatsAppSidecarProcessSupervisor] WhatsApp sidecar disabled: {Message}", exception.Message);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(nodeExecutablePath, sidecarScriptPath, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "[WhatsAppSidecarProcessSupervisor] Sidecar process crashed unexpectedly.");
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    "[WhatsAppSidecarProcessSupervisor] Restarting WhatsApp sidecar in {Seconds}s.",
                    RestartBackoff.TotalSeconds);
                await Task.Delay(RestartBackoff, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RunOnceAsync(string nodeExecutablePath, string sidecarScriptPath, CancellationToken stoppingToken)
    {
        var authStatePath = Path.GetFullPath(options.WhatsAppAuthStatePath);
        Directory.CreateDirectory(authStatePath);

        var startInfo = new ProcessStartInfo
        {
            FileName = nodeExecutablePath,
            Arguments = $"\"{sidecarScriptPath}\"",
            WorkingDirectory = Path.GetDirectoryName(sidecarScriptPath),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.EnvironmentVariables["WHATSAPP_SIDECAR_PORT"] = options.WhatsAppSidecarPort.ToString();
        startInfo.EnvironmentVariables["WHATSAPP_AUTH_STATE_PATH"] = authStatePath;

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                logger.LogInformation("[whatsapp-sidecar] {Line}", e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                logger.LogWarning("[whatsapp-sidecar] {Line}", e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        runningProcess = process;

        logger.LogInformation(
            "[WhatsAppSidecarProcessSupervisor] Started WhatsApp sidecar (pid {Pid}) on port {Port}.",
            process.Id,
            options.WhatsAppSidecarPort);

        await WaitForReadyAsync(stoppingToken).ConfigureAwait(false);

        using var registration = stoppingToken.Register(() => TryKill(process));
        await process.WaitForExitAsync(stoppingToken).ConfigureAwait(false);

        if (!stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning("[WhatsAppSidecarProcessSupervisor] WhatsApp sidecar exited with code {ExitCode}.", process.ExitCode);
        }

        runningProcess = null;
    }

    private async Task WaitForReadyAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(options.WhatsAppSidecarStartupTimeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var response = await httpClient
                    .GetAsync($"http://localhost:{options.WhatsAppSidecarPort}/status", cancellationToken)
                    .ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    logger.LogInformation("[WhatsAppSidecarProcessSupervisor] WhatsApp sidecar is ready.");
                    return;
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Not up yet — keep polling until the timeout below.
            }

            await Task.Delay(ReadinessPollInterval, cancellationToken).ConfigureAwait(false);
        }

        logger.LogWarning(
            "[WhatsAppSidecarProcessSupervisor] WhatsApp sidecar did not report ready within {Seconds}s; continuing anyway.",
            options.WhatsAppSidecarStartupTimeoutSeconds);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        TryKill(runningProcess);
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private void TryKill(Process? target)
    {
        try
        {
            if (target is not null && !target.HasExited)
            {
                target.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "[WhatsAppSidecarProcessSupervisor] Error stopping sidecar process.");
        }
    }

    private static string ResolveNodeExecutablePath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "node", "node.exe");
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Vendored Node.js runtime not found at '{path}'. See native/win-x64/node/README.md.");
        }

        return path;
    }

    private static string ResolveSidecarScriptPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "sidecar-whatsapp", "server.js");
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"WhatsApp sidecar script not found at '{path}'.");
        }

        return path;
    }
}

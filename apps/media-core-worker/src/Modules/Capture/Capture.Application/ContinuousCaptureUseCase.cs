using System.Text.Json;
using MediaOpsCore.BuildingBlocks.Application;
using MediaOpsCore.BuildingBlocks.Domain;

namespace MediaOpsCore.Modules.Capture.Application;

public sealed class ContinuousCaptureUseCase : IContinuousCaptureUseCase
{
    private readonly ICaptureSourceProvider captureSourceProvider;
    private readonly IProcessRunner processRunner;
    private readonly IMonitoringArtifactRepository monitoringArtifactRepository;
    private readonly ContinuousCaptureOptions options;

    public ContinuousCaptureUseCase(
        ICaptureSourceProvider captureSourceProvider,
        IProcessRunner processRunner,
        IMonitoringArtifactRepository monitoringArtifactRepository,
        ContinuousCaptureOptions options)
    {
        this.captureSourceProvider = captureSourceProvider;
        this.processRunner = processRunner;
        this.monitoringArtifactRepository = monitoringArtifactRepository;
        this.options = options;
    }

    public async Task<ContinuousCaptureResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var sources = await captureSourceProvider.ListActiveSourcesAsync(cancellationToken).ConfigureAwait(false);

        var attempts = 0;
        var succeeded = 0;
        var failed = 0;
        DateTimeOffset? lastCapturedAtUtc = null;

        foreach (var source in sources)
        {
            attempts++;
            var capturedAtUtc = DateTimeOffset.UtcNow;

            try
            {
                var command = BuildCommand(source);
                var execution = await processRunner.RunAsync(command, cancellationToken).ConfigureAwait(false);

                var artifact = BuildArtifact(source, capturedAtUtc, execution);
                await monitoringArtifactRepository.UpsertAsync(artifact, cancellationToken).ConfigureAwait(false);

                if (execution.Succeeded)
                {
                    succeeded++;
                    lastCapturedAtUtc = capturedAtUtc;
                }
                else
                {
                    failed++;
                }
            }
            catch
            {
                failed++;
                throw;
            }
        }

        return new ContinuousCaptureResult(attempts, succeeded, failed, lastCapturedAtUtc);
    }

    private ProcessCommand BuildCommand(MediaOpsCore.Modules.Capture.Domain.CaptureSource source)
    {
        if (string.IsNullOrWhiteSpace(options.ToolExecutable))
        {
            throw new InvalidOperationException("ToolExecutable cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(options.ToolArgumentsTemplate))
        {
            throw new InvalidOperationException("ToolArgumentsTemplate cannot be empty.");
        }

        var expanded = options.ToolArgumentsTemplate.Replace("{url}", source.StreamUrl, StringComparison.Ordinal);
        var arguments = expanded
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new ProcessCommand(options.ToolExecutable, arguments, timeout: options.CommandTimeout);
    }

    private static MonitoringArtifact BuildArtifact(
        MediaOpsCore.Modules.Capture.Domain.CaptureSource source,
        DateTimeOffset capturedAtUtc,
        ProcessExecutionResult execution)
    {
        var payload = JsonSerializer.Serialize(new
        {
            source.Platform,
            source.Media,
            source.StreamUrl,
            execution.ExitCode,
            execution.StandardOutput,
            execution.StandardError,
            execution.TimedOut
        });

        return new MonitoringArtifact(
            id: $"capture-{source.SourceId}-{capturedAtUtc:yyyyMMddHHmmssfff}",
            tenantId: source.TenantId,
            source: source.SourceId,
            kind: "capture",
            payloadJson: payload,
            capturedAtUtc: capturedAtUtc);
    }
}
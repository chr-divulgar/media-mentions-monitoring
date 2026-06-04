using System.Text.Json;
using MediaOpsCore.BuildingBlocks.Application;
using MediaOpsCore.BuildingBlocks.Domain;

namespace MediaOpsCore.Modules.Capture.Application;

public sealed class ContinuousCaptureUseCase : IContinuousCaptureUseCase
{
    private readonly ICaptureSourceProvider captureSourceProvider;
    private readonly IIngestionPluginResolver pluginResolver;
    private readonly IProcessRunner processRunner;
    private readonly IMonitoringArtifactRepository monitoringArtifactRepository;

    public ContinuousCaptureUseCase(
        ICaptureSourceProvider captureSourceProvider,
        IIngestionPluginResolver pluginResolver,
        IProcessRunner processRunner,
        IMonitoringArtifactRepository monitoringArtifactRepository)
    {
        this.captureSourceProvider = captureSourceProvider;
        this.pluginResolver = pluginResolver;
        this.processRunner = processRunner;
        this.monitoringArtifactRepository = monitoringArtifactRepository;
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
                var command = await BuildCommandAsync(source, cancellationToken).ConfigureAwait(false);
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
            catch (InvalidOperationException exception)
                when (exception.Message.StartsWith("No plugin profile configured", StringComparison.Ordinal))
            {
                // Source is ignored when no plugin profile is configured for its media/platform.
                continue;
            }
            catch
            {
                failed++;
                throw;
            }
        }

        return new ContinuousCaptureResult(attempts, succeeded, failed, lastCapturedAtUtc);
    }

    private async Task<ProcessCommand> BuildCommandAsync(
        MediaOpsCore.Modules.Capture.Domain.CaptureSource source,
        CancellationToken cancellationToken)
    {
        var plan = await pluginResolver
            .ResolveAsync(source, IngestionMode.Continuous, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(plan.ToolExecutable))
        {
            throw new InvalidOperationException("ToolExecutable cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(plan.ToolArgumentsTemplate))
        {
            throw new InvalidOperationException("ToolArgumentsTemplate cannot be empty.");
        }

        var expanded = plan.ToolArgumentsTemplate.Replace("{url}", source.StreamUrl, StringComparison.Ordinal);
        var arguments = expanded
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new ProcessCommand(plan.ToolExecutable, arguments, timeout: plan.CommandTimeout);
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
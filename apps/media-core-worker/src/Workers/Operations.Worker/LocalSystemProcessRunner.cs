using System.Diagnostics;
using MediaOpsCore.BuildingBlocks.Application;

namespace MediaOpsCore.Workers.Operations;

public sealed class LocalSystemProcessRunner : IProcessRunner
{
    public async Task<ProcessExecutionResult> RunAsync(ProcessCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command.FileName,
                WorkingDirectory = command.WorkingDirectory ?? Environment.CurrentDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        foreach (var argument in command.Arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException($"Unable to start process '{command.FileName}'.");
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var waitForExitTask = process.WaitForExitAsync(cancellationToken);
        var timedOut = false;

        var timeout = command.Timeout;
        if (timeout is null)
        {
            await waitForExitTask.ConfigureAwait(false);
        }
        else
        {
            var timeoutTask = Task.Delay(timeout.Value, cancellationToken);
            var completedTask = await Task.WhenAny(waitForExitTask, timeoutTask).ConfigureAwait(false);

            if (completedTask != waitForExitTask)
            {
                timedOut = true;

                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        return new ProcessExecutionResult(
            ExitCode: process.ExitCode,
            StandardOutput: await standardOutputTask.ConfigureAwait(false),
            StandardError: await standardErrorTask.ConfigureAwait(false),
            TimedOut: timedOut);
    }
}
using System.Diagnostics;
using MediaOpsCore.BuildingBlocks.Application;

namespace MediaOpsCore.BuildingBlocks.Infrastructure;

public sealed class SystemProcessRunner : IProcessRunner
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

        if (command.EnvironmentVariables is not null)
        {
            foreach (var environmentVariable in command.EnvironmentVariables)
            {
                process.StartInfo.EnvironmentVariables[environmentVariable.Key] = environmentVariable.Value;
            }
        }

        if (!process.Start())
        {
            throw new InvalidOperationException($"Unable to start process '{command.FileName}'.");
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        var waitForExitTask = process.WaitForExitAsync(cancellationToken);
        var timedOut = false;

        if (command.Timeout is null)
        {
            await waitForExitTask.ConfigureAwait(false);
        }
        else
        {
            var timeoutTask = Task.Delay(command.Timeout.Value, cancellationToken);
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

        var standardOutput = await standardOutputTask.ConfigureAwait(false);
        var standardError = await standardErrorTask.ConfigureAwait(false);

        return new ProcessExecutionResult(process.ExitCode, standardOutput, standardError, timedOut);
    }
}
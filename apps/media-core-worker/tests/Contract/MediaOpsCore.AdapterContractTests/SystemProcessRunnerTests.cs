using MediaOpsCore.BuildingBlocks.Application;
using MediaOpsCore.BuildingBlocks.Infrastructure;
using Xunit;

namespace MediaOpsCore.AdapterContractTests;

public sealed class SystemProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_should_capture_standard_output()
    {
        var runner = new SystemProcessRunner();

        var result = await runner.RunAsync(
            new ProcessCommand("cmd.exe", new[] { "/c", "echo hello" }));

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Succeeded);
        Assert.Contains("hello", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task RunAsync_should_capture_exit_code_for_failed_commands()
    {
        var runner = new SystemProcessRunner();

        var result = await runner.RunAsync(
            new ProcessCommand("cmd.exe", new[] { "/c", "exit /b 7" }));

        Assert.Equal(7, result.ExitCode);
        Assert.False(result.Succeeded);
        Assert.False(result.TimedOut);
    }
}
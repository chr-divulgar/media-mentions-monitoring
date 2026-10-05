using MediaOpsCore.BuildingBlocks.Application;
using MediaOpsCore.Workers.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class YtdlpBinaryProviderTests
{
    private sealed class FakeRunner : IProcessRunner
    {
        public List<ProcessCommand> Commands { get; } = [];
        public Func<ProcessCommand, bool> Succeeds { get; set; } = _ => true;

        public Task<ProcessExecutionResult> RunAsync(ProcessCommand command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            return Task.FromResult(Succeeds(command)
                ? new ProcessExecutionResult(0, "2026.10.01", "", TimedOut: false)
                : new ProcessExecutionResult(1, "", "not found", TimedOut: false));
        }
    }

    private static string NewBinDir() => Path.Combine(Path.GetTempPath(), $"ytdlp-bin-{Guid.NewGuid():N}");

    private static YtdlpBinaryProvider Build(string binDir, FakeRunner runner, Func<string, string, CancellationToken, Task> download) =>
        new(new OperationsWorkerOptions { YtdlpBinDirectory = binDir }, runner, NullLogger<YtdlpBinaryProvider>.Instance, download);

    [Fact]
    public async Task GetCommandAsync_downloads_the_official_binary_for_this_os_into_its_own_bin_folder()
    {
        var binDir = NewBinDir();
        try
        {
            var downloaded = new List<string>();
            var runner = new FakeRunner();
            var provider = Build(binDir, runner, async (url, destination, ct) =>
            {
                downloaded.Add(url);
                await File.WriteAllTextAsync(destination, "binary", ct);
            });

            var (cmd, args) = await provider.GetCommandAsync();

            var expected = Path.Combine(binDir, YtdlpBinaryProvider.StandaloneAssetName());
            Assert.Equal(expected, cmd);
            Assert.Empty(args);
            Assert.Equal($"https://github.com/yt-dlp/yt-dlp/releases/latest/download/{YtdlpBinaryProvider.StandaloneAssetName()}", Assert.Single(downloaded));
            // Never asks the machine-wide install first.
            Assert.DoesNotContain(runner.Commands, c => c.FileName == "yt-dlp");
        }
        finally
        {
            if (Directory.Exists(binDir)) Directory.Delete(binDir, recursive: true);
        }
    }

    [Fact]
    public async Task GetCommandAsync_updates_an_existing_own_binary_instead_of_downloading_it_again()
    {
        var binDir = NewBinDir();
        try
        {
            Directory.CreateDirectory(binDir);
            var local = Path.Combine(binDir, YtdlpBinaryProvider.StandaloneAssetName());
            await File.WriteAllTextAsync(local, "binary");
            var runner = new FakeRunner();
            var downloads = 0;
            var provider = Build(binDir, runner, (_, _, _) => { downloads++; return Task.CompletedTask; });

            var (cmd, _) = await provider.GetCommandAsync();

            Assert.Equal(local, cmd);
            Assert.Equal(0, downloads);
            Assert.Contains(runner.Commands, c => c.FileName == local && c.Arguments.SequenceEqual(["-U"]));
        }
        finally
        {
            Directory.Delete(binDir, recursive: true);
        }
    }

    [Fact]
    public async Task GetCommandAsync_falls_back_to_a_machine_install_only_when_the_download_fails()
    {
        var binDir = NewBinDir();
        try
        {
            var runner = new FakeRunner();
            var provider = Build(binDir, runner, (_, _, _) => throw new HttpRequestException("offline"));

            var (cmd, _) = await provider.GetCommandAsync();

            Assert.Equal("yt-dlp", cmd);
        }
        finally
        {
            if (Directory.Exists(binDir)) Directory.Delete(binDir, recursive: true);
        }
    }

    [Fact]
    public async Task GetCommandAsync_throws_when_nothing_is_available()
    {
        var binDir = NewBinDir();
        try
        {
            var runner = new FakeRunner { Succeeds = _ => false };
            var provider = Build(binDir, runner, (_, _, _) => throw new HttpRequestException("offline"));

            await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetCommandAsync());
        }
        finally
        {
            if (Directory.Exists(binDir)) Directory.Delete(binDir, recursive: true);
        }
    }
}

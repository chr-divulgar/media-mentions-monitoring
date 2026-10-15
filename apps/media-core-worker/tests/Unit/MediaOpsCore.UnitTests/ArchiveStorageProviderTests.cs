using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Workers.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaOpsCore.UnitTests;

// Each medium uploads with its own account (radio, television, and the press notes); the single account used before is
// still accepted as the fallback for any medium that has none of its own.
public sealed class ArchiveStorageProviderTests
{
    private static FtpArchiveCredentials Account(string name) => new($"ftp://archive.example.com:21/{name}", name, "secret");

    private static ArchiveStorageProvider Provider(OperationsWorkerOptions options) => new(options, NullLoggerFactory.Instance);

    [Fact]
    public void For_gives_each_medium_its_own_account_and_the_old_single_one_to_the_rest()
    {
        var options = new OperationsWorkerOptions { ArchiveEnabled = true, ArchiveFtp = Account("legacy") };
        options.ArchiveFtpByMedium[ArchiveMedia.Radio] = Account("radio");
        options.ArchiveFtpByMedium[ArchiveMedia.Press] = Account("press");
        var provider = Provider(options);

        Assert.NotNull(provider.For(ArchiveMedia.Radio));
        Assert.NotNull(provider.For(ArchiveMedia.Television)); // falls back to the legacy account
        Assert.NotNull(provider.For(ArchiveMedia.Press));
        Assert.NotSame(provider.For(ArchiveMedia.Radio), provider.For(ArchiveMedia.Press));
        Assert.Equal("radio", options.CredentialsFor(ArchiveMedia.Radio)!.User);
        Assert.Equal("legacy", options.CredentialsFor(ArchiveMedia.Television)!.User);
    }

    [Fact]
    public void For_gives_nothing_to_a_medium_with_no_account_at_all()
    {
        var options = new OperationsWorkerOptions { ArchiveEnabled = true };
        options.ArchiveFtpByMedium[ArchiveMedia.Television] = Account("television");
        var provider = Provider(options);

        Assert.NotNull(provider.For(ArchiveMedia.Television));
        Assert.Null(provider.For(ArchiveMedia.Radio));
        Assert.Null(provider.For(ArchiveMedia.Press));
        Assert.Null(provider.For("internet")); // not an archive medium
    }

    [Fact]
    public void For_gives_nothing_while_the_archive_is_switched_off()
    {
        var options = new OperationsWorkerOptions { ArchiveEnabled = false, ArchiveFtp = Account("legacy") };

        Assert.Null(Provider(options).For(ArchiveMedia.Radio));
    }

    [Fact]
    public void The_archive_is_active_as_soon_as_any_medium_has_an_account()
    {
        Assert.False(new OperationsWorkerOptions { ArchiveEnabled = true }.IsArchiveActive);
        Assert.True(new OperationsWorkerOptions { ArchiveEnabled = true, ArchiveFtp = Account("legacy") }.IsArchiveActive);

        var perMedium = new OperationsWorkerOptions { ArchiveEnabled = true };
        perMedium.ArchiveFtpByMedium[ArchiveMedia.Radio] = Account("radio");
        Assert.True(perMedium.IsArchiveActive);
    }

    [Theory]
    [InlineData("radio/2026/10/10/wradio/a.opus", ArchiveMedia.Radio)]
    [InlineData("television/2026/10/10/rcn_tv/a.mp4", ArchiveMedia.Television)]
    [InlineData("notes/internet/2026/10/10/a.pdf", ArchiveMedia.Press)]
    [InlineData("other/2026/10/10/x/a.bin", null)]
    public void MediumOf_reads_the_medium_from_the_first_folder_of_a_path(string path, string? expected) =>
        Assert.Equal(expected, ArchiveMedia.MediumOf(path));
}

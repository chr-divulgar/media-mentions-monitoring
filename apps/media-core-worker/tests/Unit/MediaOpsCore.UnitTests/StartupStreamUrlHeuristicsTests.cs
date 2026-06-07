using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class StartupStreamUrlHeuristicsTests
{
    [Fact]
    public void IsExpiringSoon_should_return_true_for_zt_token_close_to_expiry()
    {
        var url = "https://stream-177.zeno.fm/t8sz23cfhfhvv?zt=eyJhbGciOiJIUzI1NiJ9.eyJleHAiOjE4OTM0NTYwMDB9.signature";
        var now = DateTimeOffset.FromUnixTimeSeconds(1893455900);

        var result = StartupStreamUrlHeuristics.IsExpiringSoon(url, TimeSpan.FromMinutes(3), now, out var expiresAt);

        Assert.True(result);
        Assert.NotNull(expiresAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1893456000), expiresAt!.Value);
    }

    [Fact]
    public void IsExpiringSoon_should_return_false_for_non_tokenized_url()
    {
        var url = "https://example.com/live.m3u8";
        var now = DateTimeOffset.UtcNow;

        var result = StartupStreamUrlHeuristics.IsExpiringSoon(url, TimeSpan.FromMinutes(3), now, out var expiresAt);

        Assert.False(result);
        Assert.Null(expiresAt);
    }

    [Fact]
    public void IsLikelyEphemeral_should_detect_tokenized_query()
    {
        var url = "https://stream-177.zeno.fm/t8sz23cfhfhvv?zt=abc";

        Assert.True(StartupStreamUrlHeuristics.IsLikelyEphemeral(url));
    }
}

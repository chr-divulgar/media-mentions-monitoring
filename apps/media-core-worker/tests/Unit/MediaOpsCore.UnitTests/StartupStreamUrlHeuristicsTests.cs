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

    [Theory]
    [InlineData("https://prisa-co.mc.tritondigital.com/LA_LUCIERNAGA_CARACOL_RADIO_409_P/media/mcv/caracol/multimedia/20260606/4982842_023447_audio_128.mp3")]
    [InlineData("https://cdn.example.com/radio/20260607/session_audio_128.mp3")]
    [InlineData("https://cdn.example.com/audio/202606/broadcast.aac")]
    [InlineData("https://archive.tritondigital.com/station/media/files/9123456_084500_audio_64.mp3")]
    public void IsLikelyVodRecording_should_reject_dated_cdn_paths(string url)
    {
        Assert.True(StartupStreamUrlHeuristics.IsLikelyVodRecording(url));
    }

    [Theory]
    [InlineData("https://playerservices.streamtheworld.com/api/livestream-redirect/CARACOL_RADIOAAC.aac")]
    [InlineData("https://19253.live.streamtheworld.com/CARACOL_RADIOAAC.aac")]
    [InlineData("https://mdstrm.com/audio/632c9b23d1dcd7027f32f7fe/live.m3u8")]
    [InlineData("https://geostreaming.rtvc.gov.co/Radio_Radionacional/Radionacional.stream/playlist.m3u8")]
    public void IsLikelyVodRecording_should_accept_live_stream_urls(string url)
    {
        Assert.False(StartupStreamUrlHeuristics.IsLikelyVodRecording(url));
    }
}

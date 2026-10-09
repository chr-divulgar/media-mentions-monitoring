using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class YouTubeLiveProbeTests
{
    // Shapes taken from the real /live pages checked on 2026-10-09.
    private const string LivePage =
        "<link rel=\"canonical\" href=\"https://www.youtube.com/watch?v=bfwPG2pmPdM\"><script>{\"isLive\":true,\"isLiveNow\":true}</script>";

    private const string LiveWithoutIsLiveNow =
        "<link rel=\"canonical\" href=\"https://www.youtube.com/watch?v=xwRmDJPw-J4\"><script>{\"isLive\":true}</script>";

    private const string NotLivePage =
        "<link rel=\"canonical\" href=\"https://www.youtube.com/channel/UCnQiWLU0KwnQR1sp1U3H-QQ\"><script>{\"videoId\":\"yIH3N9SW910\"}</script>";

    // RTVC on 2026-10-09: /live redirected to a scheduled broadcast that had not started.
    private const string ScheduledPage =
        "<link rel=\"canonical\" href=\"https://www.youtube.com/watch?v=CfhT_IYx-kU\"><script>{\"playabilityStatus\":{\"status\":\"LIVE_STREAM_OFFLINE\",\"reason\":\"This live event will begin in 56 minutes.\"},\"isLive\":true,\"isUpcoming\":true}</script>";

    [Theory]
    [InlineData(ScheduledPage, YouTubeLiveStatus.NotLive)]
    [InlineData(LivePage, YouTubeLiveStatus.Live)]
    [InlineData(LiveWithoutIsLiveNow, YouTubeLiveStatus.Live)]
    [InlineData(NotLivePage, YouTubeLiveStatus.NotLive)]
    [InlineData("<html>consent</html>", YouTubeLiveStatus.Unknown)]
    [InlineData("", YouTubeLiveStatus.Unknown)]
    [InlineData(null, YouTubeLiveStatus.Unknown)]
    public void Classify_reads_the_canonical_link_and_isLive(string? html, YouTubeLiveStatus expected) =>
        Assert.Equal(expected, YouTubeLiveProbe.Classify(html));

    [Fact]
    public async Task A_failing_request_is_unknown_not_not_live()
    {
        var probe = new YouTubeLiveProbe(new HttpClient(new ThrowingHandler()));

        Assert.Equal(YouTubeLiveStatus.Unknown, await probe.CheckAsync("https://www.youtube.com/@x/live", CancellationToken.None));
    }

    [Fact]
    public async Task A_non_ascii_handle_is_requested_percent_encoded()
    {
        var handler = new CapturingHandler(NotLivePage);
        var probe = new YouTubeLiveProbe(new HttpClient(handler));

        var status = await probe.CheckAsync("https://www.youtube.com/@PrimeraPáginaColombia/live", CancellationToken.None);

        Assert.Equal(YouTubeLiveStatus.NotLive, status);
        Assert.Equal("https://www.youtube.com/@PrimeraP%C3%A1ginaColombia/live", handler.RequestedUrl);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("no route");
    }

    private sealed class CapturingHandler(string body) : HttpMessageHandler
    {
        public string? RequestedUrl { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUrl = request.RequestUri!.AbsoluteUri;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}

using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class GoogleNewsLinkResolverTests
{
    // Shape of a real news.google.com/_/DotsSplashUi/data/batchexecute response: a leading
    // `)]}'` anti-hijacking prefix, a blank line, then one JSON array whose first element is the
    // "wrb.fr" response tuple — its 3rd field is itself a JSON-encoded ["garturlres", url, ...]
    // array — followed by unrelated trailing metadata tuples ("di", "af.httprm").
    private static string Fixture(string url)
    {
        var escapedPayload = $"[\"garturlres\",\"{url}\",1]".Replace("\"", "\\\"");
        return $")]}}'\n\n[[\"wrb.fr\",\"Fbv4je\",\"{escapedPayload}\",null,null,null,\"generic\"],[\"di\",89],[\"af.httprm\",87,\"-123\",14]]";
    }

    [Fact]
    public void ParseBatchExecuteUrl_extracts_the_real_url_from_the_wrb_fr_payload()
    {
        var text = Fixture("https://www.valoraanalitik.com/acciones-de-ecopetrol-lideran");

        var url = GoogleNewsLinkResolver.ParseBatchExecuteUrl(text);

        Assert.Equal("https://www.valoraanalitik.com/acciones-de-ecopetrol-lideran", url);
    }

    [Fact]
    public void ParseBatchExecuteUrl_returns_null_when_the_response_has_no_blank_line_separator()
    {
        Assert.Null(GoogleNewsLinkResolver.ParseBatchExecuteUrl(")]}'\n[[\"wrb.fr\"]]"));
    }

    [Fact]
    public void ParseBatchExecuteUrl_returns_null_for_malformed_json()
    {
        Assert.Null(GoogleNewsLinkResolver.ParseBatchExecuteUrl(")]}'\n\nnot json at all"));
    }

    [Fact]
    public void ParseBatchExecuteUrl_returns_null_when_the_inner_payload_has_no_second_element()
    {
        var text = ")]}'\n\n[[\"wrb.fr\",\"Fbv4je\",\"[\\\"garturlres\\\"]\",null,null,null,\"generic\"]]";

        Assert.Null(GoogleNewsLinkResolver.ParseBatchExecuteUrl(text));
    }
}

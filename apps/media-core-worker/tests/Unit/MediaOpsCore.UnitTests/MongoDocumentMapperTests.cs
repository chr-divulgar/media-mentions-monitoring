using MediaOpsCore.Modules.Alerting.Domain;
using MediaOpsCore.Workers.Operations;
using MongoDB.Bson;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class MongoDocumentMapperTests
{
    private static BsonDocument Doc(string json) => BsonDocument.Parse(json);

    [Fact]
    public void TryMapCaptureSource_should_map_a_full_document()
    {
        var source = MongoDocumentMapper.TryMapCaptureSource(Doc("""
        {
          "sourceId": "ecos-del-combeima", "name": "EcosDelCombeima", "media": "radio",
          "streamUrl": "https://stream.example.com/live", "primaryUrl": "https://example.com", "country": "colombia",
          "fallbackStreamUrls": ["https://a.example.com", "https://b.example.com"], "isExcluded": true
        }
        """));

        Assert.NotNull(source);
        Assert.Equal("ecos-del-combeima", source.SourceId);
        Assert.Equal("EcosDelCombeima", source.Platform);
        Assert.Equal("radio", source.Media);
        Assert.Equal("https://stream.example.com/live", source.StreamUrl);
        Assert.Equal("https://example.com", source.PrimaryUrl);
        Assert.Equal("colombia", source.Country);
        Assert.Equal(["https://a.example.com", "https://b.example.com"], source.FallbackStreamUrls);
        Assert.True(source.IsExcluded);
    }

    [Fact]
    public void TryMapCaptureSource_defaults_isExcluded_to_false_when_absent()
    {
        var source = MongoDocumentMapper.TryMapCaptureSource(Doc("""{ "sourceId": "radio-a", "name": "RadioA", "media": "radio", "streamUrl": "https://s/a" }"""));

        Assert.NotNull(source);
        Assert.False(source.IsExcluded);
    }

    [Theory]
    [InlineData("tv", "television")]
    [InlineData("TV", "television")]
    [InlineData("radio", "radio")]
    [InlineData("internet", "internet")]
    public void TryMapCaptureSource_normalizes_tv_media_to_television(string storedMedia, string expectedMedia)
    {
        var source = MongoDocumentMapper.TryMapCaptureSource(Doc($$"""{ "sourceId": "s1", "name": "S1", "media": "{{storedMedia}}", "streamUrl": "https://s/1" }"""));

        Assert.NotNull(source);
        Assert.Equal(expectedMedia, source.Media);
    }

    [Fact]
    public void TryMapCaptureSource_skips_a_whatsapp_only_entry_without_sourceId()
    {
        Assert.Null(MongoDocumentMapper.TryMapCaptureSource(Doc("""{ "name": "WhatsAppOnly", "media": "radio", "streamUrl": "https://s/a" }""")));
    }

    [Fact]
    public void TryMapCaptureSource_skips_a_document_with_no_usable_url()
    {
        Assert.Null(MongoDocumentMapper.TryMapCaptureSource(Doc("""{ "sourceId": "s1", "name": "S1", "media": "radio" }""")));
        // A plain website is not a playable stream.
        Assert.Null(MongoDocumentMapper.TryMapCaptureSource(Doc("""{ "sourceId": "s1", "name": "S1", "media": "radio", "primaryUrl": "https://example.com" }""")));
    }

    [Fact]
    public void TryMapCaptureSource_bootstraps_the_stream_from_a_youtube_primaryUrl()
    {
        var source = MongoDocumentMapper.TryMapCaptureSource(Doc("""
        { "sourceId": "yt", "name": "YT", "media": "tv", "primaryUrl": "https://www.youtube.com/@canal/live" }
        """));

        Assert.NotNull(source);
        Assert.Equal("https://www.youtube.com/@canal/live", source.StreamUrl);
    }

    [Fact]
    public void TryMapCaptureSource_prefers_the_first_fallback_over_a_youtube_primaryUrl_when_bootstrapping()
    {
        var source = MongoDocumentMapper.TryMapCaptureSource(Doc("""
        { "sourceId": "yt", "name": "YT", "media": "tv", "primaryUrl": "https://www.youtube.com/@canal/live", "fallbackStreamUrls": ["https://known/good.m3u8"] }
        """));

        Assert.NotNull(source);
        Assert.Equal("https://known/good.m3u8", source.StreamUrl);
    }

    [Fact]
    public void TryMapCaptureSource_ignores_fields_that_are_not_the_expected_type()
    {
        var source = MongoDocumentMapper.TryMapCaptureSource(Doc("""
        { "sourceId": "s1", "name": "S1", "media": "radio", "streamUrl": "https://s/1", "isExcluded": "yes", "fallbackStreamUrls": "nope" }
        """));

        Assert.NotNull(source);
        Assert.False(source.IsExcluded);
        Assert.Empty(source.FallbackStreamUrls);
    }

    [Fact]
    public void TryMapClient_reads_keywords_with_their_ad_contexts_and_levels()
    {
        var client = MongoDocumentMapper.TryMapClient(Doc("""
        {
          "name": "Ecopetrol",
          "words": [
            { "value": "derrame", "level": "N4", "adds": [ { "before": "simulacro de", "after": "" } ] },
            { "value": "petroleo", "adds": [] },
            { "value": "" }
          ],
          "alerts": { "radio": ["+573001112233"], "tv": [] },
          "alertEmails": { "internet": ["a@x.co"] }
        }
        """));

        Assert.NotNull(client);
        Assert.Equal("Ecopetrol", client.Name);
        Assert.Equal(["derrame", "petroleo"], client.Keywords.Select(keyword => keyword.Value));
        Assert.Equal(AlertLevel.Informative, client.Keywords[1].Level);
        Assert.Equal(new KeywordAdContext("simulacro de", string.Empty), Assert.Single(client.Keywords[0].Adds));
        Assert.Equal(["+573001112233"], client.AlertRecipientsByMedia["radio"]);
        Assert.Equal(["a@x.co"], client.AlertEmailsByMedia!["internet"]);
    }

    [Fact]
    public void TryMapClient_treats_an_unknown_level_as_informative_and_needs_a_name()
    {
        var client = MongoDocumentMapper.TryMapClient(Doc("""{ "name": "X", "words": [ { "value": "a", "level": "N9" } ] }"""));

        Assert.NotNull(client);
        Assert.Equal(AlertLevel.Informative, client.Keywords[0].Level);
        Assert.Null(MongoDocumentMapper.TryMapClient(Doc("""{ "words": [] }""")));
    }
}

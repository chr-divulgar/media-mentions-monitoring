using System.Text.Json;
using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class FirestoreDocumentMapperTests
{
    [Fact]
    public void TryMapCaptureSource_should_map_a_full_document()
    {
        var document = Parse("""
        {
          "fields": {
            "sourceId": {"stringValue": "ecos-del-combeima"},
            "name": {"stringValue": "EcosDelCombeima"},
            "media": {"stringValue": "radio"},
            "streamUrl": {"stringValue": "https://stream.example.com/live"},
            "primaryUrl": {"stringValue": "https://example.com"},
            "country": {"stringValue": "colombia"},
            "fallbackStreamUrls": {"arrayValue": {"values": [
              {"stringValue": "https://a.example.com"},
              {"stringValue": "https://b.example.com"}
            ]}},
            "isExcluded": {"booleanValue": true}
          }
        }
        """);

        var source = FirestoreDocumentMapper.TryMapCaptureSource(document);

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
    public void TryMapCaptureSource_should_default_isExcluded_to_false_when_absent()
    {
        var document = Parse("""
        {
          "fields": {
            "sourceId": {"stringValue": "radio-a"},
            "name": {"stringValue": "RadioA"},
            "media": {"stringValue": "radio"},
            "streamUrl": {"stringValue": "https://stream.example.com/a"}
          }
        }
        """);

        var source = FirestoreDocumentMapper.TryMapCaptureSource(document);

        Assert.NotNull(source);
        Assert.False(source.IsExcluded);
        Assert.Null(source.PrimaryUrl);
    }

    [Theory]
    [InlineData("tv", "television")]
    [InlineData("TV", "television")]
    [InlineData("television", "television")]
    [InlineData("radio", "radio")]
    public void TryMapCaptureSource_normalizes_tv_media_to_television(string storedMedia, string expectedMedia)
    {
        var document = Parse($$"""
        {
          "fields": {
            "sourceId": {"stringValue": "cvnoticias-tv"},
            "name": {"stringValue": "CVNoticiasTV"},
            "media": {"stringValue": "{{storedMedia}}"},
            "streamUrl": {"stringValue": "https://stream.example.com/live"}
          }
        }
        """);

        var source = FirestoreDocumentMapper.TryMapCaptureSource(document);

        Assert.NotNull(source);
        Assert.Equal(expectedMedia, source.Media);
    }

    [Fact]
    public void TryMapCaptureSource_should_return_null_when_sourceId_is_absent()
    {
        // A WhatsApp-only platform entry (no associated capture source) — must be skipped, not
        // mapped with a blank sourceId.
        var document = Parse("""
        {
          "fields": {
            "name": {"stringValue": "SomeWhatsAppSlot"},
            "media": {"stringValue": "radio"}
          }
        }
        """);

        Assert.Null(FirestoreDocumentMapper.TryMapCaptureSource(document));
    }

    [Fact]
    public void TryMapCaptureSource_should_return_null_when_streamUrl_is_absent()
    {
        var document = Parse("""
        {
          "fields": {
            "sourceId": {"stringValue": "radio-a"},
            "name": {"stringValue": "RadioA"},
            "media": {"stringValue": "radio"}
          }
        }
        """);

        Assert.Null(FirestoreDocumentMapper.TryMapCaptureSource(document));
    }

    [Fact]
    public void TryMapCaptureSource_bootstraps_streamUrl_from_a_youtube_primaryUrl_when_streamUrl_is_absent()
    {
        // A YouTube source has no real streamUrl until yt-dlp resolves one — the document must
        // still map so it can reach that point, instead of being skipped as WhatsApp-only.
        var document = Parse("""
        {
          "fields": {
            "sourceId": {"stringValue": "cable-noticias"},
            "name": {"stringValue": "CableNoticias"},
            "media": {"stringValue": "tv"},
            "primaryUrl": {"stringValue": "https://www.youtube.com/@cablenoticias/live"}
          }
        }
        """);

        var source = FirestoreDocumentMapper.TryMapCaptureSource(document);

        Assert.NotNull(source);
        Assert.Equal("https://www.youtube.com/@cablenoticias/live", source.StreamUrl);
        Assert.Equal("https://www.youtube.com/@cablenoticias/live", source.PrimaryUrl);
    }

    [Fact]
    public void TryMapCaptureSource_still_returns_null_when_streamUrl_is_absent_and_primaryUrl_is_not_youtube()
    {
        var document = Parse("""
        {
          "fields": {
            "sourceId": {"stringValue": "cable-noticias"},
            "name": {"stringValue": "CableNoticias"},
            "media": {"stringValue": "tv"},
            "primaryUrl": {"stringValue": "https://cablenoticias.example.com"}
          }
        }
        """);

        Assert.Null(FirestoreDocumentMapper.TryMapCaptureSource(document));
    }

    [Fact]
    public void TryMapCaptureSource_bootstraps_streamUrl_from_the_first_fallback_when_streamUrl_is_absent()
    {
        // Applies to radio just as much as television — a source with candidate URLs already
        // known (discovered automatically, or pasted manually) but nothing in the primary
        // "Stream URL" box must still be recognized, not skipped as WhatsApp-only.
        var document = Parse("""
        {
          "fields": {
            "sourceId": {"stringValue": "radio-a"},
            "name": {"stringValue": "RadioA"},
            "media": {"stringValue": "radio"},
            "fallbackStreamUrls": {"arrayValue": {"values": [
              {"stringValue": "https://stream.example.com/a"},
              {"stringValue": "https://stream.example.com/b"}
            ]}}
          }
        }
        """);

        var source = FirestoreDocumentMapper.TryMapCaptureSource(document);

        Assert.NotNull(source);
        Assert.Equal("https://stream.example.com/a", source.StreamUrl);
    }

    [Fact]
    public void TryMapCaptureSource_prefers_fallbackStreamUrls_over_a_youtube_primaryUrl_when_bootstrapping()
    {
        var document = Parse("""
        {
          "fields": {
            "sourceId": {"stringValue": "cable-noticias"},
            "name": {"stringValue": "CableNoticias"},
            "media": {"stringValue": "tv"},
            "primaryUrl": {"stringValue": "https://www.youtube.com/@cablenoticias/live"},
            "fallbackStreamUrls": {"arrayValue": {"values": [
              {"stringValue": "https://manifest.googlevideo.com/already-resolved.m3u8"}
            ]}}
          }
        }
        """);

        var source = FirestoreDocumentMapper.TryMapCaptureSource(document);

        Assert.NotNull(source);
        Assert.Equal("https://manifest.googlevideo.com/already-resolved.m3u8", source.StreamUrl);
    }

    [Fact]
    public void TryMapCaptureSource_should_return_null_when_fields_property_is_missing()
    {
        var document = Parse("""{ "name": "projects/p/databases/(default)/documents/platforms/empty" }""");

        Assert.Null(FirestoreDocumentMapper.TryMapCaptureSource(document));
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;
}

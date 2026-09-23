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
    public void TryMapCaptureSource_should_return_null_when_fields_property_is_missing()
    {
        var document = Parse("""{ "name": "projects/p/databases/(default)/documents/platforms/empty" }""");

        Assert.Null(FirestoreDocumentMapper.TryMapCaptureSource(document));
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;
}

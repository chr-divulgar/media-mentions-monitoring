using System.Text.Json;
using MediaOpsCore.Modules.Alerting.Domain;
using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class FirestoreClientDocumentMapperTests
{
    private static JsonElement Document(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void TryMapClient_reads_each_keywords_alert_level()
    {
        var client = FirestoreClientDocumentMapper.TryMapClient(Document("""
            {"fields":{
              "name":{"stringValue":"Ecopetrol"},
              "words":{"arrayValue":{"values":[
                {"mapValue":{"fields":{"value":{"stringValue":"petroleo"}}}},
                {"mapValue":{"fields":{"value":{"stringValue":"derrame"},"level":{"stringValue":"N4"}}}},
                {"mapValue":{"fields":{"value":{"stringValue":"tarifa"},"level":{"stringValue":"n2"}}}}
              ]}}
            }}
            """));

        Assert.NotNull(client);
        Assert.Equal(
            [AlertLevel.Informative, AlertLevel.Crisis, AlertLevel.FollowUp],
            client!.Keywords.Select(keyword => keyword.Level));
    }

    [Fact]
    public void TryMapClient_treats_an_unknown_level_as_informative()
    {
        var client = FirestoreClientDocumentMapper.TryMapClient(Document("""
            {"fields":{
              "name":{"stringValue":"Ecopetrol"},
              "words":{"arrayValue":{"values":[
                {"mapValue":{"fields":{"value":{"stringValue":"derrame"},"level":{"stringValue":"urgente"}}}}
              ]}}
            }}
            """));

        Assert.Equal(AlertLevel.Informative, Assert.Single(client!.Keywords).Level);
    }

    [Fact]
    public void TryMapClient_reads_the_alert_email_addresses_by_media()
    {
        var client = FirestoreClientDocumentMapper.TryMapClient(Document("""
            {"fields":{
              "name":{"stringValue":"Ecopetrol"},
              "alertEmails":{"mapValue":{"fields":{
                "tv":{"arrayValue":{"values":[{"stringValue":"ana@entidad.gov.co"}]}}
              }}}
            }}
            """));

        Assert.Equal(["ana@entidad.gov.co"], client!.AlertEmailsByMedia!["tv"]);
    }
}

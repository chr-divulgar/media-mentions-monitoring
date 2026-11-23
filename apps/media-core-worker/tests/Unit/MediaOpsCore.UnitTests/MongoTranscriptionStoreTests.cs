using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Workers.Operations;
using MongoDB.Bson;
using Xunit;

namespace MediaOpsCore.UnitTests;

// The shape of a transcription in the `transcription` collection. That collection already holds 3 million documents of
// the old system (TV, October), with the same field names for text, times, media, platform and file: new ones keep those
// names and add sourceId and status, and everything about indexes is limited to documents that have a sourceId.
public sealed class MongoTranscriptionStoreTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 19, 10, 0, 0, TimeSpan.FromHours(-5));

    private static TranscriptionEntry Entry(string text = "hola") =>
        new("radio-a", "Caracol", "radio", @"C:\MediaMonitoringData\radio\2026\11\19\radio-a\radio-a_2026-11-19_10-00-00.opus", Start, Start.AddSeconds(18), text, "recognized");

    [Fact]
    public void A_document_keeps_the_names_the_old_system_used_and_adds_source_and_status()
    {
        var createdAt = new DateTime(2026, 11, 19, 15, 1, 0, DateTimeKind.Utc);

        var document = MongoTranscriptionStore.ToDocument(Entry("hola mundo"), createdAt);

        Assert.Equal("hola mundo", document["text"].AsString);
        Assert.Equal("radio", document["media"].AsString);
        Assert.Equal("Caracol", document["platform"].AsString);
        Assert.Equal(@"C:\MediaMonitoringData\radio\2026\11\19\radio-a\radio-a_2026-11-19_10-00-00.opus", document["filePath"].AsString);
        Assert.Equal("radio-a", document["sourceId"].AsString);
        Assert.Equal("recognized", document["status"].AsString);
        Assert.Equal(createdAt, document["createdAt"].ToUniversalTime());
    }

    [Fact]
    public void The_times_are_dates_in_utc_so_they_sort_and_expire_like_the_old_ones()
    {
        var document = MongoTranscriptionStore.ToDocument(Entry(), DateTime.UtcNow);

        Assert.Equal(BsonType.DateTime, document["startTime"].BsonType);
        Assert.Equal(new DateTime(2026, 11, 19, 15, 0, 0, DateTimeKind.Utc), document["startTime"].ToUniversalTime());
        Assert.Equal(new DateTime(2026, 11, 19, 15, 0, 18, DateTimeKind.Utc), document["endTime"].ToUniversalTime());
    }

    [Fact]
    public void A_document_reads_back_as_the_same_moment()
    {
        var document = MongoTranscriptionStore.ToDocument(Entry("texto"), DateTime.UtcNow);

        var entry = MongoTranscriptionStore.ToEntry(document);

        Assert.Equal("texto", entry.Text);
        Assert.Equal("radio-a", entry.SourceId);
        Assert.Equal("recognized", entry.Status);
        Assert.Equal(Start.ToUniversalTime(), entry.StartTime.ToUniversalTime());
        Assert.Equal(Start.AddSeconds(18).ToUniversalTime(), entry.EndTime.ToUniversalTime());
    }

    [Fact]
    public void A_document_without_a_status_reads_as_recognized()
    {
        var document = MongoTranscriptionStore.ToDocument(Entry(), DateTime.UtcNow);
        document.Remove("status");

        Assert.Equal("recognized", MongoTranscriptionStore.ToEntry(document).Status);
    }

    [Fact]
    public void The_key_of_an_entry_is_its_hour_file_and_its_times()
    {
        var filter = MongoTranscriptionStore.KeyFilter(Entry()).ToBsonDocument();

        Assert.Equal(3, filter.ElementCount);
        Assert.True(filter.Contains("filePath") && filter.Contains("startTime") && filter.Contains("endTime"));
    }

    [Fact]
    public void The_indexes_only_cover_the_new_documents_and_the_old_ones_never_expire()
    {
        var indexes = MongoTranscriptionStore.IndexDefinitions(retentionDays: 365);

        Assert.Equal(["transcription_hour_unique", "transcription_expiry"], indexes.Select(i => i.Name));
        // The old 3 million documents have no sourceId: with this filter they are in neither index.
        Assert.All(indexes, i => Assert.Equal(new BsonDocument("sourceId", new BsonDocument("$exists", true)), i.PartialFilter));
        var unique = indexes[0];
        Assert.True(unique.Unique);
        Assert.Equal(["filePath", "startTime", "endTime"], unique.Keys);
        var expiry = indexes[1];
        Assert.Equal(["startTime"], expiry.Keys);
        Assert.Equal(TimeSpan.FromDays(365), expiry.ExpireAfter);
    }
}

using MediaOpsCore.Modules.Alerting.Infrastructure;
using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Workers.Operations;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace MediaOpsCore.UnitTests;

// Against a real MongoDB, in a scratch database that is dropped afterwards. It runs only when MEDIAOPS_MONGO_TEST_URI is
// set (for example mongodb://localhost:27017); without it the test passes without doing anything, like the rest of the
// suite, which needs no database.
[Trait("Category", "Integration")]
public sealed class MongoTranscriptionStoreIntegrationTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 19, 10, 0, 0, TimeSpan.FromHours(-5));
    private const string HourFile = @"C:\MediaMonitoringData\radio\2026\11\19\radio-a\radio-a_2026-11-19_10-00-00.opus";

    private static TranscriptionEntry Entry(int minute, string text = "hola") =>
        new("radio-a", "Caracol", "radio", HourFile, Start.AddMinutes(minute), Start.AddMinutes(minute).AddSeconds(18), text, "recognized");

    [Fact]
    public async Task Saves_by_hour_file_and_times_loads_the_hour_in_order_and_leaves_the_old_documents_alone()
    {
        var uri = Environment.GetEnvironmentVariable("MEDIAOPS_MONGO_TEST_URI");
        if (string.IsNullOrWhiteSpace(uri))
        {
            return;
        }

        var databaseName = "mediaops-test-" + Guid.NewGuid().ToString("N");
        var client = new MongoClient(uri);
        try
        {
            var options = new MongoAlertingOptions { ConnectionString = uri, MonitoringDatabaseName = databaseName };
            var store = new MongoTranscriptionStore(options, retentionDays: 365);
            var collection = client.GetDatabase(databaseName).GetCollection<BsonDocument>("transcription");

            // A document of the old system: same collection, no sourceId.
            await collection.InsertOneAsync(new BsonDocument
            {
                { "text", "documento viejo" }, { "media", "tv" }, { "platform", "Canal1" },
                { "startTime", new DateTime(2020, 10, 6, 19, 43, 35, DateTimeKind.Utc) },
                { "endTime", new DateTime(2020, 10, 6, 19, 43, 59, DateTimeKind.Utc) },
                { "filePath", HourFile }, { "processed", true },
            });

            await store.AppendAsync(Entry(2, "segundo"));
            await store.AppendAsync(Entry(1, "primero"));
            await store.AppendAsync(Entry(1, "primero corregido")); // the same hour and times again: replaces, never duplicates
            await store.AppendManyAsync([Entry(3, "tercero"), Entry(2, "segundo")]);

            var loaded = await store.LoadHourAsync(HourFile);
            Assert.Equal(["primero corregido", "segundo", "tercero"], loaded.Select(e => e.Text));
            Assert.Equal(3, await collection.CountDocumentsAsync(Builders<BsonDocument>.Filter.Exists("sourceId")));
            Assert.Equal(1, await collection.CountDocumentsAsync(Builders<BsonDocument>.Filter.Exists("sourceId", false)));
            Assert.Empty(await store.LoadHourAsync(@"C:\other\hour.opus"));

            // The key filter of a save must use the unique index, not read the whole collection.
            var explained = await client.GetDatabase(databaseName).RunCommandAsync<BsonDocument>(new BsonDocument
            {
                { "explain", new BsonDocument { { "find", "transcription" }, { "filter", MongoTranscriptionStore.KeyFilter(Entry(1)) } } },
                { "verbosity", "queryPlanner" },
            });
            var plan = explained["queryPlanner"]["winningPlan"].ToJson();
            Assert.Contains("IXSCAN", plan);
            Assert.DoesNotContain("COLLSCAN", plan);

            var indexes = await (await collection.Indexes.ListAsync()).ToListAsync();
            var unique = indexes.Single(i => i["name"] == "transcription_hour_unique");
            var expiry = indexes.Single(i => i["name"] == "transcription_expiry");
            Assert.True(unique["unique"].AsBoolean);
            Assert.Equal(TimeSpan.FromDays(365).TotalSeconds, expiry["expireAfterSeconds"].ToDouble());
            Assert.Equal(new BsonDocument("$exists", true), unique["partialFilterExpression"]["sourceId"]);
            Assert.Equal(new BsonDocument("$exists", true), expiry["partialFilterExpression"]["sourceId"]);
        }
        finally
        {
            await client.DropDatabaseAsync(databaseName);
        }
    }
}

using System.Text.Json;
using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class JsonFileCaptureSourceRepositoryTests
{
    [Fact]
    public async Task ListAllAsync_should_return_all_sources_from_valid_json_file()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"sources-{Guid.NewGuid():N}.json");
        try
        {
            var payload = new object[]
            {
                new { sourceId = "s1", platform = "CaracolRadio", media = "radio", streamUrl = "https://a.example.com/stream" },
                new { sourceId = "s2", platform = "BluRadio",     media = "radio", streamUrl = "https://b.example.com/stream" },
                new { sourceId = "s3", platform = "CanalUno",     media = "television", streamUrl = "https://c.example.com/live", excluded = true }
            };
            await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(payload));

            var sut = new JsonFileCaptureSourceRepository(new OperationsWorkerOptions { CaptureSourcesFilePath = tempPath });

            var result = await sut.ListAllAsync();

            Assert.Equal(3, result.Count);
            Assert.True(result[2].IsExcluded);
        }
        finally { if (File.Exists(tempPath)) File.Delete(tempPath); }
    }

    [Fact]
    public async Task ListAllAsync_should_throw_FileNotFoundException_when_file_does_not_exist()
    {
        var sut = new JsonFileCaptureSourceRepository(new OperationsWorkerOptions
        {
            CaptureSourcesFilePath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json")
        });

        await Assert.ThrowsAsync<FileNotFoundException>(() => sut.ListAllAsync());
    }

    [Fact]
    public async Task ListAllAsync_should_throw_InvalidOperationException_when_file_has_empty_array()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"empty-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(tempPath, "[]");
            var sut = new JsonFileCaptureSourceRepository(new OperationsWorkerOptions { CaptureSourcesFilePath = tempPath });

            await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ListAllAsync());
        }
        finally { if (File.Exists(tempPath)) File.Delete(tempPath); }
    }

    [Fact]
    public async Task ListAllAsync_should_map_optional_fields_when_present()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"sources-{Guid.NewGuid():N}.json");
        try
        {
            var payload = new[]
            {
                new
                {
                    sourceId = "s1", platform = "P", media = "radio",
                    streamUrl = "https://a.example.com/s",
                    primaryUrl = "https://a.example.com",
                    country = "colombia",
                    fallbackStreamUrls = new[] { "https://fallback.example.com/s" }
                }
            };
            await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(payload));

            var sut = new JsonFileCaptureSourceRepository(new OperationsWorkerOptions { CaptureSourcesFilePath = tempPath });

            var result = await sut.ListAllAsync();

            Assert.Single(result);
            Assert.Equal("https://a.example.com", result[0].PrimaryUrl);
            Assert.Single(result[0].FallbackStreamUrls);
        }
        finally { if (File.Exists(tempPath)) File.Delete(tempPath); }
    }

    [Fact]
    public async Task ListAllAsync_should_default_isExcluded_to_false_when_excluded_field_is_absent()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"sources-{Guid.NewGuid():N}.json");
        try
        {
            var payload = new[]
            {
                new { sourceId = "s1", platform = "P", media = "radio", streamUrl = "https://a.example.com/s" }
            };
            await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(payload));

            var sut = new JsonFileCaptureSourceRepository(new OperationsWorkerOptions { CaptureSourcesFilePath = tempPath });

            var result = await sut.ListAllAsync();

            Assert.False(result[0].IsExcluded);
        }
        finally { if (File.Exists(tempPath)) File.Delete(tempPath); }
    }
}

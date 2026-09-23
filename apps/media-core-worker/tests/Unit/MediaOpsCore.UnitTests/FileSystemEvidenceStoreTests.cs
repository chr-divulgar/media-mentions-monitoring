using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class FileSystemEvidenceStoreTests
{
    [Fact]
    public async Task ReadJsonAsync_should_return_null_when_file_does_not_exist()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var store = new FileSystemEvidenceStore(new OperationsWorkerOptions { StageFilesystemRootPath = tempRoot });

            var result = await store.ReadJsonAsync<TestPayload>("missing/does-not-exist.json");

            Assert.Null(result);
        }
        finally
        {
            DeleteDirectoryIfExists(tempRoot);
        }
    }

    [Fact]
    public async Task ReadJsonAsync_should_round_trip_a_value_written_by_WriteJsonAsync()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var store = new FileSystemEvidenceStore(new OperationsWorkerOptions { StageFilesystemRootPath = tempRoot });
            var payload = new TestPayload("hello", 42);

            await store.WriteJsonAsync("nested/payload.json", payload);
            var result = await store.ReadJsonAsync<TestPayload>("nested/payload.json");

            Assert.NotNull(result);
            Assert.Equal(payload.Name, result.Name);
            Assert.Equal(payload.Count, result.Count);
        }
        finally
        {
            DeleteDirectoryIfExists(tempRoot);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"evidence-store-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed record TestPayload(string Name, int Count);
}

using System.Text.Json;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// What has already been verified in the archive: relative path + the size that was uploaded.
/// It is the only thing that authorises the purge to delete a local file, so it lives on disk
/// (JSON lines, append-only, compacted on load) and survives restarts without re-uploading.
/// </summary>
public sealed class ArchiveLedger
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string path;
    private readonly object gate = new();
    private readonly Dictionary<string, long> entries = new(StringComparer.Ordinal);

    public ArchiveLedger(string path)
    {
        this.path = path;
        Load();
    }

    // Size must match too: a local file that changed after it was archived is not the copy we have.
    public bool Contains(string relativePath, long size)
    {
        lock (gate)
        {
            return entries.TryGetValue(relativePath, out var archivedSize) && archivedSize == size;
        }
    }

    public void Add(string relativePath, long size)
    {
        lock (gate)
        {
            entries[relativePath] = size;
            Append(new Entry(relativePath, size, Removed: false));
        }
    }

    public void Remove(string relativePath)
    {
        lock (gate)
        {
            if (entries.Remove(relativePath))
            {
                Append(new Entry(relativePath, 0, Removed: true));
            }
        }
    }

    private void Append(Entry entry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, JsonSerializer.Serialize(entry, SerializerOptions) + Environment.NewLine);
    }

    private void Load()
    {
        if (!File.Exists(path))
        {
            return;
        }

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            Entry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<Entry>(line, SerializerOptions);
            }
            catch (JsonException)
            {
                continue; // a line cut off by a crash mid-write; the next upload pass redoes it
            }

            if (entry is null)
            {
                continue;
            }

            if (entry.Removed)
            {
                entries.Remove(entry.Path);
            }
            else
            {
                entries[entry.Path] = entry.Size;
            }
        }

        // Compact: purged files leave "removed" lines behind; rewrite only what is still live so
        // the file does not grow forever.
        var temp = path + ".tmp";
        File.WriteAllLines(temp, entries.Select(kv => JsonSerializer.Serialize(new Entry(kv.Key, kv.Value, false), SerializerOptions)));
        File.Move(temp, path, overwrite: true);
    }

    private sealed record Entry(string Path, long Size, bool Removed);
}

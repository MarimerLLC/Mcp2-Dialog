using System.Text.Json;
using ModelContextProtocol;

namespace Mcp2Dialog.Server.Tasks;

/// <summary>
/// The in-memory store, plus one JSON file per task so tasks survive a server restart. Every change is written
/// before the method returns, so "durably created before the response is sent" holds. Fine for a demo; a real
/// deployment would use a database or distributed cache that every server instance can reach.
/// </summary>
public sealed class FileTaskStore : InMemoryTaskStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(McpJsonUtilities.DefaultOptions) { WriteIndented = true };

    private readonly string _directory;

    public FileTaskStore(string directory, TimeSpan timeToLive, long pollIntervalMs)
        : base(timeToLive, pollIntervalMs)
    {
        _directory = Directory.CreateDirectory(directory).FullName;
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            if (JsonSerializer.Deserialize<StoredTask>(File.ReadAllText(file), JsonOptions) is { } task)
            {
                Restore(task);
            }
        }
    }

    public string DirectoryPath => _directory;

    protected override void OnSaved(StoredTask task)
    {
        // Write to a temp file and rename, so a crash mid-write never leaves a half-written task behind.
        var path = PathFor(task.Info.TaskId);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(task, JsonOptions));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    protected override void OnRemoved(string taskId) => File.Delete(PathFor(taskId));

    private string PathFor(string taskId) => Path.Combine(_directory, $"{taskId}.json");
}

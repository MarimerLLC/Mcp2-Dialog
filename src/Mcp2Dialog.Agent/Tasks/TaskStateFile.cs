using System.Text.Json;

namespace Mcp2Dialog.Agent.Tasks;

/// <summary>
/// What the agent remembers about the task it's watching. It's written as soon as the server returns the task
/// handle, before the first poll, so a killed agent can come back with <c>book --resume</c>.
/// </summary>
internal sealed record TaskState
{
    public required string TaskId { get; init; }
    public required string Server { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>W3C traceparent of the first run's root span. A resumed run continues the same trace.</summary>
    public string? TraceParent { get; init; }

    // Totals across every run of the agent for this task (so the summary covers the kill and the resume).
    public int Runs { get; set; }
    public int ToolCallRequests { get; set; }
    public int Polls { get; set; }
    public int Updates { get; set; }
    public List<string> Transitions { get; init; } = [];
    public string? LastStatus { get; set; }
}

internal static class TaskStateFile
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static TaskState? Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<TaskState>(File.ReadAllText(path), Options) : null;

    /// <summary>Writes a temp file and renames it, so a kill mid-write can't leave a corrupt state file.</summary>
    public static void Save(string path, TaskState state)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(state, Options));
        File.Move(path + ".tmp", path, overwrite: true);
    }
}

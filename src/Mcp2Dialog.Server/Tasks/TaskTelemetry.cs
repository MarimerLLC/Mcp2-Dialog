using System.Diagnostics;
using System.Diagnostics.Metrics;
using Mcp2Dialog.Telemetry;
using ModelContextProtocol.Extensions.Tasks;

namespace Mcp2Dialog.Server.Tasks;

/// <summary>One short span per task-lifecycle event, plus task metrics. The store calls this on every transition.</summary>
internal static class TaskTelemetry
{
    private static readonly Counter<long> Created = DialogDiagnostics.Meter.CreateCounter<long>(
        "mcp2dialog.tasks.created", description: "Tasks created");

    private static readonly Counter<long> Finished = DialogDiagnostics.Meter.CreateCounter<long>(
        "mcp2dialog.tasks.finished", description: "Tasks that reached a terminal status, by status");

    private static readonly Histogram<double> Duration = DialogDiagnostics.Meter.CreateHistogram<double>(
        "mcp2dialog.tasks.duration", unit: "s", description: "Time from task creation to terminal status");

    /// <summary>
    /// Records a lifecycle event as its own span. It nests under whatever is current: the tools/call span on
    /// creation, the tasks/update span when input arrives, the tasks/cancel span on cancel, or the background work.
    /// </summary>
    public static void Event(string name, McpTaskInfo task, string? detail = null)
    {
        // Tag the surrounding SDK span too, so tasks/get, tasks/update and tasks/cancel are searchable by task id.
        Activity.Current?.SetTag(DialogDiagnostics.Tags.TaskId, task.TaskId);

        using var activity = DialogDiagnostics.ActivitySource.StartActivity($"task {name}");
        activity?.SetTag(DialogDiagnostics.Tags.TaskId, task.TaskId);
        activity?.SetTag(DialogDiagnostics.Tags.TaskStatus, StatusName(task.Status));
        if (detail is not null)
        {
            activity?.SetTag("mcp2dialog.task.detail", detail);
        }

        if (name == "created")
        {
            Created.Add(1);
        }
        else if (IsTerminal(task.Status))
        {
            var status = new KeyValuePair<string, object?>("status", StatusName(task.Status));
            Finished.Add(1, status);
            Duration.Record((task.LastUpdatedAt - task.CreatedAt).TotalSeconds, status);
        }
    }

    public static bool IsTerminal(McpTaskStatus status) =>
        status is McpTaskStatus.Completed or McpTaskStatus.Failed or McpTaskStatus.Cancelled;

    public static string StatusName(McpTaskStatus status) => status switch
    {
        McpTaskStatus.Working => "working",
        McpTaskStatus.InputRequired => "input_required",
        McpTaskStatus.Completed => "completed",
        McpTaskStatus.Failed => "failed",
        McpTaskStatus.Cancelled => "cancelled",
        _ => status.ToString(),
    };
}

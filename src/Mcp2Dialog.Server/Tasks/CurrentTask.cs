#pragma warning disable MCPEXP002 // CallToolWithAlternateFilters is the SDK's experimental extensibility seam

using ModelContextProtocol.Server;

namespace Mcp2Dialog.Server.Tasks;

/// <summary>
/// Tells a tool which task it is running as.
/// </summary>
/// <remarks>
/// SDK 2.2.0 runs a task-backed tool in the background but doesn't pass the task id to it, and the tool needs the
/// id to post <c>statusMessage</c> updates. So a filter that runs just before the SDK's Tasks filter puts an empty
/// holder in an <see cref="AsyncLocal{T}"/>. The store fills it in when the SDK creates the task, and the
/// background work inherits it. A tool that runs synchronously (the client didn't declare Tasks) sees null.
/// </remarks>
public static class CurrentTask
{
    private sealed class Holder
    {
        public string? TaskId;
    }

    private static readonly AsyncLocal<Holder?> Current = new();

    /// <summary>The id of the task the current tool call is running as, or null if it isn't a task.</summary>
    public static string? Id => Current.Value?.TaskId;

    /// <summary>Called by the task store when the SDK creates a task for the current tools/call.</summary>
    internal static void Assign(string taskId)
    {
        if (Current.Value is { } holder)
        {
            holder.TaskId = taskId;
        }
    }

    /// <summary>
    /// Adds the filter that sets up the holder. It must be registered before WithTasks so it runs first.
    /// </summary>
    public static void AddFilter(McpServerOptions options) =>
        options.Filters.Request.CallToolWithAlternateFilters.Insert(0, (context, next, cancellationToken) =>
        {
            Current.Value = new Holder();
            return next(context, cancellationToken);
        });
}

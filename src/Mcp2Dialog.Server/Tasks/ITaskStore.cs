using System.Text.Json;
using ModelContextProtocol.Extensions.Tasks;

namespace Mcp2Dialog.Server.Tasks;

/// <summary>
/// The SDK's <see cref="IMcpTaskStore"/> backs tasks/get, tasks/update and tasks/cancel. This adds the few things
/// the demo needs that the SDK interface doesn't have: a progress message (<c>statusMessage</c>), cancelling with a
/// reason, and a small piece of tool-owned "work state" so a file-backed store can pick a task up after a server restart.
/// </summary>
public interface ITaskStore : IMcpTaskStore
{
    /// <summary>Updates the task's <c>statusMessage</c>, which clients see on their next tasks/get.</summary>
    Task SetStatusMessageAsync(string taskId, string statusMessage, CancellationToken cancellationToken = default);

    /// <summary>Ends the task as <c>cancelled</c> with a message saying why. No-op if the task is already terminal.</summary>
    Task<bool> SetCancelledAsync(string taskId, string statusMessage, CancellationToken cancellationToken = default);

    /// <summary>Saves the tool's own progress (for example, which stage it reached) next to the task.</summary>
    Task SetWorkStateAsync(string taskId, JsonElement workState, CancellationToken cancellationToken = default);

    Task<JsonElement?> GetWorkStateAsync(string taskId, CancellationToken cancellationToken = default);

    /// <summary>Tasks that aren't terminal yet. After a restart these have no background work running.</summary>
    IReadOnlyList<McpTaskInfo> ListUnfinished();

    /// <summary>Raised when a task becomes <c>cancelled</c> (for example, by tasks/cancel).</summary>
    event Action<string>? TaskCancelled;
}

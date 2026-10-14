using System.Diagnostics;
using System.Text.Json;
using Mcp2Dialog.Telemetry;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace Mcp2Dialog.Server.Tasks;

/// <summary>
/// Default <see cref="ITaskStore"/>: everything lives in memory, and a task is discarded once its TTL has passed.
/// Terminal transitions are idempotent, so a late cancel can't overwrite a result (and vice versa).
/// </summary>
public class InMemoryTaskStore(TimeSpan timeToLive, long pollIntervalMs) : ITaskStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, StoredTask> _tasks = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expired = new(StringComparer.Ordinal);

    public event Action<InputResponseReceivedEventArgs>? InputResponseReceived;
    public event Action<string>? TaskCancelled;

    public TimeSpan TimeToLive { get; } = timeToLive;
    public long PollIntervalMs { get; } = pollIntervalMs;

    /// <summary>A task plus the tool's own work state. This is also what the file-backed store writes to disk.</summary>
    protected sealed record StoredTask(McpTaskInfo Info, JsonElement? WorkState = null);

    // Task creation: the SDK calls this before it sends resultType "task", so the task must exist when this returns.
    public Task<McpTaskInfo> CreateTaskAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var info = new McpTaskInfo(
            Guid.CreateVersion7().ToString("N"), McpTaskStatus.Working, now, now,
            TimeToLive, PollIntervalMs, StatusMessage: "Accepted, starting work");

        lock (_gate)
        {
            SweepExpired(now);
            Save(new StoredTask(info));
        }

        // Let the tool that's about to run in the background know which task it is (the SDK doesn't pass it in).
        CurrentTask.Assign(info.TaskId);
        TaskTelemetry.Event("created", info);
        return Task.FromResult(info);
    }

    // tasks/get. Unknown ids return null (the SDK answers -32602 "Unknown task"); expired ids get their own message.
    public Task<McpTaskInfo?> GetTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        Activity.Current?.SetTag(DialogDiagnostics.Tags.TaskId, taskId);
        lock (_gate)
        {
            SweepExpired(DateTimeOffset.UtcNow);
            if (_expired.Contains(taskId))
            {
                throw new McpProtocolException($"Failed to retrieve task: task '{taskId}' has expired", McpErrorCode.InvalidParams);
            }

            return Task.FromResult(_tasks.GetValueOrDefault(taskId)?.Info);
        }
    }

    public Task SetCompletedAsync(string taskId, JsonElement result, CancellationToken cancellationToken = default)
    {
        Transition(taskId, "completed", info => info with { Status = McpTaskStatus.Completed, Result = result, InputRequests = null });
        return Task.CompletedTask;
    }

    public Task SetFailedAsync(string taskId, JsonElement error, CancellationToken cancellationToken = default)
    {
        var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
        Transition(taskId, "failed", info => info with
        {
            Status = McpTaskStatus.Failed, Error = error, InputRequests = null, StatusMessage = $"Failed: {message}",
        });
        return Task.CompletedTask;
    }

    // tasks/cancel (from the SDK). Cooperative: the SDK also signals the tool's CancellationToken.
    public Task<bool> SetCancelledAsync(string taskId, CancellationToken cancellationToken = default) =>
        SetCancelledAsync(taskId, "Cancelled by the client (tasks/cancel)", cancellationToken);

    public Task<bool> SetCancelledAsync(string taskId, string statusMessage, CancellationToken cancellationToken = default)
    {
        var cancelled = Transition(taskId, "cancelled", info => info with
        {
            Status = McpTaskStatus.Cancelled, InputRequests = null, StatusMessage = statusMessage,
        }, mustExist: true);

        if (cancelled)
        {
            TaskCancelled?.Invoke(taskId);
        }

        return Task.FromResult(cancelled);
    }

    // Mid-flight question: the SDK calls this when the tool calls server.ElicitAsync inside a task.
    public Task SetInputRequestsAsync(string taskId, IDictionary<string, InputRequest> inputRequests, CancellationToken cancellationToken = default)
    {
        Transition(taskId, "input_required", info => info with
        {
            Status = McpTaskStatus.InputRequired,
            InputRequests = new Dictionary<string, InputRequest>(
                (info.InputRequests ?? new Dictionary<string, InputRequest>()).Concat(inputRequests)),
        }, detail: string.Join(",", inputRequests.Keys));
        return Task.CompletedTask;
    }

    // tasks/update. Only keys that are still outstanding count; unknown or already-answered keys are ignored.
    public Task ResolveInputRequestsAsync(string taskId, IDictionary<string, InputResponse> inputResponses, CancellationToken cancellationToken = default)
    {
        var resolved = new List<KeyValuePair<string, InputResponse>>();
        lock (_gate)
        {
            var info = Find(taskId).Info;
            if (TaskTelemetry.IsTerminal(info.Status) || info.InputRequests is not { Count: > 0 } pending)
            {
                return Task.CompletedTask;
            }

            resolved.AddRange(inputResponses.Where(r => pending.ContainsKey(r.Key)));
            if (resolved.Count == 0)
            {
                return Task.CompletedTask;
            }

            var remaining = pending.Where(p => !inputResponses.ContainsKey(p.Key)).ToDictionary();
            var updated = info with
            {
                Status = remaining.Count == 0 ? McpTaskStatus.Working : McpTaskStatus.InputRequired,
                InputRequests = remaining.Count == 0 ? null : remaining,
                StatusMessage = remaining.Count == 0 ? "Answer received, continuing" : info.StatusMessage,
                LastUpdatedAt = DateTimeOffset.UtcNow,
            };
            Save(_tasks[taskId] with { Info = updated });
            TaskTelemetry.Event("input received", updated, string.Join(",", resolved.Select(r => r.Key)));
        }

        // Wakes the tool's pending server.ElicitAsync call.
        foreach (var (key, response) in resolved)
        {
            InputResponseReceived?.Invoke(new InputResponseReceivedEventArgs { TaskId = taskId, RequestId = key, Response = response });
        }

        return Task.CompletedTask;
    }

    public Task SetStatusMessageAsync(string taskId, string statusMessage, CancellationToken cancellationToken = default)
    {
        Transition(taskId, "progress", info => info with { StatusMessage = statusMessage }, detail: statusMessage);
        return Task.CompletedTask;
    }

    public Task SetWorkStateAsync(string taskId, JsonElement workState, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_tasks.TryGetValue(taskId, out var stored))
            {
                Save(stored with { WorkState = workState });
            }
        }

        return Task.CompletedTask;
    }

    public Task<JsonElement?> GetWorkStateAsync(string taskId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_tasks.TryGetValue(taskId, out var stored) ? stored.WorkState : null);
        }
    }

    public IReadOnlyList<McpTaskInfo> ListUnfinished()
    {
        lock (_gate)
        {
            SweepExpired(DateTimeOffset.UtcNow);
            return [.. _tasks.Values.Select(t => t.Info).Where(i => !TaskTelemetry.IsTerminal(i.Status))];
        }
    }

    /// <summary>Called (under the lock) whenever a task is created or changes. The file-backed store persists it here.</summary>
    protected virtual void OnSaved(StoredTask task)
    {
    }

    /// <summary>Called (under the lock) when an expired task is discarded.</summary>
    protected virtual void OnRemoved(string taskId)
    {
    }

    /// <summary>Puts a task loaded from durable storage back into memory.</summary>
    protected void Restore(StoredTask task)
    {
        lock (_gate)
        {
            _tasks[task.Info.TaskId] = task;
        }
    }

    /// <summary>
    /// Applies a change unless the task is already terminal. Returns false if nothing changed. A task that has expired
    /// (or never existed) is an error only when a client asked for the change (<paramref name="mustExist"/>); for the
    /// background work it's a no-op.
    /// </summary>
    private bool Transition(string taskId, string eventName, Func<McpTaskInfo, McpTaskInfo> change, string? detail = null, bool mustExist = false)
    {
        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskId, out var stored))
            {
                return mustExist ? throw new McpProtocolException($"Task not found: '{taskId}'", McpErrorCode.InvalidParams) : false;
            }

            if (TaskTelemetry.IsTerminal(stored.Info.Status))
            {
                return false;
            }

            var updated = change(stored.Info) with { LastUpdatedAt = DateTimeOffset.UtcNow };
            Save(stored with { Info = updated });
            TaskTelemetry.Event(eventName, updated, detail);
            return true;
        }
    }

    private StoredTask Find(string taskId) =>
        _tasks.TryGetValue(taskId, out var stored)
            ? stored
            : throw new McpProtocolException($"Task not found: '{taskId}'", McpErrorCode.InvalidParams);

    private void Save(StoredTask task)
    {
        _tasks[task.Info.TaskId] = task;
        OnSaved(task);
    }

    private void SweepExpired(DateTimeOffset now)
    {
        foreach (var expired in _tasks.Values.Where(t => IsExpired(t.Info, now)).Select(t => t.Info.TaskId).ToList())
        {
            _tasks.Remove(expired);
            _expired.Add(expired);
            OnRemoved(expired);
        }
    }

    private static bool IsExpired(McpTaskInfo info, DateTimeOffset now) =>
        info.TimeToLive is { } ttl && ttl > TimeSpan.Zero && now > info.CreatedAt + ttl;
}

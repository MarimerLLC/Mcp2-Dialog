using System.Diagnostics;
using System.Text.Json;
using Mcp2Dialog.Server.Tasks;
using Mcp2Dialog.Telemetry;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace Mcp2Dialog.Server.Freight;

/// <summary>
/// After a server restart with the file-backed store, unfinished bookings are still on disk but nothing is working on
/// them. This picks each one up from its saved stage, under the same task id, so a polling client just sees it carry on.
/// </summary>
/// <remarks>
/// The SDK's own background runner only exists for tasks created in this process, so this does by hand what it does
/// for a question: put the elicitation in the task's inputRequests and wait for the store to report the answer.
/// </remarks>
public sealed class FreightResumeService(ITaskStore store, FreightBooking booking, ILogger<FreightResumeService> logger)
    : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var task in store.ListUnfinished())
        {
            _ = Task.Run(() => ResumeAsync(task, stoppingToken), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    private async Task ResumeAsync(McpTaskInfo task, CancellationToken stoppingToken)
    {
        var taskId = task.TaskId;
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["TaskId"] = taskId });
        using var activity = DialogDiagnostics.ActivitySource.StartActivity("freight booking (resumed after restart)", ActivityKind.Internal);
        activity?.SetTag(DialogDiagnostics.Tags.TaskId, taskId);
        activity?.SetTag(DialogDiagnostics.Tags.TaskResumed, true);

        if (FreightBooking.ReadProgress(await store.GetWorkStateAsync(taskId, stoppingToken)) is not { } progress)
        {
            await store.SetCancelledAsync(taskId, "Cancelled: the server restarted before the booking started.", CancellationToken.None);
            return;
        }

        logger.LogInformation("Task {TaskId}: resuming booking at stage {Stage} after a server restart", taskId, progress.NextStage + 1);

        // tasks/cancel still works: the store raises TaskCancelled, which stops this run.
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        void OnCancelled(string id)
        {
            if (id == taskId)
            {
                cancellation.Cancel();
            }
        }

        store.TaskCancelled += OnCancelled;
        try
        {
            var summary = await booking.RunAsync(taskId, progress, (question, ct) => AskAsync(taskId, question, ct), cancellation.Token);
            var result = new CallToolResult { Content = [new TextContentBlock { Text = summary }] };
            await store.SetCompletedAsync(taskId, JsonSerializer.SerializeToElement(result, McpJsonUtilities.DefaultOptions), CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            logger.LogInformation("Task {TaskId}: stopped (cancelled or server shutting down)", taskId);
        }
        finally
        {
            store.TaskCancelled -= OnCancelled;
        }
    }

    /// <summary>Asks through the task: reuses a question that was already outstanding before the restart, or adds a new one.</summary>
    private async ValueTask<ElicitResult> AskAsync(string taskId, ElicitRequestParams question, CancellationToken cancellationToken)
    {
        var current = await store.GetTaskAsync(taskId, cancellationToken);
        var key = current?.InputRequests?.Keys.FirstOrDefault() ?? Guid.NewGuid().ToString("N");

        var answer = new TaskCompletionSource<InputResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnInput(InputResponseReceivedEventArgs e)
        {
            if (e.TaskId == taskId && e.RequestId == key)
            {
                answer.TrySetResult(e.Response);
            }
        }

        store.InputResponseReceived += OnInput;
        try
        {
            if (current?.InputRequests?.ContainsKey(key) != true)
            {
                await store.SetInputRequestsAsync(taskId, new Dictionary<string, InputRequest> { [key] = InputRequest.ForElicitation(question) }, cancellationToken);
            }

            var response = await answer.Task.WaitAsync(cancellationToken);
            return response.Deserialize(InputResponse.ElicitResultJsonTypeInfo) ?? new ElicitResult { Action = "cancel" };
        }
        finally
        {
            store.InputResponseReceived -= OnInput;
        }
    }
}

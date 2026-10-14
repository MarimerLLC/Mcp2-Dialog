using System.Diagnostics;
using System.Text.Json;
using Mcp2Dialog.Telemetry;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using Spectre.Console;

namespace Mcp2Dialog.Agent.Tasks;

/// <summary>
/// Follows one task to the end: polls tasks/get at the server's pollIntervalMs, answers inputRequests with
/// tasks/update, and shows the result. It only ever needs the task id, so it works the same after a restart.
/// </summary>
internal sealed class TaskWatcher(McpClient client, ElicitationPrompter prompter, TaskState state, string statePath, bool live)
{
    private static readonly TimeSpan UnreachableGiveUp = TimeSpan.FromSeconds(60);

    private readonly HashSet<string> _answeredKeys = new(StringComparer.Ordinal);
    private long _pollIntervalMs = 1000;

    public enum Outcome { Completed, Failed, Cancelled, Lost }

    public sealed record Finished(Outcome Outcome, GetTaskResult? Task, string Message);

    public async Task<Finished> WatchAsync(long? pollIntervalMs, CancellationToken cancellationToken)
    {
        _pollIntervalMs = pollIntervalMs ?? _pollIntervalMs;

        while (true)
        {
            var current = live
                ? await PollLiveAsync(cancellationToken)
                : await PollUntilActionAsync(onPoll: PrintIfChanged, cancellationToken);

            switch (current)
            {
                case null:
                    return new(Outcome.Lost, null, "");
                case CompletedTaskResult completed:
                    return new(Outcome.Completed, completed, ResultText(completed.Result));
                case FailedTaskResult failed:
                    return new(Outcome.Failed, failed, failed.Error.ToString());
                case CancelledTaskResult cancelled:
                    return new(Outcome.Cancelled, cancelled, cancelled.StatusMessage ?? "Cancelled");
                case InputRequiredTaskResult inputRequired:
                    await AnswerAsync(inputRequired, cancellationToken);
                    break;
            }
        }
    }

    /// <summary>
    /// Polls until something needs the agent: a terminal status, or input_required with a question we haven't
    /// answered yet. Returns null if the task can't be found (unknown or expired).
    /// </summary>
    private async Task<GetTaskResult?> PollUntilActionAsync(Action<GetTaskResult> onPoll, CancellationToken cancellationToken)
    {
        var unreachableSince = (DateTimeOffset?)null;
        while (true)
        {
            GetTaskResult task;
            try
            {
                // Poll: tasks/get {taskId}. The SDK adds the Tasks capability to _meta and sets Mcp-Name to the task id.
                task = await client.GetTaskAsync(state.TaskId, cancellationToken);
                unreachableSince = null;
            }
            catch (HttpRequestException) when ((unreachableSince ??= DateTimeOffset.UtcNow) > DateTimeOffset.UtcNow - UnreachableGiveUp)
            {
                // The server may be restarting (with the file-backed store the task survives that). Keep trying for a while.
                AnsiConsole.MarkupLine($"  [grey]{DateTime.Now:HH:mm:ss}[/] [red]server unreachable, retrying...[/]");
                await Task.Delay(TimeSpan.FromMilliseconds(_pollIntervalMs), cancellationToken);
                continue;
            }
            catch (McpProtocolException ex) when (ex.ErrorCode == McpErrorCode.InvalidParams)
            {
                // Unknown or expired task id: -32602 from the server.
                Lost = ex.Message;
                return null;
            }

            Record(task);
            onPoll(task);

            if (task.PollIntervalMs is { } interval)
            {
                _pollIntervalMs = interval;
            }

            if (task is CompletedTaskResult or FailedTaskResult or CancelledTaskResult ||
                task is InputRequiredTaskResult { InputRequests: { } requests } && requests.Keys.Any(k => !_answeredKeys.Contains(k)))
            {
                return task;
            }

            // Honour the server's pollIntervalMs between polls.
            await Task.Delay(TimeSpan.FromMilliseconds(_pollIntervalMs), cancellationToken);
        }
    }

    /// <summary>Why the task couldn't be found, as reported by the server.</summary>
    public string? Lost { get; private set; }

    /// <summary>The same loop inside a Spectre live display that redraws the status panel on every poll.</summary>
    private async Task<GetTaskResult?> PollLiveAsync(CancellationToken cancellationToken)
    {
        GetTaskResult? last = null;
        var display = new Table().HideHeaders().Border(TableBorder.Rounded).AddColumn("").AddColumn("");
        GetTaskResult? result = null;

        await AnsiConsole.Live(display).AutoClear(false).StartAsync(async context =>
        {
            using var ticker = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            var poll = PollUntilActionAsync(task => last = task, cancellationToken);
            while (!poll.IsCompleted)
            {
                Render(display, last);
                context.Refresh();
                await Task.WhenAny(poll, ticker.WaitForNextTickAsync(cancellationToken).AsTask());
            }

            result = await poll;
            Render(display, result);
            context.Refresh();
        });

        return result;
    }

    private void Render(Table display, GetTaskResult? task)
    {
        display.Rows.Clear();
        display.AddRow("[grey]Task[/]", Markup.Escape(state.TaskId));
        display.AddRow("[grey]Status[/]", task is null ? "[grey](polling...)[/]" : StatusMarkup(task.Status));
        display.AddRow("[grey]Message[/]", Markup.Escape(task?.StatusMessage ?? ""));
        display.AddRow("[grey]Elapsed[/]", Elapsed());
        display.AddRow("[grey]Polls[/]", $"{state.Polls} [grey](every {_pollIntervalMs} ms)[/]");
    }

    private string? _lastPrinted;

    private void PrintIfChanged(GetTaskResult task)
    {
        var line = $"{StatusMarkup(task.Status)}  {Markup.Escape(task.StatusMessage ?? "")}";
        if (line != _lastPrinted)
        {
            _lastPrinted = line;
            AnsiConsole.MarkupLine($"  [grey]{DateTime.Now:HH:mm:ss} · {Elapsed()} · poll {state.Polls}[/]  {line}");
        }
    }

    /// <summary>Counts the poll and any status change, and saves the state file so a later resume has the totals.</summary>
    private void Record(GetTaskResult task)
    {
        state.Polls++;
        var status = StatusName(task.Status);
        if (status != state.LastStatus)
        {
            state.Transitions.Add(status);
            state.LastStatus = status;
            Activity.Current?.AddEvent(new ActivityEvent($"task {status}", tags: new ActivityTagsCollection
            {
                [DialogDiagnostics.Tags.TaskId] = state.TaskId,
                ["mcp2dialog.task.status_message"] = task.StatusMessage,
            }));
        }

        TaskStateFile.Save(statePath, state);
    }

    /// <summary>input_required: show each new question with the existing prompter, then answer with tasks/update.</summary>
    private async Task AnswerAsync(InputRequiredTaskResult task, CancellationToken cancellationToken)
    {
        var responses = new Dictionary<string, InputResponse>();
        foreach (var (key, request) in (task.InputRequests ?? new Dictionary<string, InputRequest>()).Where(r => !_answeredKeys.Contains(r.Key)))
        {
            using var activity = DialogDiagnostics.ActivitySource.StartActivity("task input");
            activity?.SetTag(DialogDiagnostics.Tags.TaskId, state.TaskId);

            if (request.Method == RequestMethods.ElicitationCreate &&
                request.Params?.Deserialize(McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(ElicitRequestParams))) is ElicitRequestParams elicit)
            {
                responses[key] = InputResponse.FromElicitResult(await prompter.HandleAsync(elicit, cancellationToken));
            }
            else
            {
                // This agent only knows elicitation. Anything else gets a polite no.
                AnsiConsole.MarkupLine($"[yellow]Can't answer a '{Markup.Escape(request.Method)}' request; declining.[/]");
                responses[key] = InputResponse.FromElicitResult(new ElicitResult { Action = "decline" });
            }
        }

        // Update: tasks/update {taskId, inputResponses}. The server acknowledges with an empty result.
        await client.UpdateTaskAsync(new UpdateTaskRequestParams { TaskId = state.TaskId, InputResponses = responses }, cancellationToken);
        _answeredKeys.UnionWith(responses.Keys);
        state.Updates++;
        TaskStateFile.Save(statePath, state);
    }

    private string Elapsed() =>
        state.CreatedAt == default ? "" : $"{DateTimeOffset.UtcNow - state.CreatedAt:mm\\:ss}";

    private static string ResultText(JsonElement result)
    {
        var callToolResult = result.Deserialize(McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(CallToolResult))) as CallToolResult;
        return string.Join(Environment.NewLine, callToolResult?.Content.OfType<TextContentBlock>().Select(c => c.Text) ?? []);
    }

    public static string StatusName(McpTaskStatus status) => status switch
    {
        McpTaskStatus.Working => "working",
        McpTaskStatus.InputRequired => "input_required",
        McpTaskStatus.Completed => "completed",
        McpTaskStatus.Failed => "failed",
        McpTaskStatus.Cancelled => "cancelled",
        _ => status.ToString(),
    };

    public static string StatusMarkup(McpTaskStatus status) => status switch
    {
        McpTaskStatus.Working => "[blue]working[/]",
        McpTaskStatus.InputRequired => "[yellow bold]input_required[/]",
        McpTaskStatus.Completed => "[green bold]completed[/]",
        McpTaskStatus.Failed => "[red bold]failed[/]",
        McpTaskStatus.Cancelled => "[red]cancelled[/]",
        _ => Markup.Escape(status.ToString()),
    };
}

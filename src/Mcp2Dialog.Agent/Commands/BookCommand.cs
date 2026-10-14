using System.ComponentModel;
using System.Diagnostics;
using Mcp2Dialog.Agent.Tasks;
using Mcp2Dialog.Telemetry;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Mcp2Dialog.Agent.Commands;

internal sealed class BookSettings : AgentSettings
{
    [CommandOption("--quote-ref <REF>")]
    [Description("Reference of an earlier quote (optional).")]
    public string? QuoteRef { get; set; }

    [CommandOption("-d|--destination <CITY>")]
    [Description("Destination city (default on the server: Oslo).")]
    public string? Destination { get; set; }

    [CommandOption("-w|--weight <KG>")]
    [Description("Weight in kg (default on the server: 42).")]
    public double? WeightKg { get; set; }

    [CommandOption("-s|--speed <SPEED>")]
    [Description("standard, express, or overnight (default on the server: express).")]
    public string? Speed { get; set; }

    [CommandOption("--auto")]
    [Description("Answer the server's mid-flight question automatically (accept the new pickup window).")]
    public bool Auto { get; set; }

    [CommandOption("--resume [TASKID]")]
    [Description("Re-attach to a running task instead of booking again. Without an id, uses the state file.")]
    public FlagValue<string>? Resume { get; set; }

    [CommandOption("--cancel [TASKID]")]
    [Description("Send tasks/cancel for a task. Without an id, uses the state file.")]
    public FlagValue<string>? Cancel { get; set; }

    [CommandOption("--state-file <PATH>")]
    [Description("Where the task id is saved for --resume.")]
    [DefaultValue(".demo/last-task.json")]
    public string StateFile { get; set; } = ".demo/last-task.json";

    [CommandOption("--no-tasks", IsHidden = true)]
    [Description("Don't declare the Tasks extension (to show the server's fallback).")]
    public bool NoTasks { get; set; }
}

/// <summary>
/// Books freight through the Tasks extension: one tools/call that comes back as a task handle, then tasks/get polls,
/// a tasks/update for the mid-flight question, and the result. --resume picks the same task up after a kill.
/// </summary>
internal sealed class BookCommand : AsyncCommand<BookSettings>
{
    private const string ToolName = "book_freight";

    public override async Task<int> ExecuteAsync(CommandContext context, BookSettings settings, CancellationToken cancellationToken)
    {
        var saved = TaskStateFile.Load(settings.StateFile);
        if ((settings.Resume?.IsSet == true || settings.Cancel?.IsSet == true) &&
            (settings.Resume?.Value ?? settings.Cancel?.Value ?? saved?.TaskId) is null)
        {
            AnsiConsole.MarkupLine($"[red]No task to resume:[/] pass a task id, or book first (no state file at {Markup.Escape(settings.StateFile)}).");
            return 2;
        }

        await using var session = await AgentSession.StartAsync(settings, settings.Auto, cancellationToken, compactTaskPolls: true);
        session.RenderConnection();

        if (settings.Cancel?.IsSet == true)
        {
            return await CancelAsync(session, settings.Cancel.Value ?? saved!.TaskId, cancellationToken);
        }

        if (settings.Resume?.IsSet == true)
        {
            var taskId = settings.Resume.Value ?? saved!.TaskId;
            var state = saved?.TaskId == taskId ? saved : new TaskState { TaskId = taskId, Server = settings.Server };

            // Resume: no tools/call. Re-attach with tasks/get from wherever the task is now, and continue the first run's trace.
            using var resumed = DialogDiagnostics.ActivitySource.StartActivity(
                $"agent {ToolName} (resumed)", ActivityKind.Internal, parentId: state.TraceParent ?? default(string));
            resumed?.SetTag(DialogDiagnostics.Tags.TaskId, taskId);
            resumed?.SetTag(DialogDiagnostics.Tags.TaskResumed, true);

            AnsiConsole.Write(new Rule($"[yellow]Resuming task {Markup.Escape(taskId)}[/] [grey](no new tools/call)[/]").LeftJustified());
            return await WatchAsync(session, settings, state, pollIntervalMs: null, resumed, cancellationToken);
        }

        return await BookAsync(session, settings, cancellationToken);
    }

    private static async Task<int> BookAsync(AgentSession session, BookSettings settings, CancellationToken cancellationToken)
    {
        // Discover the tool first (this also fills the SDK's tool cache, used for the Mcp-Param-* headers).
        var tools = await session.Client.ListToolsAsync(cancellationToken: cancellationToken);
        if (tools.All(t => t.Name != ToolName))
        {
            AnsiConsole.MarkupLine($"[red]The server doesn't expose {ToolName}.[/]");
            return 1;
        }

        var arguments = new Dictionary<string, object?>();
        if (settings.QuoteRef is not null) arguments["quoteRef"] = settings.QuoteRef;
        if (settings.Destination is not null) arguments["destination"] = settings.Destination;
        if (settings.WeightKg is not null) arguments["weightKg"] = settings.WeightKg;
        if (settings.Speed is not null) arguments["speed"] = settings.Speed;

        using var activity = DialogDiagnostics.ActivitySource.StartActivity($"agent {ToolName}", ActivityKind.Internal);
        activity?.SetTag(DialogDiagnostics.Tags.ToolName, ToolName);

        AnsiConsole.MarkupLine($"[bold]Agent →[/] {ToolName} {Markup.Escape(System.Text.Json.JsonSerializer.Serialize(arguments))}" +
                               (settings.NoTasks ? " [red](without the Tasks extension)[/]" : " [grey](declaring io.modelcontextprotocol/tasks)[/]"));

        if (settings.NoTasks)
        {
            // No capability declared, so the server must not return a task. Expect an ordinary result.
            var plain = await session.Client.CallToolAsync(ToolName, arguments, cancellationToken: cancellationToken);
            ShowResult(Text(plain), plain.IsError == true ? "Server error" : "Server answer (no task)", plain.IsError == true ? Color.Red : Color.Yellow);
            ShowSummary(session, null, activity);
            return plain.IsError == true ? 1 : 0;
        }

        // Capability declaration + both result shapes: CallToolAsTaskAsync adds io.modelcontextprotocol/tasks to this
        // request's _meta, and returns either a normal CallToolResult or the CreateTaskResult (resultType: "task").
        var response = await session.Client.CallToolAsTaskAsync(
            new CallToolRequestParams
            {
                Name = ToolName,
                Arguments = arguments.ToDictionary(a => a.Key, a => System.Text.Json.JsonSerializer.SerializeToElement(a.Value)),
            },
            cancellationToken);

        if (!response.IsTask)
        {
            ShowResult(Text(response.Result!), "Server answer (no task)", Color.Green);
            ShowSummary(session, null, activity);
            return response.Result!.IsError == true ? 1 : 0;
        }

        // Task created: save the handle before the first poll, so a kill from here on can be resumed.
        var created = response.TaskCreated!;
        var state = new TaskState
        {
            TaskId = created.TaskId,
            Server = settings.Server,
            CreatedAt = created.CreatedAt,
            TraceParent = activity?.Id,
            ToolCallRequests = session.Stats.ToolCallRequests,
            Transitions = { TaskWatcher.StatusName(created.Status) },
            LastStatus = TaskWatcher.StatusName(created.Status),
        };
        TaskStateFile.Save(settings.StateFile, state);
        activity?.SetTag(DialogDiagnostics.Tags.TaskId, created.TaskId);

        AnsiConsole.Write(new Panel(new Markup(
                $"task id [bold]{Markup.Escape(created.TaskId)}[/]  ·  ttl {created.TimeToLive?.TotalMinutes:0} min  ·  poll every {created.PollIntervalMs} ms\n" +
                $"[grey]saved to {Markup.Escape(Path.GetFullPath(settings.StateFile))}[/]"))
            .Header("[magenta]Server returned a task handle[/]").BorderColor(Color.Magenta1));

        // End the call's span and flush telemetry now, so the start of the trace survives even if this process is
        // killed. Watching (and any later resumed run) continues as children of it, in the same trace.
        activity?.Stop();
        session.FlushTelemetry();
        using var watching = DialogDiagnostics.ActivitySource.StartActivity(
            $"agent watch task", ActivityKind.Internal, activity?.Context ?? default);
        watching?.SetTag(DialogDiagnostics.Tags.TaskId, created.TaskId);

        return await WatchAsync(session, settings, state, created.PollIntervalMs, watching, cancellationToken);
    }

    private static async Task<int> WatchAsync(
        AgentSession session, BookSettings settings, TaskState state, long? pollIntervalMs, Activity? activity, CancellationToken cancellationToken)
    {
        state.Runs++;
        var toolCallsBefore = session.Stats.ToolCallRequests;
        var live = !settings.Wire && AnsiConsole.Profile.Capabilities.Interactive;
        var watcher = new TaskWatcher(session.Client, session.Prompter, state, settings.StateFile, live);

        TaskWatcher.Finished finished;
        try
        {
            finished = await watcher.WatchAsync(pollIntervalMs, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Ctrl+C detaches. The task keeps running on the server.
            state.ToolCallRequests += session.Stats.ToolCallRequests - toolCallsBefore;
            TaskStateFile.Save(settings.StateFile, state);
            AnsiConsole.MarkupLine($"\n[yellow]Detached from task {Markup.Escape(state.TaskId)}.[/] It keeps running on the server. " +
                                   "Re-attach with [bold]book --resume[/].");
            return 130;
        }

        // Count any tools/call made while watching (there should be none: polling never re-issues the call).
        state.ToolCallRequests += session.Stats.ToolCallRequests - toolCallsBefore;
        TaskStateFile.Save(settings.StateFile, state);
        activity?.SetTag(DialogDiagnostics.Tags.TaskStatus, state.LastStatus);

        switch (finished.Outcome)
        {
            case TaskWatcher.Outcome.Completed:
                ShowResult(finished.Message, "[green]Task completed[/]", Color.Green);
                break;
            case TaskWatcher.Outcome.Cancelled:
                ShowResult(finished.Message, "[red]Task cancelled[/]", Color.Red);
                break;
            case TaskWatcher.Outcome.Failed:
                ShowResult(finished.Message, "[red]Task failed[/]", Color.Red);
                break;
            case TaskWatcher.Outcome.Lost:
                activity?.SetStatus(ActivityStatusCode.Error, watcher.Lost);
                ShowResult($"{watcher.Lost}\n\nThe server no longer knows task {state.TaskId}: it may have expired, " +
                           "or the server restarted with the in-memory task store. Start a new booking with: book",
                    "[red]Task not found[/]", Color.Red);
                return 2;
        }

        ShowSummary(session, state, activity);
        return finished.Outcome == TaskWatcher.Outcome.Completed ? 0 : 1;
    }

    private static async Task<int> CancelAsync(AgentSession session, string taskId, CancellationToken cancellationToken)
    {
        using var activity = DialogDiagnostics.ActivitySource.StartActivity($"agent cancel {ToolName}");
        activity?.SetTag(DialogDiagnostics.Tags.TaskId, taskId);
        try
        {
            // Cancel: tasks/cancel {taskId}. Cooperative; the ack is empty, so ask for the status afterwards.
            await session.Client.CancelTaskAsync(taskId, cancellationToken);
            var after = await session.Client.GetTaskAsync(taskId, cancellationToken);
            ShowResult($"Status now: {TaskWatcher.StatusName(after.Status)}. {after.StatusMessage}", "[yellow]tasks/cancel acknowledged[/]", Color.Yellow);
            return 0;
        }
        catch (McpProtocolException ex)
        {
            ShowResult(ex.Message, "[red]Couldn't cancel[/]", Color.Red);
            return 2;
        }
    }

    private static void ShowResult(string text, string header, Color color) =>
        AnsiConsole.Write(new Panel(Markup.Escape(text)).Header(header).BorderColor(color).Expand());

    private static void ShowSummary(AgentSession session, TaskState? state, Activity? activity)
    {
        var summary = new Table().HideHeaders().Border(TableBorder.Rounded).AddColumn("").AddColumn("");
        summary.AddRow("tools/call requests", $"[bold]{state?.ToolCallRequests ?? session.Stats.ToolCallRequests}[/]" +
                                              (state is { Runs: > 1 } ? $" [grey](across {state.Runs} agent runs)[/]" : ""));
        if (state is not null)
        {
            summary.AddRow("task id", state.TaskId);
            summary.AddRow("tasks/get polls", state.Polls.ToString());
            summary.AddRow("tasks/update requests", state.Updates.ToString());
            summary.AddRow("status transitions", string.Join(" → ", state.Transitions));
        }

        summary.AddRow("questions answered (this run)", session.Stats.QuestionsAnswered.ToString());
        summary.AddRow("trace id", activity?.TraceId.ToString() ?? "(tracing off)");
        AnsiConsole.Write(summary);
    }

    private static string Text(CallToolResult result) =>
        string.Join(Environment.NewLine, result.Content.OfType<TextContentBlock>().Select(c => c.Text));
}

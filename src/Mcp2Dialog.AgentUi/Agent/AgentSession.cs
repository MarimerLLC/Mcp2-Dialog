using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mcp2Dialog.AgentUi.Apps;
using Mcp2Dialog.Telemetry;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Protocol;

namespace Mcp2Dialog.AgentUi.Agent;

/// <summary>
/// One agent: its MCP client, the conversation so far, and the wire log. It's a scoped service, so each browser tab
/// (Blazor circuit) gets its own. The agent is also the MCP Apps host: when a tool names a <c>ui://</c> resource
/// and the MCP Apps option is on, it renders that app next to the conversation and proxies the app's requests.
/// </summary>
public sealed class AgentSession(IOptions<AgentOptions> options, ILoggerFactory loggerFactory) : IAsyncDisposable
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<AgentSession>();
    private readonly Lock _lock = new();
    private readonly List<TranscriptEntry> _transcript = [];

    // The host may cache ui:// templates (they're static; the data comes with each tool result). Cleared on reconnect.
    private readonly Dictionary<string, TextResourceContents> _appResources = [];

    private HttpClient? _http;
    private McpClient? _client;
    private CancellationTokenSource? _run;

    public AgentOptions Options => options.Value;
    public WireLog Wire { get; } = new();
    public DialogStats Stats { get; } = new();

    /// <summary>Answer the server's questions with fixed values (Oslo, 42 kg, overnight, accept the surcharge).</summary>
    public bool AutoAnswer { get; set; }

    /// <summary>
    /// Declare the MCP Apps extension (<c>io.modelcontextprotocol/ui</c>) and render tool UIs. Capabilities are part
    /// of the client, so changing this reconnects before the next call.
    /// </summary>
    public bool UseApps { get; set; }

    public bool IsBusy => _run is not null;
    public IReadOnlyList<McpClientTool> Tools { get; private set; } = [];

    /// <summary>The latest <c>ui/update-model-context</c> from an app. A host with a model would add it to the next turn.</summary>
    public string? ModelContext { get; private set; }

    public event Action? Changed;

    public IReadOnlyList<TranscriptEntry> Transcript
    {
        get
        {
            lock (_lock)
            {
                return [.. _transcript];
            }
        }
    }

    public Task<CallToolResult?> QuoteAsync(IReadOnlyDictionary<string, object?> arguments) =>
        RunToolAsync("get_shipping_quote", arguments, CallOrigin.Agent);

    public async Task ListToolsAsync()
    {
        try
        {
            await EnsureConnectedAsync(CancellationToken.None);
            Add(new ToolsEntry([.. Tools.Select(t => new ToolSummary(
                t.Name, t.Description, UiResourceUri(t.ProtocolTool), Visibility(t.ProtocolTool)))]));
        }
        catch (Exception ex) when (ex is HttpRequestException or McpException)
        {
            Add(new NoteEntry(NoteKind.Error, "Can't reach the MCP server", $"{Options.Server}: {ex.Message}"));
        }
    }

    public void Stop() => _run?.Cancel();

    public void Clear()
    {
        lock (_lock)
        {
            _transcript.Clear();
        }

        ModelContext = null;
        Wire.Clear();
        Changed?.Invoke();
    }

    /// <summary>A tools/call that an MCP App asked its host (this agent) to make.</summary>
    internal Task<CallToolResult?> CallToolForAppAsync(string name, IReadOnlyDictionary<string, object?> arguments) =>
        RunToolAsync(name, arguments, CallOrigin.App);

    internal async Task<ReadResourceResult> ReadResourceForAppAsync(string uri) =>
        await (await EnsureConnectedAsync(CancellationToken.None)).ReadResourceAsync(uri);

    internal void AddAppMessage(string text) =>
        Add(new NoteEntry(NoteKind.AppMessage, "Message from the app (ui/message, role: user)",
            text + Environment.NewLine + "This agent has no model, so nothing answers it. A chat host would add it to the conversation and reply."));

    internal void SetModelContext(string text)
    {
        ModelContext = text;
        Changed?.Invoke();
    }

    /// <summary>
    /// Makes one tools/call. All of the back-and-forth happens inside that single CallToolAsync, driven by the SDK's
    /// MRTR retry loop and <see cref="HandleElicitationAsync"/>.
    /// </summary>
    private async Task<CallToolResult?> RunToolAsync(string toolName, IReadOnlyDictionary<string, object?> arguments, CallOrigin origin)
    {
        if (_run is not null)
        {
            Add(new NoteEntry(NoteKind.Error, "Busy", "Another tool call is still running. Finish or stop it first."));
            return null;
        }

        using var run = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        _run = run;
        Changed?.Invoke();

        McpAppInstance? app = null;
        try
        {
            McpClient client;
            try
            {
                client = await EnsureConnectedAsync(run.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException or McpException)
            {
                Add(new NoteEntry(NoteKind.Error, "Can't reach the MCP server", $"{Options.Server}: {ex.Message}"));
                return null;
            }

            Stats.Reset();
            Add(new CallEntry(origin, toolName, JsonSerializer.Serialize(arguments)));

            // Each dialog gets its own trace. Blazor runs this in the circuit's long-lived WebSocket request, so without
            // this the span would join whatever activity that request has, and every dialog in a tab would share a trace.
            Activity.Current = null;

            // One parent span for the whole dialog: every round's HTTP request, server span, and elicitation nests
            // under it, so the dialog shows up as a single trace.
            using var activity = DialogDiagnostics.ActivitySource.StartActivity($"agent {toolName}", ActivityKind.Internal);
            activity?.SetTag(DialogDiagnostics.Tags.ToolName, toolName);
            activity?.SetTag("mcp2dialog.call.origin", origin.ToString().ToLowerInvariant());

            // An MCP App opens alongside the call, not after it, so it can show progress while the server asks its
            // questions. An app's own calls update that app in place, so they don't open another one.
            var tool = Tools.FirstOrDefault(t => t.Name == toolName);
            if (origin == CallOrigin.Agent && UseApps && tool is not null && UiResourceUri(tool.ProtocolTool) is { } uri)
            {
                app = await LoadAppAsync(client, tool, uri, arguments, run.Token);
            }

            CallToolResult result;
            try
            {
                result = await client.CallToolAsync(toolName, arguments, cancellationToken: run.Token);
            }
            catch (McpException ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                Add(new NoteEntry(NoteKind.Error, "MCP error", ex.Message));
                app?.Cancel(ex.Message);
                return null;
            }
            catch (OperationCanceledException)
            {
                Add(new NoteEntry(NoteKind.Info, "Stopped", $"The {toolName} call was stopped before it finished."));
                app?.Cancel("Stopped by the user.");
                return null;
            }

            activity?.SetTag("mcp2dialog.dialog.tool_call_requests", Stats.ToolCallRequests);
            activity?.SetTag("mcp2dialog.dialog.input_required_results", Stats.InputRequiredResults);

            var text = string.Join(Environment.NewLine, result.Content.OfType<TextContentBlock>().Select(c => c.Text));
            Add(new ResultEntry(origin, text, result.IsError == true, new DialogSummary(
                Stats.ToolCallRequests, Stats.InputRequiredResults, Stats.QuestionsAnswered, activity?.TraceId.ToString())));
            app?.SetResult(result);
            return result;
        }
        finally
        {
            // Nothing can answer a question once its call is over.
            foreach (var entry in Transcript.OfType<QuestionEntry>().Where(q => q.Question.IsOpen))
            {
                entry.Question.Abandon();
            }

            _run = null;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// The SDK calls this once per elicitation in an input_required result (all of a round's questions at once). It
    /// puts a card in the conversation and waits for the user to answer it, unless auto-answer is on.
    /// </summary>
    private async ValueTask<ElicitResult> HandleElicitationAsync(ElicitRequestParams? request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var question = new PendingQuestion(request);

        using var activity = DialogDiagnostics.ActivitySource.StartActivity("elicitation");
        activity?.SetTag(DialogDiagnostics.Tags.DialogId, question.DialogId);
        activity?.SetTag(DialogDiagnostics.Tags.Round, question.Round);

        _logger.LogInformation("Server asked in dialog {DialogId} round {DialogRound}: {Message} {@Fields}",
            question.DialogId, question.Round, request.Message, request.RequestedSchema?.Properties.Keys);

        Add(new QuestionEntry(question));
        using var registration = cancellationToken.Register(question.Abandon);

        if (AutoAnswer)
        {
            // A short pause, so you can see the question before its answer.
            await Task.Delay(TimeSpan.FromMilliseconds(700), cancellationToken);
            question.Complete(question.AutoAnswer(), automatically: true);
        }

        var result = await question.Answer;

        Interlocked.Increment(ref Stats.QuestionsAnswered);
        activity?.SetTag(DialogDiagnostics.Tags.Outcome, result.Action);
        _logger.LogInformation("Answered dialog {DialogId} round {DialogRound} with {Action} {@Content}",
            question.DialogId, question.Round, result.Action, result.Content?.ToDictionary(kv => kv.Key, kv => kv.Value.GetRawText()));
        Changed?.Invoke();

        return result;
    }

    private async Task<McpAppInstance?> LoadAppAsync(
        McpClient client, McpClientTool tool, string uri, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        if (!_appResources.TryGetValue(uri, out var resource))
        {
            var read = await client.ReadResourceAsync(uri, cancellationToken: cancellationToken);
            resource = read.Contents.OfType<TextResourceContents>().FirstOrDefault(c => c.MimeType == McpApps.HtmlMimeType);
            if (resource is null)
            {
                Add(new NoteEntry(NoteKind.Error, "No MCP App",
                    $"{tool.Name} names {uri}, but the server didn't return {McpApps.HtmlMimeType} content for it. Showing text only."));
                return null;
            }

            _appResources[uri] = resource;
        }

        var app = new McpAppInstance(this, tool.ProtocolTool, uri, resource, arguments, loggerFactory.CreateLogger<McpAppInstance>());
        Add(new AppEntry(app));
        return app;
    }

    private async Task<McpClient> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_client is not null && ClientDeclaresApps == UseApps)
        {
            return _client;
        }

        await DisconnectAsync();

        _http = new HttpClient(new WireLoggingHandler(Wire, Stats, loggerFactory.CreateLogger<WireLoggingHandler>())
        {
            InnerHandler = new SocketsHttpHandler(),
        });

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(Options.Server),
                TransportMode = HttpTransportMode.StreamableHttp,
                Name = "mcp2dialog-server",
            },
            _http,
            loggerFactory);

        // The elicitation handler is all the client code MRTR needs. When a tools/call comes back input_required,
        // the SDK calls it once per input request, then retries the same tools/call with inputResponses and the
        // server's requestState, up to 10 times.
        var clientOptions = new McpClientOptions
        {
            ClientInfo = new() { Name = "mcp2dialog-agent", Version = "1.0.0" },
            Handlers = new() { ElicitationHandler = HandleElicitationAsync },
        };

        // MCP Apps is an extension, so the client opts in through the extensions capability. Under 2026-07-28 the SDK
        // sends client capabilities in every request's _meta, so the stateless server sees this on each call.
        if (UseApps)
        {
            clientOptions.Capabilities = new ClientCapabilities
            {
                Extensions = new Dictionary<string, object>
                {
                    [McpApps.ExtensionId] = JsonSerializer.SerializeToElement(
                        new McpUiClientCapabilities { MimeTypes = [McpApps.HtmlMimeType] }, McpApps.SerializerOptions),
                },
            };
        }

        var client = await McpClient.CreateAsync(transport, clientOptions, loggerFactory, cancellationToken);
        _client = client;
        ClientDeclaresApps = UseApps;
        _appResources.Clear();

        // Discover the tools first, as an agent would. This also fills the SDK's tool cache, which it uses to add
        // Mcp-Param-* headers to tools/call, and tells the host which tools have a UI.
        Tools = [.. await client.ListToolsAsync(cancellationToken: cancellationToken)];

        Add(new ConnectedEntry(
            $"{client.ServerInfo.Name} {client.ServerInfo.Version}", client.NegotiatedProtocolVersion ?? "(unknown)",
            client.SessionId, ClientDeclaresApps));
        return client;
    }

    private bool ClientDeclaresApps { get; set; }

    private async Task DisconnectAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
        }

        _http?.Dispose();
        _http = null;
    }

    internal static string? UiResourceUri(Tool tool) =>
        tool.Meta?["ui"]?["resourceUri"] is JsonValue value && value.TryGetValue<string>(out var uri) ? uri : null;

    /// <summary>Who may call the tool: <c>model</c>, <c>app</c>, or both (the default when <c>_meta.ui.visibility</c> is absent).</summary>
    internal static IReadOnlyList<string> Visibility(Tool tool) =>
        tool.Meta?["ui"]?["visibility"] is JsonArray list
            ? [.. list.Select(v => v?.GetValue<string>()).OfType<string>()]
            : [McpUiToolVisibility.Model, McpUiToolVisibility.App];

    private void Add(TranscriptEntry entry)
    {
        lock (_lock)
        {
            _transcript.Add(entry);
        }

        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _run?.Cancel();
        await DisconnectAsync();
    }
}

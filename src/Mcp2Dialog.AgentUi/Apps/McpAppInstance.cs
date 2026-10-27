using System.Text.Json;
using System.Text.Json.Nodes;
using Mcp2Dialog.AgentUi.Agent;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Protocol;

namespace Mcp2Dialog.AgentUi.Apps;

/// <summary>
/// The host side of one rendered MCP App. The app (in its sandboxed iframe) is a JSON-RPC client and the host is its
/// server: it answers <c>ui/initialize</c>, pushes the tool's input and result, and proxies the app's
/// <c>tools/call</c> and <c>resources/read</c> to the MCP server, after checking that the app is allowed to make them.
/// </summary>
public sealed class McpAppInstance
{
    /// <summary>The MCP Apps protocol version this host speaks on the postMessage channel.</summary>
    public const string ProtocolVersion = "2026-01-26";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly AgentSession _session;
    private readonly Tool _tool;
    private readonly JsonObject? _ui;
    private readonly IReadOnlyDictionary<string, object?> _arguments;
    private readonly ILogger<McpAppInstance> _logger;

    private Func<string, Task>? _send;
    private bool _initialized;
    private bool _inputSent;
    private bool _outcomeSent;
    private string? _cancelReason;
    private int _nextRequestId;

    internal McpAppInstance(AgentSession session, Tool tool, string resourceUri, TextResourceContents resource,
        IReadOnlyDictionary<string, object?> arguments, ILogger<McpAppInstance> logger)
    {
        _session = session;
        _tool = tool;
        _ui = resource.Meta?["ui"] as JsonObject;
        _arguments = arguments;
        _logger = logger;
        ResourceUri = resourceUri;
        Html = resource.Text;
    }

    public string ToolName => _tool.Name;
    public string ResourceUri { get; }
    public string Html { get; }
    public CallToolResult? Result { get; private set; }

    /// <summary>The resource's <c>_meta.ui.csp</c>. The sandbox proxy turns it into its CSP header.</summary>
    public string CspJson => _ui?["csp"]?.ToJsonString() ?? "{}";

    public string PermissionsJson => _ui?["permissions"]?.ToJsonString() ?? "{}";

    public bool PrefersBorder => _ui?["prefersBorder"] is JsonValue value && value.TryGetValue<bool>(out var border) && border;

    /// <summary>Connects the instance to its iframe. <paramref name="send"/> posts one JSON-RPC message to the app.</summary>
    public void Attach(Func<string, Task> send) => _send = send;

    public void Detach() => _send = null;

    internal void SetResult(CallToolResult result)
    {
        Result = result;
        _ = SendPendingAsync();
    }

    internal void Cancel(string reason)
    {
        _cancelReason = reason;
        _ = SendPendingAsync();
    }

    /// <summary>Asks the app to finish up before the host removes it. The host doesn't wait for the answer.</summary>
    public Task TeardownAsync() =>
        SendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = $"host-{Interlocked.Increment(ref _nextRequestId)}",
            ["method"] = "ui/resource-teardown",
            ["params"] = new JsonObject { ["reason"] = "The agent page is closing." },
        }, "ui/resource-teardown");

    /// <summary>Handles one message from the app. Returns the JSON-RPC response for a request, or null.</summary>
    public async Task<string?> HandleMessageAsync(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject message)
        {
            return null;
        }

        var method = message["method"]?.GetValue<string>();
        var id = message["id"]?.DeepClone();
        _session.Wire.Add(WireChannel.PostMessage, WireKind.AppToHost,
            $"app → host  {method ?? "response"}{(id is null ? "" : $"  (id {id.ToJsonString()})")}", [], message.ToJsonString(Indented));

        if (method is null)
        {
            // A response to the host's own request, such as ui/resource-teardown.
            return null;
        }

        if (id is null)
        {
            await HandleNotificationAsync(method, message["params"]);
            return null;
        }

        JsonObject response;
        try
        {
            response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = await HandleRequestAsync(method, message["params"] as JsonObject) };
        }
        catch (AppRequestException ex)
        {
            response = Error(id, ex.Code, ex.Message);
        }
        catch (McpException ex)
        {
            response = Error(id, -32603, ex.Message);
        }

        var responseJson = response.ToJsonString(Indented);
        _session.Wire.Add(WireChannel.PostMessage, WireKind.HostToApp,
            $"host → app  response to {method}{(response["error"] is null ? "" : "  (error)")}", [], Abbreviate(responseJson));
        return response.ToJsonString();
    }

    private async Task<JsonNode> HandleRequestAsync(string method, JsonObject? parameters)
    {
        switch (method)
        {
            case "ui/initialize":
                return new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersion,
                    ["hostInfo"] = new JsonObject { ["name"] = "mcp2dialog-agent", ["version"] = "1.0.0" },
                    ["hostCapabilities"] = new JsonObject
                    {
                        ["serverTools"] = new JsonObject(),
                        ["serverResources"] = new JsonObject(),
                        ["logging"] = new JsonObject(),
                        ["sandbox"] = new JsonObject { ["csp"] = JsonNode.Parse(CspJson) },
                    },
                    ["hostContext"] = new JsonObject
                    {
                        ["toolInfo"] = new JsonObject { ["tool"] = JsonSerializer.SerializeToNode(_tool, McpJsonUtilities.DefaultOptions) },
                        ["theme"] = "light",
                        ["displayMode"] = "inline",
                        ["availableDisplayModes"] = new JsonArray("inline"),
                        ["containerDimensions"] = new JsonObject { ["maxHeight"] = 900 },
                        ["platform"] = "web",
                        ["locale"] = "en-US",
                        ["userAgent"] = "mcp2dialog-agent/1.0.0",
                        ["styles"] = new JsonObject
                        {
                            ["variables"] = new JsonObject
                            {
                                ["--color-background-primary"] = "#ffffff",
                                ["--color-background-secondary"] = "#f3f0fd",
                                ["--color-text-primary"] = "#1b1f27",
                                ["--color-text-secondary"] = "#5b6475",
                                ["--color-border-primary"] = "#dcd6f5",
                                ["--font-sans"] = "\"Segoe UI\", system-ui, sans-serif",
                            },
                        },
                    },
                };

            case "tools/call":
                return await CallToolAsync(parameters);

            case "resources/read":
                var uri = parameters?["uri"]?.GetValue<string>() ?? throw new AppRequestException(-32602, "resources/read needs a uri.");
                return JsonSerializer.SerializeToNode(await _session.ReadResourceForAppAsync(uri), McpJsonUtilities.DefaultOptions)!;

            case "ui/message":
                _session.AddAppMessage(Text(parameters?["content"]));
                return new JsonObject();

            case "ui/update-model-context":
                _session.SetModelContext(Text(parameters?["content"]));
                return new JsonObject();

            case "ui/request-display-mode":
                // This host only shows apps inline, and returns the mode the app actually gets.
                return new JsonObject { ["mode"] = "inline" };

            case "ping":
                return new JsonObject();

            default:
                throw new AppRequestException(-32601, $"This host doesn't support {method}.");
        }
    }

    /// <summary>
    /// Proxies an app's tools/call. The host decides: the tool must exist on this app's server and its
    /// <c>_meta.ui.visibility</c> must include <c>app</c>. The call then runs like any other, so if the server needs
    /// more information, its questions appear in the conversation, not in the app.
    /// </summary>
    private async Task<JsonNode> CallToolAsync(JsonObject? parameters)
    {
        var name = parameters?["name"]?.GetValue<string>() ?? throw new AppRequestException(-32602, "tools/call needs a tool name.");
        var tool = _session.Tools.FirstOrDefault(t => t.Name == name)
            ?? throw new AppRequestException(-32602, $"The server has no tool named {name}.");

        if (!AgentSession.Visibility(tool.ProtocolTool).Contains(McpUiToolVisibility.App))
        {
            throw new AppRequestException(-32000, $"{name} isn't visible to apps (its _meta.ui.visibility leaves out \"app\").");
        }

        if (_session.IsBusy)
        {
            throw new AppRequestException(-32000, "The agent is busy with another tool call.");
        }

        var arguments = (parameters?["arguments"] as JsonObject ?? new JsonObject())
            .ToDictionary(kv => kv.Key, kv => (object?)(kv.Value is null ? null : JsonSerializer.SerializeToElement(kv.Value)));

        var result = await _session.CallToolForAppAsync(name, arguments)
            ?? throw new AppRequestException(-32000, "The tool call failed or was stopped. The agent's conversation has the details.");
        return JsonSerializer.SerializeToNode(result, McpJsonUtilities.DefaultOptions)!;
    }

    private async Task HandleNotificationAsync(string method, JsonNode? parameters)
    {
        switch (method)
        {
            case "ui/notifications/initialized":
                // The host must not send anything to the app before this.
                _initialized = true;
                await SendPendingAsync();
                break;

            case "notifications/message":
                _logger.LogInformation("App {ResourceUri} logged {Data}", ResourceUri, parameters?.ToJsonString());
                break;

            // ui/notifications/size-changed is handled in the browser, which resizes the iframe.
        }
    }

    /// <summary>Sends the tool's input once the app is ready, then its result (or cancellation) once there is one.</summary>
    private async Task SendPendingAsync()
    {
        if (!_initialized)
        {
            return;
        }

        if (!_inputSent)
        {
            _inputSent = true;
            await NotifyAsync("ui/notifications/tool-input", new JsonObject { ["arguments"] = JsonSerializer.SerializeToNode(_arguments) });
        }

        if (_outcomeSent)
        {
            return;
        }

        if (Result is not null)
        {
            _outcomeSent = true;
            await NotifyAsync("ui/notifications/tool-result", JsonSerializer.SerializeToNode(Result, McpJsonUtilities.DefaultOptions));
        }
        else if (_cancelReason is not null)
        {
            _outcomeSent = true;
            await NotifyAsync("ui/notifications/tool-cancelled", new JsonObject { ["reason"] = _cancelReason });
        }
    }

    private Task NotifyAsync(string method, JsonNode? parameters) =>
        SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters }, method);

    private async Task SendAsync(JsonObject message, string method)
    {
        _session.Wire.Add(WireChannel.PostMessage, WireKind.HostToApp, $"host → app  {method}", [], Abbreviate(message.ToJsonString(Indented)));
        if (_send is { } send)
        {
            try
            {
                await send(message.ToJsonString());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Couldn't post {Method} to the app {ResourceUri}", method, ResourceUri);
            }
        }
    }

    /// <summary>The text of a ContentBlock or a list of them.</summary>
    private static string Text(JsonNode? content) => content switch
    {
        JsonArray blocks => string.Join(Environment.NewLine, blocks.Select(Text)),
        JsonObject block => block["text"]?.GetValue<string>() ?? block.ToJsonString(),
        _ => "",
    };

    private static JsonObject Error(JsonNode id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private static string Abbreviate(string json) => json.Length <= 4000 ? json : json[..4000] + $"\n... ({json.Length} chars)";

    private sealed class AppRequestException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}

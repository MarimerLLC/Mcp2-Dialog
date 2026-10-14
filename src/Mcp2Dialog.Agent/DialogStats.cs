namespace Mcp2Dialog.Agent;

/// <summary>Counts what happened during one command so the agent can show a summary.</summary>
internal sealed class DialogStats
{
    public int ToolCallRequests;
    public int InputRequiredResults;
    public int QuestionsAnswered;

    /// <summary>Every JSON-RPC POST, in order, with what the headers said about it.</summary>
    public List<Hop> Hops { get; } = [];
}

/// <summary>One HTTP request and its response, as seen by the agent.</summary>
/// <param name="Method">The JSON-RPC method.</param>
/// <param name="Round">For tools/call, which round of the dialog this was.</param>
/// <param name="ServedBy">The X-Served-By response header, if the server sent one.</param>
/// <param name="ProtocolVersion">The MCP-Protocol-Version request header.</param>
/// <param name="SessionIdSent">Whether the request carried Mcp-Session-Id.</param>
/// <param name="SessionIdReceived">Whether the response carried Mcp-Session-Id.</param>
internal sealed record Hop(
    string Method,
    int? Round,
    string? ServedBy,
    int StatusCode,
    string? ResultType,
    string? ProtocolVersion,
    bool SessionIdSent,
    bool SessionIdReceived);

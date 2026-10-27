using Mcp2Dialog.AgentUi.Apps;

namespace Mcp2Dialog.AgentUi.Agent;

/// <summary>Who started a tool call: the agent itself, or an MCP App through the agent (its host).</summary>
public enum CallOrigin
{
    Agent,
    App,
}

/// <summary>One item in the conversation column.</summary>
public abstract record TranscriptEntry
{
    public DateTimeOffset At { get; } = DateTimeOffset.Now;
}

public sealed record ConnectedEntry(string Server, string Protocol, string? SessionId, bool AppsDeclared) : TranscriptEntry;

public sealed record ToolsEntry(IReadOnlyList<ToolSummary> Tools) : TranscriptEntry;

public sealed record ToolSummary(string Name, string? Description, string? UiResourceUri, IReadOnlyList<string> Visibility);

public sealed record CallEntry(CallOrigin Origin, string Tool, string Arguments) : TranscriptEntry;

public sealed record QuestionEntry(PendingQuestion Question) : TranscriptEntry;

public sealed record AppEntry(McpAppInstance App) : TranscriptEntry;

public sealed record ResultEntry(CallOrigin Origin, string Text, bool IsError, DialogSummary Summary) : TranscriptEntry;

public sealed record DialogSummary(int ToolCallRequests, int InputRequiredResults, int QuestionsAnswered, string? TraceId);

/// <summary>Something the agent wants to point out, such as a ui/message from an app or an error.</summary>
public sealed record NoteEntry(NoteKind Kind, string Title, string Text) : TranscriptEntry;

public enum NoteKind
{
    Info,
    Error,
    AppMessage,
    ModelContext,
}

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Mcp2Dialog.Telemetry;

/// <summary>
/// Names shared by the agent and the server so their telemetry lines up in one trace.
/// </summary>
public static class DialogDiagnostics
{
    /// <summary>ActivitySource and Meter name used by this sample's own spans and metrics.</summary>
    public const string SourceName = "Mcp2Dialog";

    /// <summary>ActivitySource and Meter name used by the MCP C# SDK.</summary>
    public const string McpSdkSourceName = "Experimental.ModelContextProtocol";

    public static readonly ActivitySource ActivitySource = new(SourceName);
    public static readonly Meter Meter = new(SourceName);

    /// <summary>Span and log property names for the dialog.</summary>
    public static class Tags
    {
        public const string DialogId = "mcp2dialog.dialog.id";
        public const string Round = "mcp2dialog.dialog.round";
        public const string InputKeys = "mcp2dialog.dialog.input_keys";
        public const string Outcome = "mcp2dialog.dialog.outcome";
        public const string ToolName = "mcp2dialog.tool.name";

        // Tasks extension demo (book_freight)
        public const string TaskId = "mcp2dialog.task.id";
        public const string TaskStatus = "mcp2dialog.task.status";
        public const string TaskStage = "mcp2dialog.task.stage";
        public const string TaskResumed = "mcp2dialog.task.resumed";
    }

    /// <summary>
    /// Keys the server puts in each elicitation's <c>_meta</c>. They give the client a readable copy of the
    /// dialog id and round for display and logging. The authoritative copy travels in the encrypted <c>requestState</c>.
    /// </summary>
    public static class MetaKeys
    {
        public const string DialogId = "mcp2dialog/dialogId";
        public const string Round = "mcp2dialog/round";
    }
}

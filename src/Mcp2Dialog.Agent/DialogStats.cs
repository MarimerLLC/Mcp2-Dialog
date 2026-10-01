namespace Mcp2Dialog.Agent;

/// <summary>Counts what happened during one command so the agent can show a summary.</summary>
internal sealed class DialogStats
{
    public int ToolCallRequests;
    public int InputRequiredResults;
    public int QuestionsAnswered;
}

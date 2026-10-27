namespace Mcp2Dialog.AgentUi.Agent;

/// <summary>Counts what happened during one tool call so the agent can show a summary.</summary>
public sealed class DialogStats
{
    public int ToolCallRequests;
    public int InputRequiredResults;
    public int QuestionsAnswered;

    public void Reset()
    {
        ToolCallRequests = 0;
        InputRequiredResults = 0;
        QuestionsAnswered = 0;
    }
}

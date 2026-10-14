namespace Mcp2Dialog.Agent;

/// <summary>Counts what happened during one command so the agent can show a summary.</summary>
internal sealed class DialogStats
{
    public int ToolCallRequests;
    public int InputRequiredResults;
    public int QuestionsAnswered;

    /// <summary>The last tasks/get answer shown by --wire, so unchanged polls can be shown as one line.</summary>
    public string? LastTaskPoll;
    public bool TaskPollRequestShown;
}

namespace Mcp2Dialog.AgentUi;

/// <summary>The <c>Agent</c> configuration section.</summary>
public sealed class AgentOptions
{
    /// <summary>MCP endpoint of the server.</summary>
    public string Server { get; set; } = "http://localhost:5499/mcp";

    /// <summary>
    /// Base URL of the MCP Apps sandbox proxy. It must be a different origin from the agent's own pages (here, a
    /// different port), so an app's HTML can never touch the agent's page, cookies, or Blazor circuit.
    /// </summary>
    public string SandboxUrl { get; set; } = "http://localhost:5497";

    /// <summary>The Aspire dashboard, for the "open trace" links.</summary>
    public string DashboardUrl { get; set; } = "http://localhost:18918";
}

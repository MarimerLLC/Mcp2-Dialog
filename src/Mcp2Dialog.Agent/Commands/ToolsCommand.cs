using Spectre.Console;
using Spectre.Console.Cli;

namespace Mcp2Dialog.Agent.Commands;

internal sealed class ToolsCommand : AsyncCommand<AgentSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, AgentSettings settings, CancellationToken cancellationToken)
    {
        await using var session = await AgentSession.StartAsync(settings, autoAnswer: true, cancellationToken);
        session.RenderConnection();

        var tools = await session.Client.ListToolsAsync(cancellationToken: cancellationToken);

        var table = new Table().Border(TableBorder.Rounded).AddColumn("Tool").AddColumn("Description");
        foreach (var tool in tools)
        {
            table.AddRow(Markup.Escape(tool.Name), Markup.Escape(tool.Description ?? ""));
        }

        AnsiConsole.Write(table);
        return 0;
    }
}

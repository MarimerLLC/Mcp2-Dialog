using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Mcp2Dialog.Telemetry;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Mcp2Dialog.Agent.Commands;

internal sealed class QuoteSettings : AgentSettings
{
    [CommandOption("-d|--destination <CITY>")]
    [Description("Destination city. Leave it out and the server will ask.")]
    public string? Destination { get; set; }

    [CommandOption("-w|--weight <KG>")]
    [Description("Package weight in kg. Leave it out and the server will ask.")]
    public double? WeightKg { get; set; }

    [CommandOption("-s|--speed <SPEED>")]
    [Description("standard, express, or overnight. Leave it out and the server will ask.")]
    public string? Speed { get; set; }

    [CommandOption("--auto")]
    [Description("Answer the server's questions automatically (Oslo, 42 kg, overnight, accept surcharge).")]
    public bool Auto { get; set; }
}

/// <summary>
/// Makes one tools/call. All of the back-and-forth happens inside that single CallToolAsync, driven by the
/// SDK's MRTR retry loop and the elicitation handler.
/// </summary>
internal sealed class QuoteCommand : AsyncCommand<QuoteSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, QuoteSettings settings, CancellationToken cancellationToken)
    {
        await using var session = await AgentSession.StartAsync(settings, settings.Auto, cancellationToken);
        session.RenderConnection();

        // Discover the tool first, as an agent would. This also fills the SDK's tool cache, which it uses to add
        // Mcp-Param-* headers to tools/call.
        var tools = await session.Client.ListToolsAsync(cancellationToken: cancellationToken);
        if (tools.All(t => t.Name != "get_shipping_quote"))
        {
            AnsiConsole.MarkupLine("[red]The server doesn't expose get_shipping_quote.[/]");
            return 1;
        }

        var arguments = new Dictionary<string, object?>();
        if (settings.Destination is not null) arguments["destination"] = settings.Destination;
        if (settings.WeightKg is not null) arguments["weightKg"] = settings.WeightKg;
        if (settings.Speed is not null) arguments["speed"] = settings.Speed;

        AnsiConsole.MarkupLine($"[bold]Agent →[/] get_shipping_quote {Markup.Escape(JsonSerializer.Serialize(arguments))}");

        // One parent span for the whole dialog: every round's HTTP request, server span, and elicitation
        // nests under it, so the dialog shows up as a single trace.
        using var activity = DialogDiagnostics.ActivitySource.StartActivity("agent get_shipping_quote", ActivityKind.Internal);
        activity?.SetTag(DialogDiagnostics.Tags.ToolName, "get_shipping_quote");

        CallToolResult result;
        try
        {
            result = await session.Client.CallToolAsync("get_shipping_quote", arguments, cancellationToken: cancellationToken);
        }
        catch (McpException ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            AnsiConsole.Write(new Panel(Markup.Escape(ex.Message)).Header("[red]MCP error[/]").BorderColor(Color.Red));
            return 1;
        }

        var stats = session.Stats;
        activity?.SetTag("mcp2dialog.dialog.tool_call_requests", stats.ToolCallRequests);
        activity?.SetTag("mcp2dialog.dialog.input_required_results", stats.InputRequiredResults);

        var text = string.Join(Environment.NewLine, result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        AnsiConsole.Write(new Panel(Markup.Escape(text))
            .Header(result.IsError == true ? "[red]Server error[/]" : "[green]Server answer[/]")
            .BorderColor(result.IsError == true ? Color.Red : Color.Green));

        var summary = new Table().HideHeaders().Border(TableBorder.Rounded).AddColumn("").AddColumn("");
        summary.AddRow("tools/call HTTP requests", stats.ToolCallRequests.ToString());
        summary.AddRow("input_required results", stats.InputRequiredResults.ToString());
        summary.AddRow("questions answered", stats.QuestionsAnswered.ToString());
        summary.AddRow("trace id", activity?.TraceId.ToString() ?? "(tracing off)");
        AnsiConsole.Write(summary);

        return result.IsError == true ? 1 : 0;
    }
}

using Mcp2Dialog.Agent.Commands;
using Spectre.Console.Cli;

var app = new CommandApp<QuoteCommand>();
app.Configure(config =>
{
    config.SetApplicationName("mcp2dialog-agent");

    config.AddCommand<QuoteCommand>("quote")
        .WithDescription("Ask the MCP server for a shipping quote. The server asks for whatever it's missing.")
        .WithExample("quote")
        .WithExample("quote", "--destination", "Oslo")
        .WithExample("quote", "--auto", "--wire");

    config.AddCommand<ToolsCommand>("tools")
        .WithDescription("List the tools the MCP server exposes.");
});

return await app.RunAsync(args);

using System.ComponentModel;
using Spectre.Console.Cli;

namespace Mcp2Dialog.Agent;

internal class AgentSettings : CommandSettings
{
    [CommandOption("--server <URL>")]
    [Description("MCP endpoint of the server.")]
    [DefaultValue("http://localhost:5399/mcp")]
    public string Server { get; set; } = "http://localhost:5399/mcp";

    [CommandOption("--wire")]
    [Description("Show every JSON-RPC message sent and received over HTTP.")]
    public bool Wire { get; set; }

    [CommandOption("-v|--verbose")]
    [Description("Write Debug-level logs (including SDK logs) to the console.")]
    public bool Verbose { get; set; }

    [CommandOption("--no-otlp")]
    [Description("Don't export logs, traces, and metrics over OTLP.")]
    public bool NoOtlp { get; set; }
}

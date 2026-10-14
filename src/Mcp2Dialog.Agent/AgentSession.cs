using Mcp2Dialog.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using OpenTelemetry.Trace;
using Serilog.Core;
using Serilog.Events;
using Spectre.Console;

namespace Mcp2Dialog.Agent;

/// <summary>
/// The agent's host (Serilog, OpenTelemetry, HttpClient pipeline) and its connected MCP client.
/// </summary>
internal sealed class AgentSession : IAsyncDisposable
{
    private readonly IHost _host;

    private AgentSession(IHost host, McpClient client)
    {
        _host = host;
        Client = client;
    }

    public McpClient Client { get; }
    public DialogStats Stats => _host.Services.GetRequiredService<DialogStats>();
    public ElicitationPrompter Prompter => _host.Services.GetRequiredService<ElicitationPrompter>();

    /// <param name="compactTaskPolls">With --wire, collapse tasks/get polls whose status didn't change into one line.</param>
    public static async Task<AgentSession> StartAsync(
        AgentSettings settings, bool autoAnswer, CancellationToken cancellationToken, bool compactTaskPolls = false)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory,
        });

        if (settings.NoOtlp)
        {
            builder.Configuration["Otlp:Enabled"] = "false";
        }

        // Spectre owns the console, so only warnings reach it unless --verbose is set. OTLP still gets everything.
        var consoleLevel = new LoggingLevelSwitch(settings.Verbose ? LogEventLevel.Debug : LogEventLevel.Warning);
        builder.AddDialogTelemetry("mcp2dialog-agent", consoleLevel);

        builder.Services.AddSingleton<DialogStats>();
        builder.Services.AddSingleton(new WireOptions(settings.Wire, compactTaskPolls));
        builder.Services.AddTransient<WireLoggingHandler>();
        builder.Services.AddHttpClient("mcp").AddHttpMessageHandler<WireLoggingHandler>();
        builder.Services.AddSingleton(sp => new ElicitationPrompter(
            autoAnswer, sp.GetRequiredService<DialogStats>(), sp.GetRequiredService<ILogger<ElicitationPrompter>>()));

        var host = builder.Build();
        await host.StartAsync(cancellationToken);

        try
        {
            var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
            var httpClient = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("mcp");
            var prompter = host.Services.GetRequiredService<ElicitationPrompter>();

            var transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(settings.Server),
                    TransportMode = HttpTransportMode.StreamableHttp,
                    Name = "mcp2dialog-server",
                },
                httpClient,
                loggerFactory);

            // The elicitation handler is all the client code MRTR needs. When a tools/call comes back
            // input_required, the SDK calls this handler once per input request. It then retries the same
            // tools/call with inputResponses and the server's requestState, up to 10 times.
            var client = await McpClient.CreateAsync(
                transport,
                new McpClientOptions
                {
                    ClientInfo = new() { Name = "mcp2dialog-agent", Version = "1.0.0" },
                    Handlers = new() { ElicitationHandler = prompter.HandleAsync },
                },
                loggerFactory,
                cancellationToken);

            return new AgentSession(host, client);
        }
        catch
        {
            await host.StopAsync(CancellationToken.None);
            host.Dispose();
            throw;
        }
    }

    /// <summary>Exports any buffered spans now instead of waiting for the next batch.</summary>
    public void FlushTelemetry() => _host.Services.GetService<TracerProvider>()?.ForceFlush(2000);

    public void RenderConnection()
    {
        var grid = new Grid().AddColumn(new GridColumn().NoWrap()).AddColumn();
        grid.AddRow("[grey]Server[/]", Markup.Escape($"{Client.ServerInfo.Name} {Client.ServerInfo.Version}"));
        grid.AddRow("[grey]Protocol[/]", Markup.Escape(Client.NegotiatedProtocolVersion ?? "(unknown)"));
        grid.AddRow("[grey]Session id[/]", Client.SessionId is { } id
            ? Markup.Escape(id)
            : "[grey](none: stateless, so requestState correlates the rounds)[/]");
        AnsiConsole.Write(new Panel(grid).Header("Connected").BorderColor(Color.Grey));
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();

        // Stopping and disposing the host flushes the OTLP exporters and Serilog sinks.
        await _host.StopAsync(CancellationToken.None);
        _host.Dispose();
    }
}

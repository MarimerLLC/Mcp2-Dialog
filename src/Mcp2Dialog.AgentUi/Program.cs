using Mcp2Dialog.AgentUi;
using Mcp2Dialog.AgentUi.Agent;
using Mcp2Dialog.AgentUi.Apps;
using Mcp2Dialog.AgentUi.Components;
using Mcp2Dialog.Telemetry;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// No ASP.NET Core instrumentation: the agent's traces are its MCP dialogs, not the Blazor circuit's HTTP traffic.
builder.AddDialogTelemetry("mcp2dialog-agent");

builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// One agent (MCP client, conversation, wire log) per browser tab: scoped services live as long as the Blazor circuit.
builder.Services.AddScoped<AgentSession>();

var app = builder.Build();

// The sandbox origin (its own port) serves the MCP Apps sandbox proxy and nothing else. This runs first, so none
// of the agent's pages, static files, or Blazor endpoints are reachable from that origin.
app.UseMcpAppsSandbox(new Uri(app.Services.GetRequiredService<IOptions<AgentOptions>>().Value.SandboxUrl));

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

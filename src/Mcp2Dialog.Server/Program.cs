using Microsoft.AspNetCore.DataProtection;
using Mcp2Dialog.Server;
using Mcp2Dialog.Telemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.AddDialogTelemetry(
    "mcp2dialog-server",
    configureTracing: tracing => tracing.AddAspNetCoreInstrumentation(),
    configureMetrics: metrics => metrics.AddAspNetCoreInstrumentation());

// The server keeps no state between rounds. The dialog state goes to the client inside an encrypted,
// time-limited requestState token, and the client sends it back on the retry.
builder.Services.AddDataProtection().SetApplicationName("Mcp2Dialog");
builder.Services.AddSingleton<DialogStateProtector>();
builder.Services.AddSingleton<ShippingRates>();

builder.Services
    .AddMcpServer(options => options.ServerInfo = new() { Name = "mcp2dialog-server", Version = "1.0.0" })
    // Streamable HTTP is stateless by default in SDK 2.x. Clients that negotiate 2026-07-28 get native MRTR,
    // and there is no Mcp-Session-Id: requestState is what correlates the rounds of a dialog.
    .WithHttpTransport()
    .WithTools<ShippingTools>();

var app = builder.Build();

app.UseSerilogRequestLogging();
app.MapMcp("/mcp");

app.Run();

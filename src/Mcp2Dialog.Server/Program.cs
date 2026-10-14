using Mcp2Dialog.Server;
using Mcp2Dialog.Telemetry;
using ModelContextProtocol.Protocol;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

var instance = builder.Configuration.GetSection(ServerInstance.Section).Get<ServerInstance>() ?? new();
builder.Services.AddSingleton(instance);

builder.AddDialogTelemetry(
    "mcp2dialog-server",
    configureTracing: tracing => tracing.AddAspNetCoreInstrumentation(),
    configureMetrics: metrics => metrics.AddAspNetCoreInstrumentation(),
    configureLogging: instance.Name is { } name ? logging => logging.Enrich.WithProperty("Instance", name) : null,
    consoleTemplate: instance.Name is null ? TelemetryExtensions.ConsoleTemplate : ServerInstanceExtensions.ConsoleTemplate);

// The server keeps no state between rounds. The dialog state goes to the client inside an encrypted,
// time-limited requestState token, and the client sends it back on the retry.
builder.Services.AddDialogStateKeys(builder.Configuration, builder.Environment, instance);
builder.Services.AddSingleton<DialogStateProtector>();
builder.Services.AddSingleton<ShippingRates>();

var mcp = builder.Services
    .AddMcpServer(options => options.ServerInfo = new() { Name = "mcp2dialog-server", Version = "1.0.0" })
    // Streamable HTTP is stateless by default in SDK 2.x. Clients that negotiate 2026-07-28 get native MRTR,
    // and there is no Mcp-Session-Id: requestState is what correlates the rounds of a dialog.
    .WithHttpTransport()
    .WithTools<ShippingTools>();

// Optional caching hint: the tool list is the same for every caller, so let clients and gateways reuse it.
// The SDK already sends ttlMs/cacheScope (0 and private) on tools/list; this filter sets real values.
if (builder.Configuration.GetValue<TimeSpan?>("Caching:ToolsListTtl") is { } toolsListTtl)
{
    mcp.WithRequestFilters(filters => filters.AddListToolsFilter(next => async (context, cancellationToken) =>
    {
        var result = await next(context, cancellationToken);
        result.TimeToLive = toolsListTtl;
        result.CacheScope = CacheScope.Public;
        return result;
    }));
}

var app = builder.Build();

app.UseInstanceStamp(instance);
app.UseSerilogRequestLogging();
app.MapMcp("/mcp");

app.Run();

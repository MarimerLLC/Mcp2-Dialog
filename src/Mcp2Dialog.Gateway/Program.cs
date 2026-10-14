using Mcp2Dialog.Gateway;
using Mcp2Dialog.Telemetry;
using OpenTelemetry.Trace;
using Yarp.ReverseProxy.Health;

var builder = WebApplication.CreateBuilder(args);

builder.AddDialogTelemetry(
    "mcp2dialog-gateway",
    configureTracing: tracing => tracing.AddAspNetCoreInstrumentation().AddSource("Yarp.ReverseProxy"),
    consoleTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}");

// A plain reverse proxy. Routes and the cluster (round robin, no session affinity) are in appsettings.json.
builder.Services.AddReverseProxy().LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));
builder.Services.AddSingleton<IPassiveHealthCheckPolicy, ConnectFailureHealthPolicy>();
builder.Services.AddToolRateLimit(builder.Configuration);

var retryOnConnectFailure = builder.Configuration.GetValue("Gateway:RetryOnConnectFailure", true);

var app = builder.Build();

app.UseRateLimiter();

// The proxy pipeline. It leaves out YARP's session affinity step, so nothing pins a client to an instance.
app.MapReverseProxy(proxy =>
{
    proxy.UseMiddleware<McpRequestLog>();
    if (retryOnConnectFailure)
    {
        proxy.UseMiddleware<RetryOnConnectFailure>();
    }

    proxy.UseLoadBalancing();
    proxy.UsePassiveHealthChecks();
});

app.Run();

using System.Diagnostics;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

namespace Mcp2Dialog.Gateway;

/// <summary>
/// One line per proxied request: what was asked (from the Mcp-Method and Mcp-Name headers), which instance the
/// load balancer picked, and the status. Header-only: nothing here touches the request or response body.
/// </summary>
internal sealed class McpRequestLog(RequestDelegate next, ILogger<McpRequestLog> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        await next(context);

        var destination = context.GetReverseProxyFeature().ProxiedDestination?.DestinationId ?? "(none)";
        var note = context.Items[RetryOnConnectFailure.RefusedByKey] is string refusedBy
            ? $"  (retried: {refusedBy} refused the connection)"
            : "";

        // YARP's own message for a failed forward replaces its multi-line stack trace in the console (see appsettings.json).
        if (context.GetForwarderErrorFeature() is { } error)
        {
            note += $"  (proxy error {error.Error}: {error.Exception?.Message})";
        }

        logger.Log(note.Contains("proxy error") ? LogLevel.Warning : LogLevel.Information,
            "{McpMethod} {McpName} → {Destination} {StatusCode} in {ElapsedMs:0} ms{Note}",
            McpHeaders.MethodOf(context.Request), McpHeaders.NameOf(context.Request), destination,
            context.Response.StatusCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds, note);
    }
}

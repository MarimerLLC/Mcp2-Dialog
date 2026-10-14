using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Health;
using Yarp.ReverseProxy.Model;

namespace Mcp2Dialog.Gateway;

/// <summary>
/// Hand-rolled: YARP has no built-in retry. When the chosen instance refuses the connection, the request was
/// never sent (YARP hadn't started on the body), so it's safe to send the same POST to another instance, even
/// though tools/call isn't idempotent. Any other failure (a reset mid-request, a timeout) is passed through.
/// </summary>
internal sealed class RetryOnConnectFailure(RequestDelegate next, ILogger<RetryOnConnectFailure> logger)
{
    public const string RefusedByKey = "mcp2dialog.refused-by";

    public async Task InvokeAsync(HttpContext context)
    {
        var proxy = context.GetReverseProxyFeature();
        var candidates = proxy.AvailableDestinations;

        while (true)
        {
            await next(context);

            if (proxy.ProxiedDestination is not { } refused || !ConnectFailure.Happened(context) || context.Response.HasStarted)
            {
                return;
            }

            candidates = candidates.Where(d => d != refused).ToList();
            if (candidates.Count == 0)
            {
                return;
            }

            logger.LogWarning("{Destination} refused the connection, so {McpMethod} {McpName} was never sent. Trying another instance.",
                refused.DestinationId, McpHeaders.MethodOf(context.Request), McpHeaders.NameOf(context.Request));
            context.Items[RefusedByKey] = refused.DestinationId;

            // Undo the 502 YARP prepared and let load balancing pick again from the instances that are left.
            context.Response.Clear();
            context.Features.Set<IForwarderErrorFeature>(null);
            proxy.ProxiedDestination = null;
            proxy.AvailableDestinations = candidates;
        }
    }
}

/// <summary>
/// Passive health check: an instance that refuses a connection is taken out of rotation for the cluster's
/// ReactivationPeriod, so only the first request after a crash pays for the failed connect. YARP's built-in
/// TransportFailureRate policy needs at least 10 requests in its window before it acts, which a demo never reaches.
/// </summary>
internal sealed class ConnectFailureHealthPolicy(IDestinationHealthUpdater healthUpdater) : IPassiveHealthCheckPolicy
{
    public const string PolicyName = "ConnectFailure";

    public string Name => PolicyName;

    public void RequestProxied(HttpContext context, ClusterState cluster, DestinationState destination)
    {
        if (ConnectFailure.Happened(context))
        {
            var reactivation = cluster.Model.Config.HealthCheck?.Passive?.ReactivationPeriod ?? TimeSpan.FromSeconds(10);
            healthUpdater.SetPassive(cluster, destination, DestinationHealth.Unhealthy, reactivation);
        }
    }
}

internal static class ConnectFailure
{
    /// <summary>True when YARP couldn't connect to the destination at all (for example, connection refused).</summary>
    public static bool Happened(HttpContext context)
    {
        if (context.GetForwarderErrorFeature() is not { Error: ForwarderError.Request, Exception: { } exception })
        {
            return false;
        }

        for (Exception? e = exception; e is not null; e = e.InnerException)
        {
            if (e is HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError })
            {
                return true;
            }
        }

        return false;
    }
}

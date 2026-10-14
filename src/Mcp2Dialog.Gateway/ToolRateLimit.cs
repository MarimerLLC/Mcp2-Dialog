using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace Mcp2Dialog.Gateway;

/// <summary>Config section <c>Gateway:RateLimit</c>. It's reloaded live, so the switch can be flipped mid-demo.</summary>
internal sealed class ToolRateLimitOptions
{
    public const string Section = "Gateway:RateLimit";

    public bool Enabled { get; set; }
    public int PermitLimit { get; set; } = 3;
    public TimeSpan Window { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Header-based policy: each tool gets PermitLimit tools/call requests per Window, counted by the Mcp-Name
/// header alone. The gateway never parses the JSON-RPC body to find out which tool is being called.
/// </summary>
internal static class ToolRateLimit
{
    public const string PolicyName = "per-tool";

    public static IServiceCollection AddToolRateLimit(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ToolRateLimitOptions>(configuration.GetSection(ToolRateLimitOptions.Section));

        return services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            limiter.AddPolicy(PolicyName, context =>
            {
                var options = context.RequestServices.GetRequiredService<IOptionsMonitor<ToolRateLimitOptions>>().CurrentValue;
                var request = context.Request;
                if (!options.Enabled || McpHeaders.MethodOf(request) != "tools/call")
                {
                    return RateLimitPartition.GetNoLimiter("unlimited");
                }

                // The partition key comes straight from the header, e.g. "get_shipping_quote 3/00:00:10".
                return RateLimitPartition.GetFixedWindowLimiter(
                    $"{McpHeaders.NameOf(request)} {options.PermitLimit}/{options.Window}",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = options.PermitLimit, Window = options.Window });
            });

            limiter.OnRejected = (rejected, _) =>
            {
                var context = rejected.HttpContext;
                context.Response.Headers["X-Served-By"] = "gateway";
                var options = context.RequestServices.GetRequiredService<IOptionsMonitor<ToolRateLimitOptions>>().CurrentValue;
                context.RequestServices.GetRequiredService<ILogger<McpRequestLog>>().LogWarning(
                    "{McpMethod} {McpName} → rejected at the gateway, 429: over {PermitLimit} calls per {Window:g} for this Mcp-Name",
                    McpHeaders.MethodOf(context.Request), McpHeaders.NameOf(context.Request), options.PermitLimit, options.Window);
                return ValueTask.CompletedTask;
            };
        });
    }
}

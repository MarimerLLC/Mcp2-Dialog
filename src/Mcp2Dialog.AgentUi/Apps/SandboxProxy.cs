using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mcp2Dialog.AgentUi.Apps;

/// <summary>
/// Serves the MCP Apps sandbox proxy on its own origin. A web host must load apps through a proxy on a different
/// origin from its own pages. Here that's a second port, and that port serves the proxy and nothing else.
/// </summary>
public static partial class SandboxProxy
{
    public const string Path = "/sandbox";

    private static readonly Lazy<string> Html = new(() =>
    {
        using var stream = typeof(SandboxProxy).Assembly.GetManifestResourceStream("Mcp2Dialog.AgentUi.Apps.sandbox-proxy.html")
            ?? throw new InvalidOperationException("The sandbox proxy HTML isn't embedded in the agent assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    /// <summary>Answers every request to the sandbox origin, so the agent's own pages are never served there.</summary>
    public static IApplicationBuilder UseMcpAppsSandbox(this IApplicationBuilder app, Uri sandboxUrl) => app.Use(async (context, next) =>
    {
        var request = context.Request;
        var isSandboxOrigin = string.Equals(request.Host.Host, sandboxUrl.Host, StringComparison.OrdinalIgnoreCase)
            && (request.Host.Port ?? 80) == sandboxUrl.Port;

        if (!isSandboxOrigin)
        {
            await next(context);
            return;
        }

        if (!HttpMethods.IsGet(request.Method) || request.Path != Path)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("This origin serves only the MCP Apps sandbox proxy.");
            return;
        }

        // The CSP goes in an HTTP header, which the app can't edit (unlike a <meta> tag). The app's srcdoc
        // document inherits it.
        context.Response.Headers.ContentSecurityPolicy = BuildCsp(request.Query["csp"]);
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(Html.Value);
    });

    /// <summary>
    /// Builds the CSP from the resource's <c>_meta.ui.csp</c>. With no declared domains this is the spec's
    /// restrictive default: inline script and style only, and no network access.
    /// </summary>
    public static string BuildCsp(string? cspJson)
    {
        string[] connect = [], resource = [], frame = [], baseUri = [];
        if (!string.IsNullOrWhiteSpace(cspJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(cspJson);
                connect = Domains(doc.RootElement, "connectDomains");
                resource = Domains(doc.RootElement, "resourceDomains");
                frame = Domains(doc.RootElement, "frameDomains");
                baseUri = Domains(doc.RootElement, "baseUriDomains");
            }
            catch (JsonException)
            {
                // An unreadable CSP gets the restrictive default.
            }
        }

        string With(string sources, string[] domains) => string.Join(' ', [sources, .. domains]);

        return string.Join("; ",
            "default-src 'none'",
            With("script-src 'self' 'unsafe-inline'", resource),
            With("style-src 'self' 'unsafe-inline'", resource),
            With("img-src 'self' data:", resource),
            With("font-src 'self' data:", resource),
            With("media-src 'self' data:", resource),
            connect.Length > 0 ? With("connect-src", connect) : "connect-src 'none'",
            frame.Length > 0 ? With("frame-src", frame) : "frame-src 'none'",
            baseUri.Length > 0 ? With("base-uri", baseUri) : "base-uri 'none'",
            "object-src 'none'");
    }

    /// <summary>Reads a list of origins, dropping anything that could inject another CSP source or directive.</summary>
    private static string[] Domains(JsonElement csp, string name) =>
        csp.ValueKind == JsonValueKind.Object && csp.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray()
                .Where(d => d.ValueKind == JsonValueKind.String)
                .Select(d => d.GetString()!)
                .Where(d => SafeOrigin().IsMatch(d))]
            : [];

    [GeneratedRegex(@"^https?://[A-Za-z0-9.*-]+(:\d+)?$")]
    private static partial Regex SafeOrigin();
}

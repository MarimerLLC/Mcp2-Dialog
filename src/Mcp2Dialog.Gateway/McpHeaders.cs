namespace Mcp2Dialog.Gateway;

/// <summary>
/// The 2026-07-28 standard request headers. The client copies the JSON-RPC method and the tool (or prompt, or
/// resource) name into them, and the server rejects a request whose headers don't match its body. So a gateway
/// can route, log, and rate-limit on these two headers and never open the body.
/// </summary>
internal static class McpHeaders
{
    public const string Method = "Mcp-Method";
    public const string Name = "Mcp-Name";

    public static string MethodOf(HttpRequest request) =>
        request.Headers[Method].FirstOrDefault() ?? request.Method;

    public static string NameOf(HttpRequest request) =>
        request.Headers[Name].FirstOrDefault() ?? "-";
}

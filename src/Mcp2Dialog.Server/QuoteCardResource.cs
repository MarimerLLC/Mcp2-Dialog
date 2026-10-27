using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Mcp2Dialog.Server;

/// <summary>
/// The MCP App (<c>io.modelcontextprotocol/ui</c>) that renders a shipping quote. It's an ordinary resource with a
/// <c>ui://</c> URI and the <c>text/html;profile=mcp-app</c> MIME type. <c>get_shipping_quote</c> points at it from its
/// <c>_meta.ui.resourceUri</c>. A host that supports MCP Apps reads it with resources/read and runs it in a sandboxed
/// iframe. Every other client ignores it and shows the tool's text result instead.
/// </summary>
[McpServerResourceType]
public sealed class QuoteCardResource
{
    public const string Uri = "ui://mcp2dialog/quote-card.html";

    private static readonly Lazy<string> Html = new(() =>
    {
        using var stream = typeof(QuoteCardResource).Assembly.GetManifestResourceStream("Mcp2Dialog.Server.AppUi.quote-card.html")
            ?? throw new InvalidOperationException("The quote card HTML isn't embedded in the server assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    [McpServerResource(UriTemplate = Uri, Name = "quote_card", Title = "Shipping quote card", MimeType = McpApps.HtmlMimeType)]
    [Description("Interactive view of a shipping quote, rendered by hosts that support MCP Apps.")]
    public static ResourceContents GetQuoteCard() => new TextResourceContents
    {
        Uri = Uri,
        MimeType = McpApps.HtmlMimeType,
        Text = Html.Value,
        // The card loads nothing from outside, so it declares no CSP domains and the host applies its most
        // restrictive policy (no network access at all).
        Meta = new JsonObject
        {
            ["ui"] = JsonSerializer.SerializeToNode(new McpUiResourceMeta { PrefersBorder = true }, McpApps.SerializerOptions),
        },
    };
}

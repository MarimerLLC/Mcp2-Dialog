using System.Text.Json;
using System.Text.Json.Nodes;
using Mcp2Dialog.Telemetry;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Json;
using Spectre.Console.Rendering;

namespace Mcp2Dialog.Agent;

/// <param name="Show">--wire: print every message.</param>
/// <param name="Scaleout">--scaleout: show which instance served each request.</param>
/// <param name="KillAfterRound">--kill-after-round: kill the instance that served this tools/call round.</param>
internal sealed record WireOptions(bool Show, bool Scaleout = false, int? KillAfterRound = null);

/// <summary>
/// Sits in the MCP client's HttpClient pipeline. It logs every JSON-RPC message and, with --wire, prints it to
/// the console, so you can watch input_required results, inputResponses, and requestState move back and forth.
/// </summary>
internal sealed class WireLoggingHandler(WireOptions options, DialogStats stats, ILogger<WireLoggingHandler> logger)
    : DelegatingHandler
{
    private const int RequestStatePreviewLength = 24;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Only POSTs carry JSON-RPC. Buffering a long-lived GET SSE stream would never finish.
        if (request.Method != HttpMethod.Post || request.Content is null)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        await request.Content.LoadIntoBufferAsync(cancellationToken);
        var requestBody = await request.Content.ReadAsStringAsync(cancellationToken);
        var requestJson = TryParse(requestBody);
        var method = requestJson?["method"]?.GetValue<string>() ?? "(response)";
        var isRetry = requestJson?["params"]?["requestState"] is not null || requestJson?["params"]?["inputResponses"] is not null;

        int? round = method == "tools/call" ? Interlocked.Increment(ref stats.ToolCallRequests) : null;

        var mcpHeaders = request.Headers
            .Where(h => h.Key.StartsWith("mcp", StringComparison.OrdinalIgnoreCase))
            .Select(h => $"{h.Key}: {string.Join(",", h.Value)}")
            .ToList();
        var sessionIdSent = request.Headers.Contains("Mcp-Session-Id");

        logger.LogDebug("→ POST {Method}{Retry} headers {Headers} body {Body}",
            method, isRetry ? " (retry)" : "", mcpHeaders, requestBody);

        if (options.Show)
        {
            var title = $"→ {method}" + (isRetry ? " [yellow](retry with inputResponses + requestState)[/]" : "");
            var headerLines = mcpHeaders.Select(h => $"[grey]{Markup.Escape(h)}[/]").ToList();
            if (options.Scaleout)
            {
                headerLines.Add(ScaleoutView.NoSessionLine(sessionIdSent));
            }

            Show(title, Color.Blue, requestJson, requestBody, headerLines);
        }

        var response = await base.SendAsync(request, cancellationToken);

        var responseBody = "";
        if (response.Content.Headers.ContentLength is not 0)
        {
            await response.Content.LoadIntoBufferAsync(cancellationToken);
            responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        }

        var responseJson = TryParse(ExtractSseData(responseBody));
        var resultType = responseJson?["result"]?["resultType"]?.GetValue<string>();
        if (resultType == "input_required")
        {
            Interlocked.Increment(ref stats.InputRequiredResults);
        }

        var servedBy = response.Headers.TryGetValues(DialogDiagnostics.Headers.ServedBy, out var values) ? values.FirstOrDefault() : null;
        var hop = new Hop(method, round, servedBy, (int)response.StatusCode, resultType,
            request.Headers.TryGetValues("MCP-Protocol-Version", out var versions) ? versions.FirstOrDefault() : null,
            sessionIdSent, response.Headers.Contains("Mcp-Session-Id"));
        lock (stats.Hops)
        {
            stats.Hops.Add(hop);
        }

        logger.LogDebug("← {StatusCode} {Method} resultType {ResultType} served by {ServedBy} body {Body}",
            (int)response.StatusCode, method, resultType ?? "(none)", servedBy ?? "(unknown)", responseBody);

        if (options.Show)
        {
            var color = resultType == "input_required" ? Color.Yellow : response.IsSuccessStatusCode ? Color.Green : Color.Red;
            var title = $"← {(int)response.StatusCode} {method}" + (resultType is null ? "" : $" [bold]resultType: {resultType}[/]") +
                        (options.Scaleout ? $" {ScaleoutView.Badge(servedBy)}" : "");
            Show(title, color, responseJson, responseBody, options.Scaleout ? ScaleoutView.ResponseLines(hop, responseJson) : []);
        }
        else if (options.Scaleout)
        {
            ScaleoutView.WriteHopLine(hop, responseJson);
        }

        if (round is not null && round == options.KillAfterRound)
        {
            ScaleoutView.KillInstance(hop,
                response.Headers.TryGetValues(DialogDiagnostics.Headers.ServedByPid, out var pids) ? pids.FirstOrDefault() : null);
        }

        return response;
    }

    /// <param name="headers">Markup lines shown above the body.</param>
    private static void Show(string title, Color color, JsonNode? json, string raw, IReadOnlyList<string> headers)
    {
        var rows = new List<IRenderable>();
        foreach (var header in headers)
        {
            rows.Add(new Markup(header));
        }

        if (json is not null)
        {
            Abbreviate(json);
            rows.Add(new JsonText(json.ToJsonString()));
        }
        else if (!string.IsNullOrWhiteSpace(raw))
        {
            rows.Add(new Text(raw));
        }
        else
        {
            rows.Add(new Markup("[grey](empty body)[/]"));
        }

        AnsiConsole.Write(new Panel(new Rows(rows)).Header(title).BorderColor(color).Expand());
    }

    /// <summary>Shortens requestState so the encrypted token doesn't fill the screen.</summary>
    private static void Abbreviate(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj.ToList())
                {
                    if (key == "requestState" && child is JsonValue value &&
                        value.TryGetValue<string>(out var s) && s.Length > RequestStatePreviewLength)
                    {
                        obj[key] = $"{s[..RequestStatePreviewLength]}... ({s.Length} chars, encrypted by the server)";
                    }
                    else if (child is not null)
                    {
                        Abbreviate(child);
                    }
                }
                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    if (child is not null)
                    {
                        Abbreviate(child);
                    }
                }
                break;
        }
    }

    /// <summary>Streamable HTTP may answer with a JSON body or an SSE stream. For SSE, take the last data payload.</summary>
    private static string ExtractSseData(string body)
    {
        if (!body.Contains("data:", StringComparison.Ordinal))
        {
            return body;
        }

        var data = body.Split('\n')
            .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
            .Select(line => line["data:".Length..].Trim())
            .LastOrDefault(line => line.Length > 0);
        return data ?? body;
    }

    private static JsonNode? TryParse(string text)
    {
        try
        {
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

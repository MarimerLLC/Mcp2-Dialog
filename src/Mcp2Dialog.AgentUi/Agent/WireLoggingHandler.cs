using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mcp2Dialog.AgentUi.Agent;

/// <summary>
/// Sits in the MCP client's HttpClient pipeline. It logs every JSON-RPC message and adds it to the wire panel, so you
/// can watch input_required results, inputResponses, and requestState move back and forth.
/// </summary>
internal sealed class WireLoggingHandler(WireLog wire, DialogStats stats, ILogger<WireLoggingHandler> logger) : DelegatingHandler
{
    private const int RequestStatePreviewLength = 24;
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

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

        if (method == "tools/call")
        {
            Interlocked.Increment(ref stats.ToolCallRequests);
        }

        var mcpHeaders = request.Headers
            .Where(h => h.Key.StartsWith("mcp", StringComparison.OrdinalIgnoreCase))
            .Select(h => $"{h.Key}: {string.Join(",", h.Value)}")
            .ToList();

        logger.LogDebug("→ POST {Method}{Retry} headers {Headers} body {Body}",
            method, isRetry ? " (retry)" : "", mcpHeaders, requestBody);
        wire.Add(WireChannel.Http, WireKind.Request,
            $"→ {method}" + (isRetry ? "  (retry with inputResponses + requestState)" : ""),
            mcpHeaders, Pretty(requestJson, requestBody));

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

        logger.LogDebug("← {StatusCode} {Method} resultType {ResultType} body {Body}",
            (int)response.StatusCode, method, resultType ?? "(none)", responseBody);

        var kind = resultType == "input_required" ? WireKind.InputRequired
            : response.IsSuccessStatusCode && responseJson?["error"] is null ? WireKind.Response
            : WireKind.Error;
        wire.Add(WireChannel.Http, kind,
            $"← {(int)response.StatusCode} {method}" + (resultType is null ? "" : $"  resultType: {resultType}"),
            [], Pretty(responseJson, responseBody));

        return response;
    }

    private static string Pretty(JsonNode? json, string raw)
    {
        if (json is null)
        {
            return string.IsNullOrWhiteSpace(raw) ? "(empty body)" : raw;
        }

        Abbreviate(json);
        return json.ToJsonString(Indented);
    }

    /// <summary>Shortens requestState so the encrypted token doesn't fill the panel.</summary>
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
                    else if (key == "text" && child is JsonValue text && text.TryGetValue<string>(out var html) &&
                             html.StartsWith("<!DOCTYPE html>", StringComparison.OrdinalIgnoreCase))
                    {
                        // The MCP App's HTML from resources/read. Show that it came, not all of it.
                        obj[key] = $"{html[..15]}... ({html.Length} chars of HTML)";
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

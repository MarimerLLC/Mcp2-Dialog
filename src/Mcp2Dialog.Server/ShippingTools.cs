using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mcp2Dialog.Telemetry;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using static ModelContextProtocol.Protocol.ElicitRequestParams;

namespace Mcp2Dialog.Server;

/// <summary>
/// A tool that can't answer until it knows enough. When information is missing it throws
/// <see cref="InputRequiredException"/>, so the server returns an <c>input_required</c> result instead of a final
/// one. That result holds one or more elicitation requests plus an opaque <c>requestState</c>. The client answers
/// the requests and retries the same tools/call with <c>inputResponses</c> and the echoed <c>requestState</c>.
/// This repeats until the tool has what it needs.
/// <para>
/// The final result carries the quote twice: as text for any client (and a model), and as <c>structuredContent</c>
/// for the MCP App named in <c>_meta.ui.resourceUri</c>, which hosts that support MCP Apps render instead of the text.
/// </para>
/// </summary>
[McpServerToolType]
public sealed class ShippingTools(DialogStateProtector protector, ShippingRates rates, ILogger<ShippingTools> logger)
{
    private static readonly Counter<long> Rounds = DialogDiagnostics.Meter.CreateCounter<long>(
        "mcp2dialog.dialog.rounds", description: "tools/call requests handled as part of a dialog, by outcome");

    private static readonly Histogram<int> RoundsToComplete = DialogDiagnostics.Meter.CreateHistogram<int>(
        "mcp2dialog.dialog.rounds_to_complete", description: "Number of round trips a dialog needed to finish");

    [McpServerTool(Name = "get_shipping_quote", ReadOnly = true)]
    [McpAppUi(ResourceUri = QuoteCardResource.Uri)]
    [Description("Gets a shipping quote for a package. Any details that are missing are requested from the user " +
                 "through a multi round-trip dialog, so all arguments are optional.")]
    public CallToolResult GetShippingQuote(
        McpServer server,
        RequestContext<CallToolRequestParams> context,
        [Description("Destination city")] string? destination = null,
        [Description("Package weight in kilograms")] double? weightKg = null,
        [Description("Service level: standard, express, or overnight")] string? speed = null)
    {
        var request = context.Params!;

        // 1. Correlate: a retry carries the requestState we issued last round; an initial call starts a new dialog.
        DialogState state;
        if (request.RequestState is { } token)
        {
            if (!protector.TryUnprotect(token, out var previous))
            {
                throw new McpException("The dialog state is invalid or has expired. Start a new quote.");
            }

            state = previous! with { Round = previous!.Round + 1 };
        }
        else
        {
            state = DialogState.Start();
        }

        state = state.WithArguments(destination, weightKg, speed);

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["DialogId"] = state.DialogId,
            ["DialogRound"] = state.Round,
        });

        // Tag the SDK's tools/call span and add one of our own, so each round is easy to find in a trace.
        Activity.Current?.SetTag(DialogDiagnostics.Tags.DialogId, state.DialogId);
        using var activity = DialogDiagnostics.ActivitySource.StartActivity($"dialog round {state.Round}");
        activity?.SetTag(DialogDiagnostics.Tags.DialogId, state.DialogId);
        activity?.SetTag(DialogDiagnostics.Tags.Round, state.Round);
        activity?.SetTag(DialogDiagnostics.Tags.ToolName, "get_shipping_quote");

        // Under 2026-07-28 the client's capabilities, including the extensions it supports, ride on each request's
        // _meta, which the SDK surfaces on the JSON-RPC message context. (McpServer.ClientCapabilities is always null on
        // a stateless server.)
        var clientCapabilities = context.JsonRpcRequest.Context?.ClientCapabilities ?? server.ClientCapabilities;
        var appsSupported = McpApps.GetUiCapability(clientCapabilities)?.MimeTypes?.Contains(McpApps.HtmlMimeType) == true;

        logger.LogInformation(
            "Dialog {DialogId} round {DialogRound}: {Kind} (protocol {ProtocolVersion}, MRTR supported: {MrtrSupported}, MCP Apps supported: {AppsSupported})",
            state.DialogId, state.Round, request.RequestState is null ? "new request" : "retry with requestState",
            server.NegotiatedProtocolVersion, server.IsMrtrSupported, appsSupported);

        // 2. Fold in the client's answers from the previous round, if any.
        if (request.InputResponses is { Count: > 0 } responses)
        {
            foreach (var (key, response) in responses)
            {
                var answer = response.Deserialize(InputResponse.ElicitResultJsonTypeInfo);
                logger.LogInformation("Dialog {DialogId}: input '{InputKey}' came back as {Action} {@Content}",
                    state.DialogId, key, answer?.Action, answer?.Content?.ToDictionary(kv => kv.Key, kv => kv.Value.GetRawText()));

                if (answer is not { IsAccepted: true })
                {
                    return Finish(state, "cancelled",
                        $"Quote cancelled: the user chose to {answer?.Action ?? "cancel"} the '{key}' question " +
                        $"(dialog {state.DialogId}, round {state.Round}).");
                }

                state = Apply(state, key, answer.Content);
            }
        }

        // 3. Work out what's still missing, and ask for all of it in one round.
        var (inputRequests, problems) = WhatIsMissing(state);

        if (inputRequests.Count > 0)
        {
            // MRTR needs a 2026-07-28 client (or a stateful 2025-11-25 session the SDK can bridge), and the client
            // must declare elicitation. Without both, we can't ask, so tell the caller which arguments to send.
            if (!server.IsMrtrSupported || clientCapabilities?.Elicitation is null)
            {
                return Finish(state, "unsupported",
                    $"More information is needed ({string.Join(", ", inputRequests.Keys)}). " +
                    "Call again with destination, weightKg, and speed, or use a client that supports " +
                    "MCP 2026-07-28 multi round-trip requests with elicitation.");
            }

            foreach (var (_, input) in inputRequests)
            {
                input.Params = WithDialogMeta(input.Params, state);
            }

            activity?.SetTag(DialogDiagnostics.Tags.InputKeys, string.Join(",", inputRequests.Keys));
            activity?.SetTag(DialogDiagnostics.Tags.Outcome, "input_required");
            Rounds.Add(1, new KeyValuePair<string, object?>("outcome", "input_required"));

            logger.LogInformation(
                "Dialog {DialogId} round {DialogRound}: returning input_required for {InputKeys}{Problems}",
                state.DialogId, state.Round, inputRequests.Keys, problems.Count > 0 ? $" ({string.Join("; ", problems)})" : "");

            throw new InputRequiredException(inputRequests, protector.Protect(state));
        }

        // 4. Everything is known: produce the quote.
        var price = rates.Price(state.WeightKg!.Value, state.Speed!);
        var surcharge = state.SurchargeAccepted == true ? ShippingRates.HeavyOvernightSurcharge : 0m;
        var eta = ShippingRates.Services.First(s => s.Speed == state.Speed).Eta;

        return Finish(state, "completed",
            $"Quote for a {state.WeightKg:0.##} kg package to {state.Destination} by {state.Speed} ({eta}): " +
            $"${price + surcharge:0.00}" + (surcharge > 0 ? $" including ${surcharge:0.00} heavy overnight surcharge" : "") +
            $". Dialog {state.DialogId} completed in {state.Round} round(s).");

        CallToolResult Finish(DialogState final, string outcome, string message)
        {
            activity?.SetTag(DialogDiagnostics.Tags.Outcome, outcome);
            Rounds.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
            RoundsToComplete.Record(final.Round, new KeyValuePair<string, object?>("outcome", outcome));
            logger.LogInformation("Dialog {DialogId} finished as {Outcome} after {DialogRound} round(s) in {Elapsed}",
                final.DialogId, outcome, final.Round, DateTimeOffset.UtcNow - final.StartedAt);

            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = message }],
                StructuredContent = JsonSerializer.SerializeToElement(QuoteView(final, outcome, message), JsonSerializerOptions.Web),
            };
        }
    }

    /// <summary>
    /// What the quote card renders. Hosts don't add structuredContent to a model's context, so it can carry more
    /// detail than the text, such as the price of every service level for the card's "switch" buttons.
    /// </summary>
    private object QuoteView(DialogState state, string outcome, string message)
    {
        var known = outcome == "completed";
        var price = known ? rates.Price(state.WeightKg!.Value, state.Speed!) : (decimal?)null;
        var surcharge = known && state.SurchargeAccepted == true ? ShippingRates.HeavyOvernightSurcharge : 0m;

        return new
        {
            status = outcome,
            message,
            dialogId = state.DialogId,
            rounds = state.Round,
            destination = state.Destination,
            weightKg = state.WeightKg,
            speed = state.Speed,
            eta = ShippingRates.Services.FirstOrDefault(s => s.Speed == state.Speed).Eta,
            price,
            surcharge,
            total = price + surcharge,
            options = state.WeightKg is > 0 and <= ShippingRates.MaxWeightKg
                ? ShippingRates.Services.Select(s => new
                {
                    speed = s.Speed,
                    label = s.Label,
                    eta = s.Eta,
                    price = rates.Price(state.WeightKg.Value, s.Speed),
                    surchargeApplies = s.Speed == "overnight" && state.WeightKg > ShippingRates.HeavyOvernightThresholdKg,
                }).ToArray()
                : [],
        };
    }

    private (Dictionary<string, InputRequest> Requests, List<string> Problems) WhatIsMissing(DialogState state)
    {
        var requests = new Dictionary<string, InputRequest>();
        var problems = new List<string>();

        if (state.Destination is null)
        {
            requests["destination"] = Ask("Where is the package going?", "destination", new StringSchema
            {
                Title = "Destination city",
                MinLength = 2,
            });
        }

        if (state.WeightKg is null)
        {
            requests["weight"] = Ask("How much does the package weigh?", "weightKg", new NumberSchema
            {
                Title = "Weight (kg)",
                Minimum = 0.1,
                Maximum = ShippingRates.MaxWeightKg,
            });
        }
        else if (state.WeightKg is <= 0 or > ShippingRates.MaxWeightKg)
        {
            problems.Add($"weight {state.WeightKg} kg is out of range");
            requests["weight"] = Ask(
                $"{state.WeightKg} kg isn't something we can ship. Enter a weight between 0.1 and {ShippingRates.MaxWeightKg} kg.",
                "weightKg", new NumberSchema { Title = "Weight (kg)", Minimum = 0.1, Maximum = ShippingRates.MaxWeightKg });
        }

        // The later questions depend on the earlier answers, so they wait for a later round.
        if (requests.Count > 0)
        {
            return (requests, problems);
        }

        if (!ShippingRates.IsKnownSpeed(state.Speed))
        {
            if (state.Speed is not null)
            {
                problems.Add($"unknown speed '{state.Speed}'");
            }

            requests["speed"] = Ask(
                $"Shipping {state.WeightKg:0.##} kg to {state.Destination}. Which service do you want?",
                "speed",
                new TitledSingleSelectEnumSchema
                {
                    Title = "Service level",
                    OneOf = [.. ShippingRates.Services.Select(s => new EnumSchemaOption
                    {
                        Const = s.Speed,
                        Title = $"{s.Label} ({s.Eta}) - ${rates.Price(state.WeightKg!.Value, s.Speed):0.00}",
                    })],
                });
            return (requests, problems);
        }

        if (state.Speed == "overnight" && state.WeightKg > ShippingRates.HeavyOvernightThresholdKg && state.SurchargeAccepted is null)
        {
            requests["surcharge"] = Ask(
                $"Overnight packages over {ShippingRates.HeavyOvernightThresholdKg} kg have a " +
                $"${ShippingRates.HeavyOvernightSurcharge:0.00} surcharge. Accept it? (No switches to express.)",
                "accept",
                new BooleanSchema { Title = "Accept surcharge", Default = true });
        }

        return (requests, problems);
    }

    private static DialogState Apply(DialogState state, string key, IDictionary<string, JsonElement>? content)
    {
        JsonElement value = default;
        var hasValue = content is not null && key switch
        {
            "destination" => content.TryGetValue("destination", out value),
            "weight" => content.TryGetValue("weightKg", out value),
            "speed" => content.TryGetValue("speed", out value),
            "surcharge" => content.TryGetValue("accept", out value),
            _ => false,
        };

        if (!hasValue)
        {
            return state;
        }

        return key switch
        {
            "destination" => state with { Destination = value.GetString()?.Trim() },
            "weight" => state with { WeightKg = value.ValueKind == JsonValueKind.Number ? value.GetDouble() : double.TryParse(value.GetString(), out var kg) ? kg : null },
            "speed" => state with { Speed = value.GetString()?.ToLowerInvariant() },
            "surcharge" when value.ValueKind == JsonValueKind.False => state with { SurchargeAccepted = false, Speed = "express" },
            "surcharge" => state with { SurchargeAccepted = value.ValueKind == JsonValueKind.True },
            _ => state,
        };
    }

    private static InputRequest Ask(string message, string property, PrimitiveSchemaDefinition schema) =>
        InputRequest.ForElicitation(new ElicitRequestParams
        {
            Message = message,
            RequestedSchema = new()
            {
                Properties = { [property] = schema },
                Required = [property],
            },
        });

    /// <summary>
    /// Adds a readable copy of the dialog id and round to the elicitation's <c>_meta</c> so the client can show and log it.
    /// </summary>
    private static JsonElement? WithDialogMeta(JsonElement? elicitParams, DialogState state)
    {
        if (elicitParams is not { } element || JsonNode.Parse(element.GetRawText()) is not JsonObject obj)
        {
            return elicitParams;
        }

        var meta = obj["_meta"] as JsonObject ?? new JsonObject();
        meta[DialogDiagnostics.MetaKeys.DialogId] = state.DialogId;
        meta[DialogDiagnostics.MetaKeys.Round] = state.Round;
        obj["_meta"] = meta;
        return JsonSerializer.SerializeToElement(obj);
    }
}

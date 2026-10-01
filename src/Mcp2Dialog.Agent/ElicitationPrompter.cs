using System.Text.Json;
using Mcp2Dialog.Telemetry;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Spectre.Console;
using static ModelContextProtocol.Protocol.ElicitRequestParams;

namespace Mcp2Dialog.Agent;

/// <summary>
/// Answers the server's elicitation requests. It prompts the user with Spectre.Console, or, with --auto,
/// picks fixed answers so the whole dialog can run unattended.
/// </summary>
internal sealed class ElicitationPrompter(bool autoAnswer, DialogStats stats, ILogger<ElicitationPrompter> logger)
{
    public async ValueTask<ElicitResult> HandleAsync(ElicitRequestParams? request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var dialogId = request.Meta?[DialogDiagnostics.MetaKeys.DialogId]?.GetValue<string>() ?? "(unknown)";
        var round = request.Meta?[DialogDiagnostics.MetaKeys.Round]?.GetValue<int>();

        using var activity = DialogDiagnostics.ActivitySource.StartActivity("elicitation");
        activity?.SetTag(DialogDiagnostics.Tags.DialogId, dialogId);
        activity?.SetTag(DialogDiagnostics.Tags.Round, round);

        logger.LogInformation("Server asked in dialog {DialogId} round {DialogRound}: {Message} {@Fields}",
            dialogId, round, request.Message, request.RequestedSchema?.Properties.Keys);

        AnsiConsole.Write(new Panel(new Markup(Markup.Escape(request.Message)))
            .Header($"[yellow]Server needs more information[/] [grey]dialog ...{Markup.Escape(Short(dialogId))} · round {round}[/]")
            .BorderColor(Color.Yellow)
            .Expand());

        var result = autoAnswer ? AutoAnswer(request) : await PromptAsync(request, cancellationToken);

        Interlocked.Increment(ref stats.QuestionsAnswered);
        activity?.SetTag(DialogDiagnostics.Tags.Outcome, result.Action);
        logger.LogInformation("Answered dialog {DialogId} round {DialogRound} with {Action} {@Content}",
            dialogId, round, result.Action, result.Content?.ToDictionary(kv => kv.Key, kv => kv.Value.GetRawText()));

        return result;
    }

    private static async Task<ElicitResult> PromptAsync(ElicitRequestParams request, CancellationToken cancellationToken)
    {
        var action = await new SelectionPrompt<string>()
            .Title("How do you want to respond?")
            .AddChoices("accept", "decline", "cancel")
            .ShowAsync(AnsiConsole.Console, cancellationToken);

        if (action != "accept")
        {
            return new ElicitResult { Action = action };
        }

        var content = new Dictionary<string, JsonElement>();
        foreach (var (name, schema) in Fields(request))
        {
            var label = Markup.Escape(schema.Title ?? name);
            object value = schema switch
            {
                NumberSchema number => await new TextPrompt<double>(label)
                    .Validate(v => (number.Minimum is null || v >= number.Minimum) && (number.Maximum is null || v <= number.Maximum)
                        ? ValidationResult.Success()
                        : ValidationResult.Error($"Must be between {number.Minimum} and {number.Maximum}"))
                    .ShowAsync(AnsiConsole.Console, cancellationToken),

                BooleanSchema boolean => await new ConfirmationPrompt(label) { DefaultValue = boolean.Default ?? true }
                    .ShowAsync(AnsiConsole.Console, cancellationToken),

                TitledSingleSelectEnumSchema titled => (await new SelectionPrompt<EnumSchemaOption>()
                    .Title(label)
                    .UseConverter(o => Markup.Escape(o.Title))
                    .AddChoices(titled.OneOf)
                    .ShowAsync(AnsiConsole.Console, cancellationToken)).Const,

                UntitledSingleSelectEnumSchema untitled => await new SelectionPrompt<string>()
                    .Title(label)
                    .AddChoices(untitled.Enum)
                    .ShowAsync(AnsiConsole.Console, cancellationToken),

                StringSchema text => await new TextPrompt<string>(label)
                    .Validate(v => text.MinLength is null || v.Trim().Length >= text.MinLength
                        ? ValidationResult.Success()
                        : ValidationResult.Error($"Must be at least {text.MinLength} characters"))
                    .ShowAsync(AnsiConsole.Console, cancellationToken),

                _ => await new TextPrompt<string>(label).ShowAsync(AnsiConsole.Console, cancellationToken),
            };

            content[name] = JsonSerializer.SerializeToElement(value);
        }

        return new ElicitResult { Action = "accept", Content = content };
    }

    /// <summary>
    /// Fixed answers that take the dialog through all three rounds: destination and weight, then speed
    /// (the last option, overnight), then the heavy overnight surcharge (42 kg is over the limit).
    /// </summary>
    private static ElicitResult AutoAnswer(ElicitRequestParams request)
    {
        var content = new Dictionary<string, JsonElement>();
        foreach (var (name, schema) in Fields(request))
        {
            object value = schema switch
            {
                NumberSchema number => Math.Clamp(42.0, number.Minimum ?? double.MinValue, number.Maximum ?? double.MaxValue),
                BooleanSchema boolean => boolean.Default ?? true,
                TitledSingleSelectEnumSchema titled => titled.OneOf[^1].Const,
                UntitledSingleSelectEnumSchema untitled => untitled.Enum[^1],
                StringSchema text => text.Default ?? "Oslo",
                _ => "",
            };

            content[name] = JsonSerializer.SerializeToElement(value);
            AnsiConsole.MarkupLine($"  [grey]auto-answer[/] {Markup.Escape(name)} = [bold]{Markup.Escape(content[name].GetRawText())}[/]");
        }

        return new ElicitResult { Action = "accept", Content = content };
    }

    private static IEnumerable<KeyValuePair<string, PrimitiveSchemaDefinition>> Fields(ElicitRequestParams request) =>
        request.RequestedSchema?.Properties ?? Enumerable.Empty<KeyValuePair<string, PrimitiveSchemaDefinition>>();

    private static string Short(string id) => id.Length > 8 ? id[^8..] : id;
}

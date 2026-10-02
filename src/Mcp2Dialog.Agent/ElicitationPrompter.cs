using System.Globalization;
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
    // The SDK resolves all of a round's input requests concurrently (Task.WhenAll). There is one console,
    // so the questions take turns: each one's panel, prompts, and echoed answer finish before the next starts.
    private readonly SemaphoreSlim _console = new(1, 1);

    public async ValueTask<ElicitResult> HandleAsync(ElicitRequestParams? request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _console.WaitAsync(cancellationToken);
        try
        {
            return await HandleOneAsync(request, cancellationToken);
        }
        finally
        {
            _console.Release();
        }
    }

    private async Task<ElicitResult> HandleOneAsync(ElicitRequestParams request, CancellationToken cancellationToken)
    {
        var dialogId = request.Meta?[DialogDiagnostics.MetaKeys.DialogId]?.GetValue<string>() ?? "(unknown)";
        var round = request.Meta?[DialogDiagnostics.MetaKeys.Round]?.GetValue<int>();

        using var activity = DialogDiagnostics.ActivitySource.StartActivity("elicitation");
        activity?.SetTag(DialogDiagnostics.Tags.DialogId, dialogId);
        activity?.SetTag(DialogDiagnostics.Tags.Round, round);

        logger.LogInformation("Server asked in dialog {DialogId} round {DialogRound}: {Message} {@Fields}",
            dialogId, round, request.Message, request.RequestedSchema?.Properties.Keys);

        AnsiConsole.Write(new Panel(new Markup(Markup.Escape(request.Message)))
            .Header("[yellow]Server needs more information[/]" + (round is null
                ? ""
                : $" [grey]dialog ...{Markup.Escape(Short(dialogId))} · round {round}[/]"))
            .BorderColor(Color.Yellow)
            .Expand());

        var result = autoAnswer ? AutoAnswer(request) : await PromptAsync(request, cancellationToken);

        Interlocked.Increment(ref stats.QuestionsAnswered);
        activity?.SetTag(DialogDiagnostics.Tags.Outcome, result.Action);
        logger.LogInformation("Answered dialog {DialogId} round {DialogRound} with {Action} {@Content}",
            dialogId, round, result.Action, result.Content?.ToDictionary(kv => kv.Key, kv => kv.Value.GetRawText()));

        return result;
    }

    /// <summary>One possible reply to a field: a value to send, or a refusal (decline or cancel) that ends the question.</summary>
    private sealed record Choice(string Label, object? Value = null, string? Refusal = null);

    // Every elicitation result carries an action: accept, decline (the user refused), or cancel (the user
    // dismissed it). Answering a question means accept. Declining and cancelling are offered as escape routes.
    private static readonly Choice[] Refusals =
    [
        new("Decline to answer", Refusal: "decline"),
        new("Cancel", Refusal: "cancel"),
    ];

    private static async Task<ElicitResult> PromptAsync(ElicitRequestParams request, CancellationToken cancellationToken)
    {
        var content = new Dictionary<string, JsonElement>();
        foreach (var (name, schema) in Fields(request))
        {
            var label = schema.Title ?? name;
            var choice = schema switch
            {
                TitledSingleSelectEnumSchema titled =>
                    await ChooseAsync(label, titled.OneOf.Select(o => new Choice(o.Title, o.Const)), cancellationToken),

                UntitledSingleSelectEnumSchema untitled =>
                    await ChooseAsync(label, untitled.Enum.Select(e => new Choice(e, e)), cancellationToken),

                // "No" is a real answer (for the surcharge it switches to express), so it isn't the same as declining.
                BooleanSchema boolean => await ChooseAsync(label,
                    boolean.Default == false ? [new("No", false), new("Yes", true)] : [new("Yes", true), new("No", false)],
                    cancellationToken),

                NumberSchema number => await TypeAsync(label, text =>
                    !double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) ? (null, "Enter a number")
                    : value < number.Minimum || value > number.Maximum ? (null, $"Must be between {number.Minimum} and {number.Maximum}")
                    : (value, null),
                    cancellationToken),

                StringSchema text => await TypeAsync(label, input =>
                    input.Trim().Length < (text.MinLength ?? 0) ? (null, $"Must be at least {text.MinLength} characters")
                    : (input.Trim(), null),
                    cancellationToken),

                _ => await TypeAsync(label, input => (input, null), cancellationToken),
            };

            if (choice.Refusal is { } refusal)
            {
                return new ElicitResult { Action = refusal };
            }

            content[name] = JsonSerializer.SerializeToElement(choice.Value);
        }

        return new ElicitResult { Action = "accept", Content = content };
    }

    /// <summary>A menu of the field's options, followed by the refusals.</summary>
    private static async Task<Choice> ChooseAsync(string label, IEnumerable<Choice> options, CancellationToken cancellationToken)
    {
        var choice = await new SelectionPrompt<Choice>()
            .Title(Markup.Escape(label))
            .UseConverter(c => c.Refusal is null ? Markup.Escape(c.Label) : $"[grey]{Markup.Escape(c.Label)}[/]")
            .AddChoices(options.Concat(Refusals))
            .ShowAsync(AnsiConsole.Console, cancellationToken);

        // Selection prompts erase themselves once you choose, so print the choice to keep the transcript readable.
        AnsiConsole.MarkupLine($"{Markup.Escape(label)}: [bold]{Markup.Escape(choice.Label)}[/]");
        return choice;
    }

    /// <summary>
    /// A typed answer. A blank line opens a menu to decline, cancel, or go back to the question.
    /// </summary>
    /// <param name="parse">Turns the typed text into a value, or returns an error message to show.</param>
    private static async Task<Choice> TypeAsync(
        string label, Func<string, (object? Value, string? Error)> parse, CancellationToken cancellationToken)
    {
        while (true)
        {
            var text = await new TextPrompt<string>($"{Markup.Escape(label)} [grey](blank to decline or cancel)[/]")
                .AllowEmpty()
                .Validate(input => string.IsNullOrWhiteSpace(input) || parse(input).Error is not { } error
                    ? ValidationResult.Success()
                    : ValidationResult.Error(Markup.Escape(error)))
                .ShowAsync(AnsiConsole.Console, cancellationToken);

            if (!string.IsNullOrWhiteSpace(text))
            {
                return new Choice(text, parse(text).Value);
            }

            var choice = await ChooseAsync("No answer given", [new("Back to the question")], cancellationToken);
            if (choice.Refusal is not null)
            {
                return choice;
            }
        }
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

using System.Text.Json;
using Mcp2Dialog.Telemetry;
using ModelContextProtocol.Protocol;
using static ModelContextProtocol.Protocol.ElicitRequestParams;

namespace Mcp2Dialog.AgentUi.Agent;

/// <summary>
/// One elicitation request from an input_required result, waiting for the user. The SDK's elicitation handler
/// awaits <see cref="Answer"/>, and the question's card in the page completes it.
/// </summary>
public sealed class PendingQuestion
{
    private readonly TaskCompletionSource<ElicitResult> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PendingQuestion(ElicitRequestParams request)
    {
        Request = request;
        DialogId = request.Meta?[DialogDiagnostics.MetaKeys.DialogId]?.GetValue<string>() ?? "(unknown)";
        Round = request.Meta?[DialogDiagnostics.MetaKeys.Round]?.GetValue<int>();
        Fields = [.. request.RequestedSchema?.Properties ?? Enumerable.Empty<KeyValuePair<string, PrimitiveSchemaDefinition>>()];
    }

    public ElicitRequestParams Request { get; }
    public string DialogId { get; }
    public int? Round { get; }
    public IReadOnlyList<KeyValuePair<string, PrimitiveSchemaDefinition>> Fields { get; }

    public Task<ElicitResult> Answer => _answer.Task;
    public ElicitResult? Result { get; private set; }
    public bool AnsweredAutomatically { get; private set; }
    public bool IsOpen => !_answer.Task.IsCompleted;

    public void Complete(ElicitResult result, bool automatically = false)
    {
        Result = result;
        AnsweredAutomatically = automatically;
        _answer.TrySetResult(result);
    }

    public void Abandon() => _answer.TrySetCanceled();

    /// <summary>
    /// Fixed answers that take the dialog through all three rounds: destination and weight, then speed
    /// (the last option, overnight), then the heavy overnight surcharge (42 kg is over the limit).
    /// </summary>
    public ElicitResult AutoAnswer()
    {
        var content = new Dictionary<string, JsonElement>();
        foreach (var (name, schema) in Fields)
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
        }

        return new ElicitResult { Action = "accept", Content = content };
    }
}

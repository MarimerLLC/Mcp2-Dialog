using System.Diagnostics;
using System.Text.Json;
using Mcp2Dialog.Server.Tasks;
using Mcp2Dialog.Telemetry;
using ModelContextProtocol.Protocol;
using static ModelContextProtocol.Protocol.ElicitRequestParams;

namespace Mcp2Dialog.Server.Freight;

public sealed record FreightOrder(string? QuoteRef, string Destination, double WeightKg, string Speed);

/// <summary>The booking's own progress, saved next to the task so a restarted server can carry on from the same stage.</summary>
public sealed record FreightProgress(FreightOrder Order, int NextStage = 0, bool? PickupAccepted = null);

/// <summary>Asks the client a question. Inside a task this ends up in the task's <c>inputRequests</c>.</summary>
public delegate ValueTask<ElicitResult> AskClient(ElicitRequestParams question, CancellationToken cancellationToken);

/// <summary>
/// The slow part of book_freight: a few clearly named stages, with one question for the user halfway through.
/// Each stage updates the task's statusMessage, so a client polling tasks/get can show what's happening.
/// </summary>
public sealed class FreightBooking(ITaskStore store, IConfiguration configuration, ILogger<FreightBooking> logger)
{
    private static readonly (string Label, double Seconds)[] Stages =
    [
        ("Validating shipment details", 4),
        ("Reserving carrier capacity", 8),
        ("Customs pre-clearance", 8),
        // The pickup-window question comes here, about halfway through.
        ("Assigning pickup slot", 10),
        ("Issuing waybill and labels", 10),
    ];

    private const int QuestionBeforeStage = 3;
    private const string NewPickupWindow = "18:00-20:00";

    /// <summary>--fast (Freight:Fast) runs every stage at a quarter of the speed, about 10 seconds in all.</summary>
    private double Scale => configuration.GetValue("Freight:Fast", false) ? 0.25 : 1.0;

    public async Task<string> RunAsync(string taskId, FreightProgress progress, AskClient ask, CancellationToken cancellationToken)
    {
        await SaveAsync(taskId, progress, cancellationToken);

        for (var stage = progress.NextStage; stage < Stages.Length; stage++)
        {
            if (stage == QuestionBeforeStage && progress.PickupAccepted is null)
            {
                // Mid-flight question. The task moves to input_required until the client answers with tasks/update.
                var accepted = await AskAboutPickupWindowAsync(taskId, ask, cancellationToken);
                if (!accepted)
                {
                    const string message = "Booking cancelled: the new pickup window was not accepted.";
                    await store.SetCancelledAsync(taskId, message, CancellationToken.None);
                    logger.LogInformation("Task {TaskId}: {Message}", taskId, message);
                    return message;
                }

                progress = progress with { PickupAccepted = true };
                await SaveAsync(taskId, progress, cancellationToken);
            }

            var (label, seconds) = Stages[stage];
            using (var activity = DialogDiagnostics.ActivitySource.StartActivity($"freight stage {stage + 1}: {label}"))
            {
                activity?.SetTag(DialogDiagnostics.Tags.TaskId, taskId);
                activity?.SetTag(DialogDiagnostics.Tags.TaskStage, label);

                // Progress update: clients see this as statusMessage on their next tasks/get.
                await store.SetStatusMessageAsync(taskId, $"{label} ({stage + 1}/{Stages.Length})", cancellationToken);
                logger.LogInformation("Task {TaskId} stage {Stage}/{StageCount}: {StageLabel}", taskId, stage + 1, Stages.Length, label);

                await Task.Delay(TimeSpan.FromSeconds(seconds * Scale), cancellationToken);
            }

            progress = progress with { NextStage = stage + 1 };
            await SaveAsync(taskId, progress, cancellationToken);
        }

        var order = progress.Order;
        var waybill = $"MCP-{taskId[^6..].ToUpperInvariant()}";
        var summary = $"Booked {order.WeightKg:0.##} kg to {order.Destination} by {order.Speed}" +
                      (order.QuoteRef is null ? "" : $" (quote {order.QuoteRef})") +
                      $". Waybill {waybill}, pickup today {NewPickupWindow}.";

        await store.SetStatusMessageAsync(taskId, $"Booked: waybill {waybill}", cancellationToken);
        logger.LogInformation("Task {TaskId}: {Summary}", taskId, summary);
        return summary;
    }

    private async Task<bool> AskAboutPickupWindowAsync(string taskId, AskClient ask, CancellationToken cancellationToken)
    {
        await store.SetStatusMessageAsync(taskId, $"Waiting for you: carrier moved pickup to {NewPickupWindow}", cancellationToken);
        logger.LogInformation("Task {TaskId}: asking the client to confirm the new pickup window", taskId);

        using var activity = DialogDiagnostics.ActivitySource.StartActivity("freight question: pickup window");
        activity?.SetTag(DialogDiagnostics.Tags.TaskId, taskId);

        var answer = await ask(new ElicitRequestParams
        {
            Message = $"The carrier moved the pickup window to {NewPickupWindow} today. Accept the new window? " +
                      "(No cancels the booking.)",
            RequestedSchema = new()
            {
                Properties = { ["accept"] = new BooleanSchema { Title = $"Accept pickup {NewPickupWindow}", Default = true } },
                Required = ["accept"],
            },
        }, cancellationToken);

        var accepted = answer is { IsAccepted: true } &&
                       answer.Content?.TryGetValue("accept", out var value) == true && value.ValueKind == JsonValueKind.True;

        activity?.SetTag(DialogDiagnostics.Tags.Outcome, accepted ? "accepted" : answer.Action);
        logger.LogInformation("Task {TaskId}: pickup window answer {Action} (accepted: {Accepted})", taskId, answer.Action, accepted);
        return accepted;
    }

    private Task SaveAsync(string taskId, FreightProgress progress, CancellationToken cancellationToken) =>
        store.SetWorkStateAsync(taskId, JsonSerializer.SerializeToElement(progress), cancellationToken);

    public static FreightProgress? ReadProgress(JsonElement? workState) =>
        workState is { } json ? json.Deserialize<FreightProgress>() : null;
}

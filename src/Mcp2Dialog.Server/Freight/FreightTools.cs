using System.ComponentModel;
using System.Diagnostics;
using Mcp2Dialog.Server.Tasks;
using Mcp2Dialog.Telemetry;
using ModelContextProtocol.Server;

namespace Mcp2Dialog.Server.Freight;

/// <summary>
/// A slow tool for the Tasks extension (<c>io.modelcontextprotocol/tasks</c>). When the client declares the extension,
/// the SDK's Tasks filter creates a task, answers tools/call with <c>resultType: "task"</c> straight away, and runs
/// this method in the background. The client then polls tasks/get, answers the mid-flight question with tasks/update,
/// and reads the final result from tasks/get once the status is completed.
/// </summary>
[McpServerToolType]
public sealed class FreightTools(FreightBooking booking, ILogger<FreightTools> logger)
{
    public const string ToolName = "book_freight";

    [McpServerTool(Name = ToolName)]
    [Description("Books freight with a carrier. Booking takes about 40 seconds and may ask one question along the way, " +
                 "so it runs as an MCP task. All arguments are optional; the defaults book 42 kg to Oslo by express.")]
    public async Task<string> BookFreight(
        McpServer server,
        [Description("Reference of an earlier shipping quote, if you have one")] string? quoteRef = null,
        [Description("Destination city")] string? destination = null,
        [Description("Package weight in kilograms")] double? weightKg = null,
        [Description("Service level: standard, express, or overnight")] string? speed = null,
        CancellationToken cancellationToken = default)
    {
        // The SDK only runs a tool as a task when the client declared the Tasks extension on this request.
        // Without it, this method runs inline and there is no task. Booking would take far too long to do inside
        // one tools/call, so say what's needed instead (like the guidance in get_shipping_quote).
        if (CurrentTask.Id is not { } taskId)
        {
            logger.LogInformation("book_freight called without the Tasks extension; returning guidance");
            Activity.Current?.SetTag(DialogDiagnostics.Tags.Outcome, "unsupported");
            return "book_freight needs the MCP Tasks extension. Booking takes about 40 seconds and asks a question " +
                   $"part-way through, so it only runs as a task. Call it again from a client that declares " +
                   $"'io.modelcontextprotocol/tasks' in the request's _meta (io.modelcontextprotocol/clientCapabilities " +
                   "→ extensions), then poll tasks/get for the result.";
        }

        var order = new FreightOrder(
            string.IsNullOrWhiteSpace(quoteRef) ? null : quoteRef.Trim(),
            string.IsNullOrWhiteSpace(destination) ? "Oslo" : destination.Trim(),
            weightKg is > 0 and <= ShippingRates.MaxWeightKg ? weightKg.Value : 42,
            ShippingRates.IsKnownSpeed(speed?.ToLowerInvariant()) ? speed!.ToLowerInvariant() : "express");

        using var scope = logger.BeginScope(new Dictionary<string, object> { ["TaskId"] = taskId });
        using var activity = DialogDiagnostics.ActivitySource.StartActivity("freight booking");
        activity?.SetTag(DialogDiagnostics.Tags.TaskId, taskId);
        activity?.SetTag(DialogDiagnostics.Tags.ToolName, ToolName);

        logger.LogInformation("Task {TaskId}: booking {WeightKg} kg to {Destination} by {Speed} in the background",
            taskId, order.WeightKg, order.Destination, order.Speed);

        // Inside a task, server.ElicitAsync doesn't send elicitation/create. The SDK adds it to the task's
        // inputRequests (status input_required) and waits for the matching tasks/update.
        return await booking.RunAsync(taskId, new FreightProgress(order), server.ElicitAsync, cancellationToken);
    }
}

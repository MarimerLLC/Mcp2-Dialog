namespace Mcp2Dialog.Server;

/// <summary>
/// Everything the server knows about one shipping-quote dialog. It is never stored on the server; it
/// travels to the client and back inside the encrypted <c>requestState</c> token.
/// </summary>
public sealed record DialogState
{
    /// <summary>The correlation id for every round of this dialog.</summary>
    public required string DialogId { get; init; }

    /// <summary>The number of the request that produced this state (1 = the original tools/call).</summary>
    public int Round { get; init; } = 1;

    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    public string? Destination { get; init; }
    public double? WeightKg { get; init; }
    public string? Speed { get; init; }
    public bool? SurchargeAccepted { get; init; }

    public static DialogState Start() => new() { DialogId = Guid.CreateVersion7().ToString("N") };

    /// <summary>Fills in any values the caller passed as tool arguments.</summary>
    public DialogState WithArguments(string? destination, double? weightKg, string? speed) => this with
    {
        Destination = Destination ?? NullIfBlank(destination),
        WeightKg = WeightKg ?? weightKg,
        Speed = Speed ?? NullIfBlank(speed)?.ToLowerInvariant(),
    };

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

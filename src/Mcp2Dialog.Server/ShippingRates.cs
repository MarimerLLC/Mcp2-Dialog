namespace Mcp2Dialog.Server;

/// <summary>A deliberately simple rate card for the sample.</summary>
public sealed class ShippingRates
{
    public const double MaxWeightKg = 70;
    public const double HeavyOvernightThresholdKg = 30;
    public const decimal HeavyOvernightSurcharge = 45m;

    public static readonly IReadOnlyList<(string Speed, string Label, double Multiplier, string Eta)> Services =
    [
        ("standard", "Standard", 1.0, "5-7 days"),
        ("express", "Express", 1.8, "2 days"),
        ("overnight", "Overnight", 3.2, "next day"),
    ];

    public static bool IsKnownSpeed(string? speed) => Services.Any(s => s.Speed == speed);

    public decimal Price(double weightKg, string speed)
    {
        var service = Services.First(s => s.Speed == speed);
        var price = (8.0 + weightKg * 1.75) * service.Multiplier;
        return Math.Round((decimal)price, 2);
    }
}

namespace AuctionPlatform.Domain.Common;

/// <summary>
/// Currency handling helpers. Amounts are always <see cref="decimal"/> backed by
/// PostgreSQL numeric(18,2); floating point is never used for money.
/// </summary>
public static class Money
{
    public const string DefaultCurrency = "SAR";
    public const int Scale = 2;

    /// <summary>Deterministic half-away-from-zero rounding to the currency scale.</summary>
    public static decimal Round(decimal amount) =>
        Math.Round(amount, Scale, MidpointRounding.AwayFromZero);

    public static bool IsPositive(decimal amount) => amount > 0m;
}

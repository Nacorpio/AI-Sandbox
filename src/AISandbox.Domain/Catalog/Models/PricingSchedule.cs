using AISandbox.Domain.Abstractions;

namespace AISandbox.Domain.Catalog.Models;

/// <summary>
/// Token prices for one model route, in one currency, per million tokens.
/// </summary>
public sealed record PricingSchedule
{
    private const decimal TokensPerMillion = 1_000_000m;

    private PricingSchedule(string currency, decimal inputPerMillion, decimal outputPerMillion, decimal? cacheReadPerMillion)
    {
        Currency = currency;
        InputPerMillion = inputPerMillion;
        OutputPerMillion = outputPerMillion;
        CacheReadPerMillion = cacheReadPerMillion;
    }

    public string Currency { get; }

    public decimal InputPerMillion { get; }

    public decimal OutputPerMillion { get; }

    public decimal? CacheReadPerMillion { get; }

    public static PricingSchedule Free { get; } = new(Money.Usd, 0m, 0m, null);

    public static Result<PricingSchedule> Create(string currency, decimal inputPerMillion, decimal outputPerMillion, decimal? cacheReadPerMillion = null)
    {
        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
        {
            return Error.Validation("pricing", "Currency must be a three-letter code.");
        }

        if (inputPerMillion < 0 || outputPerMillion < 0 || cacheReadPerMillion < 0)
        {
            return Error.Validation("pricing", "Prices cannot be negative.");
        }

        return new PricingSchedule(currency.ToUpperInvariant(), inputPerMillion, outputPerMillion, cacheReadPerMillion);
    }

    public Money CostOf(int inputTokens, int outputTokens) =>
        new((inputTokens * InputPerMillion + outputTokens * OutputPerMillion) / TokensPerMillion, Currency);
}

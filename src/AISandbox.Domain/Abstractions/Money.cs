namespace AISandbox.Domain.Abstractions;

/// <summary>
/// An amount in one currency. Model prices are tiny, so amounts keep full decimal precision.
/// </summary>
public sealed record Money(decimal Amount, string Currency)
{
    public const string Usd = "USD";

    public static Money Zero(string currency = Usd) => new(0m, currency);

    public static Money operator +(Money left, Money right)
    {
        if (left.Currency != right.Currency)
        {
            throw new InvalidOperationException($"Cannot add {left.Currency} to {right.Currency}.");
        }

        return new Money(left.Amount + right.Amount, left.Currency);
    }

    public override string ToString() => $"{Amount:0.##########} {Currency}";
}

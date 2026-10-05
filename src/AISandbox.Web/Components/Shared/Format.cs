using System.Globalization;
using AISandbox.Domain.Abstractions;
using AISandbox.Domain.Experimentation;

namespace AISandbox.Web.Components.Shared;

internal static class Format
{
    /// <summary>
    /// Single calls cost fractions of a cent, so show enough digits to tell models apart.
    /// </summary>
    public static string Cost(Money money)
    {
        var amount = money.Amount.ToString(money.Amount >= 0.01m ? "0.####" : "0.#########", CultureInfo.InvariantCulture);
        return money.Currency == Money.Usd ? $"${amount}" : $"{amount} {money.Currency}";
    }

    public static string Duration(TimeSpan duration) =>
        duration.TotalSeconds >= 1 ? $"{duration.TotalSeconds:0.00} s" : $"{duration.TotalMilliseconds:0} ms";

    public static string Percent(double value) => $"{value * 100:0.#}%";

    public static string Tokens(TokenUsage usage) => $"{usage.InputTokens:N0} in · {usage.OutputTokens:N0} out";
}

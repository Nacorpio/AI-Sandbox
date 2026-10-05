using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using AISandbox.Application.Abstractions;
using AISandbox.Domain.Catalog.Providers;
using Microsoft.Extensions.Options;

namespace AISandbox.Infrastructure.Runs;

/// <summary>
/// Bound from the <c>ProviderLimits</c> configuration section.
/// </summary>
public sealed class ProviderLimitsOptions
{
    public const string SectionName = "ProviderLimits";

    /// <summary>
    /// How many calls may be in flight to one provider at the same time.
    /// </summary>
    public int DefaultConcurrency { get; set; } = 4;
}

internal sealed class ProviderLimitsOptionsValidator : IValidateOptions<ProviderLimitsOptions>
{
    public ValidateOptionsResult Validate(string? name, ProviderLimitsOptions options) =>
        options.DefaultConcurrency is < 1 or > 1000
            ? ValidateOptionsResult.Fail($"{ProviderLimitsOptions.SectionName}:{nameof(ProviderLimitsOptions.DefaultConcurrency)} must be between 1 and 1000.")
            : ValidateOptionsResult.Success;
}

/// <summary>
/// One concurrency limiter per provider, so several models on the same provider share its budget.
/// Waiting callers queue in arrival order.
/// </summary>
internal sealed class ProviderLimiter(IOptions<ProviderLimitsOptions> options) : IProviderLimiter, IDisposable
{
    private readonly ConcurrentDictionary<ProviderId, ConcurrencyLimiter> _limiters = new();

    public async ValueTask<IDisposable> AcquireAsync(ProviderId providerId, CancellationToken cancellationToken)
    {
        var limiter = _limiters.GetOrAdd(providerId, _ => new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = options.Value.DefaultConcurrency,
            QueueLimit = int.MaxValue,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        }));

        return await limiter.AcquireAsync(1, cancellationToken);
    }

    public void Dispose()
    {
        foreach (var limiter in _limiters.Values)
        {
            limiter.Dispose();
        }
    }
}

using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using AISandbox.Application.Abstractions;
using AISandbox.Application.Features.Providers;
using AISandbox.Domain.Catalog.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AISandbox.Infrastructure.Runs;

/// <summary>
/// Bound from the <c>ProviderLimits</c> configuration section.
/// </summary>
public sealed class ProviderLimitsOptions
{
    public const string SectionName = "ProviderLimits";

    /// <summary>
    /// How many calls may be in flight to one provider at the same time, unless the provider
    /// has its own rate-limit policy.
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
/// One limiter set per provider, so several models on the same provider share its budget.
/// Waiting callers queue in arrival order. A provider's policy sets its concurrency and,
/// optionally, a requests-per-second pace; without a policy the global default concurrency applies.
/// Tokens per second is stored on the policy but not enforced.
/// </summary>
internal sealed class ProviderLimiter(IOptions<ProviderLimitsOptions> options, IServiceScopeFactory scopes)
    : IProviderLimiter, IDisposable
{
    private static readonly TimeSpan MinReplenishmentPeriod = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan RetireDelay = TimeSpan.FromMinutes(5);

    private sealed record Limits(ConcurrencyLimiter Concurrency, TokenBucketRateLimiter? Pace) : IDisposable
    {
        public void Dispose()
        {
            Concurrency.Dispose();
            Pace?.Dispose();
        }
    }

    private readonly ConcurrentDictionary<ProviderId, Lazy<Task<Limits>>> _limits = new();

    public async ValueTask<IDisposable> AcquireAsync(ProviderId providerId, CancellationToken cancellationToken)
    {
        var limits = await _limits.GetOrAdd(providerId, id => new Lazy<Task<Limits>>(() => CreateAsync(id))).Value;

        var slot = await limits.Concurrency.AcquireAsync(1, cancellationToken);
        if (limits.Pace is null)
        {
            return slot;
        }

        try
        {
            using var pace = await limits.Pace.AcquireAsync(1, cancellationToken);
            return slot;
        }
        catch
        {
            slot.Dispose();
            throw;
        }
    }

    public void Invalidate(ProviderId providerId)
    {
        if (_limits.TryRemove(providerId, out var old) && old.IsValueCreated)
        {
            // Calls already waiting on the old limiters finish there; dispose them later.
            _ = old.Value.ContinueWith(
                async task =>
                {
                    if (task.IsCompletedSuccessfully)
                    {
                        await Task.Delay(RetireDelay);
                        task.Result.Dispose();
                    }
                },
                TaskScheduler.Default);
        }
    }

    private async Task<Limits> CreateAsync(ProviderId id)
    {
        RateLimitPolicy? policy;
        await using (var scope = scopes.CreateAsyncScope())
        {
            policy = await scope.ServiceProvider.GetRequiredService<IProviderQueries>().GetRateLimitAsync(id, CancellationToken.None);
        }

        var concurrency = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = policy?.MaxConcurrency ?? options.Value.DefaultConcurrency,
            QueueLimit = int.MaxValue,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });

        TokenBucketRateLimiter? pace = null;
        if (policy?.RequestsPerSecond is { } perSecond)
        {
            var period = TimeSpan.FromSeconds(Math.Max(1 / perSecond, MinReplenishmentPeriod.TotalSeconds));
            var tokens = Math.Max(1, (int)Math.Round(perSecond * period.TotalSeconds));
            pace = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = tokens,
                TokensPerPeriod = tokens,
                ReplenishmentPeriod = period,
                AutoReplenishment = true,
                QueueLimit = int.MaxValue,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            });
        }

        return new Limits(concurrency, pace);
    }

    public void Dispose()
    {
        foreach (var entry in _limits.Values)
        {
            if (entry.IsValueCreated && entry.Value.IsCompletedSuccessfully)
            {
                entry.Value.Result.Dispose();
            }
        }
    }
}

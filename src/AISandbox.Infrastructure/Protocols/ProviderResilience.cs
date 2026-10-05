using System.Net;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace AISandbox.Infrastructure.Protocols;

/// <summary>
/// Tunables for the resilience pipeline around every provider call (section "ProviderResilience").
/// </summary>
public sealed class ProviderResilience
{
    public const string SectionName = "ProviderResilience";

    /// <summary>Retries after the first attempt, so a call makes at most <c>MaxRetries + 1</c> attempts.</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Base of the exponential, jittered backoff between attempts.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Longest a <c>Retry-After</c> header is honoured, so a hostile header cannot stall a run.</summary>
    public TimeSpan MaxRetryAfter { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Covers every attempt and the waits between them.</summary>
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>Share of failed calls within the sampling window that opens the circuit.</summary>
    public double CircuitFailureRatio { get; set; } = 0.5;

    /// <summary>Calls needed within the sampling window before the circuit may open (at least 2).</summary>
    public int CircuitMinimumThroughput { get; set; } = 5;

    public TimeSpan CircuitSamplingDuration { get; set; } = TimeSpan.FromSeconds(60);

    public TimeSpan CircuitBreakDuration { get; set; } = TimeSpan.FromSeconds(30);
}

internal sealed class ProviderResilienceValidator : IValidateOptions<ProviderResilience>
{
    public ValidateOptionsResult Validate(string? name, ProviderResilience options)
    {
        var errors = new List<string>();
        if (options.MaxRetries is < 0 or > 10)
        {
            errors.Add("ProviderResilience:MaxRetries must be between 0 and 10.");
        }

        Positive(options.BaseDelay, nameof(options.BaseDelay), TimeSpan.FromMinutes(1));
        Positive(options.MaxRetryAfter, nameof(options.MaxRetryAfter), TimeSpan.FromMinutes(10));
        Positive(options.AttemptTimeout, nameof(options.AttemptTimeout), TimeSpan.FromMinutes(10));
        Positive(options.TotalTimeout, nameof(options.TotalTimeout), TimeSpan.FromMinutes(30));
        Positive(options.CircuitSamplingDuration, nameof(options.CircuitSamplingDuration), TimeSpan.FromHours(1));
        Positive(options.CircuitBreakDuration, nameof(options.CircuitBreakDuration), TimeSpan.FromHours(1));

        if (options.AttemptTimeout > options.TotalTimeout)
        {
            errors.Add("ProviderResilience:AttemptTimeout must not exceed TotalTimeout.");
        }

        if (options.CircuitFailureRatio is <= 0 or > 1)
        {
            errors.Add("ProviderResilience:CircuitFailureRatio must be greater than 0 and at most 1.");
        }

        if (options.CircuitMinimumThroughput < 2)
        {
            errors.Add("ProviderResilience:CircuitMinimumThroughput must be at least 2.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);

        void Positive(TimeSpan value, string property, TimeSpan max)
        {
            if (value <= TimeSpan.Zero || value > max)
            {
                errors.Add($"ProviderResilience:{property} must be greater than zero and at most {max}.");
            }
        }
    }
}

/// <summary>Counts the HTTP attempts one call made; read back by <see cref="ProviderHttp"/>.</summary>
internal sealed class AttemptCounter
{
    public static readonly HttpRequestOptionsKey<AttemptCounter> Key = new("AISandbox.AttemptCounter");

    private int _count;

    public int Count => Volatile.Read(ref _count);

    public void Increment() => Interlocked.Increment(ref _count);
}

/// <summary>Sits inside the retry loop, so it runs once per attempt.</summary>
internal sealed class AttemptCountingHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Options.TryGetValue(AttemptCounter.Key, out var counter))
        {
            counter.Increment();
        }

        return base.SendAsync(request, cancellationToken);
    }
}

internal static class ProviderPipeline
{
    public static bool IsTransient(HttpResponseMessage response) =>
        response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;

    public static void Configure(ResiliencePipelineBuilder<HttpResponseMessage> builder, ProviderResilience options, TimeProvider time)
    {
        builder.AddTimeout(options.TotalTimeout);

        if (options.MaxRetries > 0)
        {
            builder.AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = options.MaxRetries,
                Delay = options.BaseDelay,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = args => ValueTask.FromResult(args.Outcome switch
                {
                    { Exception: HttpRequestException or TimeoutRejectedException } => true,
                    { Result: { } response } => IsTransient(response),
                    _ => false,
                }),
                DelayGenerator = args => ValueTask.FromResult(
                    args.Outcome.Result is { StatusCode: HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable } response
                        ? RetryAfter(response, options.MaxRetryAfter, time)
                        : null),
            });
        }

        builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
        {
            FailureRatio = options.CircuitFailureRatio,
            MinimumThroughput = options.CircuitMinimumThroughput,
            SamplingDuration = options.CircuitSamplingDuration,
            BreakDuration = options.CircuitBreakDuration,
            // Rate limiting says the caller is too fast, not that the provider is down.
            ShouldHandle = args => ValueTask.FromResult(args.Outcome switch
            {
                { Exception: HttpRequestException or TimeoutRejectedException } => true,
                { Result: { } response } => response.StatusCode != HttpStatusCode.TooManyRequests && IsTransient(response),
                _ => false,
            }),
        });

        builder.AddTimeout(options.AttemptTimeout);
    }

    /// <summary>The wait a <c>Retry-After</c> header asks for (seconds or HTTP-date), capped; null when absent.</summary>
    public static TimeSpan? RetryAfter(HttpResponseMessage response, TimeSpan cap, TimeProvider time)
    {
        var header = response.Headers.RetryAfter;
        TimeSpan? wait = header?.Delta ?? (header?.Date is { } date ? date - time.GetUtcNow() : null);
        return wait is null ? null : wait.Value < TimeSpan.Zero ? TimeSpan.Zero : wait.Value > cap ? cap : wait.Value;
    }
}

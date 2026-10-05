using AISandbox.Application.Abstractions;
using AISandbox.Domain.Abstractions;
using AISandbox.Domain.Catalog.Providers;

namespace AISandbox.Application.Features.Providers;

public sealed record ListProviders;

/// <summary>
/// Rate-limit fields as typed by the user. All null means "no policy": the global default applies.
/// </summary>
public sealed record RateLimitSettings(int? MaxConcurrency, double? RequestsPerSecond, double? TokensPerSecond)
{
    public static Result<RateLimitPolicy?> ToPolicy(RateLimitSettings? settings)
    {
        if (settings is null or { MaxConcurrency: null, RequestsPerSecond: null, TokensPerSecond: null })
        {
            return Result.Success<RateLimitPolicy?>(null);
        }

        if (settings.MaxConcurrency is null)
        {
            return Error.Validation("rateLimit", "Max concurrency is required when a rate limit is set.");
        }

        var policy = RateLimitPolicy.Create(settings.MaxConcurrency.Value, settings.RequestsPerSecond, settings.TokensPerSecond);
        return policy.IsFailure ? policy.Error! : Result.Success<RateLimitPolicy?>(policy.Value);
    }

    public static RateLimitSettings? From(RateLimitPolicy? policy) =>
        policy is null ? null : new(policy.MaxConcurrency, policy.RequestsPerSecond, policy.TokensPerSecond);
}

public sealed record ProviderSummary(
    ProviderId Id,
    string Name,
    ProviderKind Kind,
    string BaseUrl,
    AuthSchemeKind Auth,
    SecretStatus Key,
    IReadOnlyDictionary<string, string> PathVariables,
    RateLimitSettings? RateLimit,
    string? AuthHeaderName);

public sealed record ProviderRow(
    ProviderId Id,
    string Name,
    ProviderKind Kind,
    string BaseUrl,
    AuthSchemeKind Auth,
    SecretReference Secret,
    IReadOnlyDictionary<string, string> PathVariables,
    RateLimitSettings? RateLimit,
    string? AuthHeaderName);

/// <summary>
/// Read-side projection of providers. Implemented in Infrastructure without loading aggregates.
/// </summary>
public interface IProviderQueries
{
    Task<IReadOnlyList<ProviderRow>> ListAsync(CancellationToken cancellationToken);

    /// <summary>The provider's rate-limit policy; null when it has none or does not exist.</summary>
    Task<RateLimitPolicy?> GetRateLimitAsync(ProviderId id, CancellationToken cancellationToken);

    Task<int> CountModelsAsync(ProviderId id, CancellationToken cancellationToken);
}

public sealed class ListProvidersHandler(IProviderQueries queries, ISecretStore secrets)
    : IQueryHandler<ListProviders, IReadOnlyList<ProviderSummary>>
{
    public async Task<IReadOnlyList<ProviderSummary>> HandleAsync(ListProviders query, CancellationToken cancellationToken)
    {
        var rows = await queries.ListAsync(cancellationToken);
        var summaries = new List<ProviderSummary>(rows.Count);
        foreach (var row in rows)
        {
            var key = await secrets.DescribeAsync(row.Secret, cancellationToken);
            summaries.Add(new ProviderSummary(row.Id, row.Name, row.Kind, row.BaseUrl, row.Auth, key, row.PathVariables, row.RateLimit, row.AuthHeaderName));
        }

        return summaries;
    }
}

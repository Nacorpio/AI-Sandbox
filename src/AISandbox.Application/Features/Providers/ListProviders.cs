using AISandbox.Application.Abstractions;
using AISandbox.Domain.Catalog.Providers;

namespace AISandbox.Application.Features.Providers;

public sealed record ListProviders;

public sealed record ProviderSummary(
    ProviderId Id,
    string Name,
    ProviderKind Kind,
    string BaseUrl,
    AuthSchemeKind Auth,
    SecretStatus Key);

public sealed record ProviderRow(
    ProviderId Id,
    string Name,
    ProviderKind Kind,
    string BaseUrl,
    AuthSchemeKind Auth,
    SecretReference Secret);

/// <summary>
/// Read-side projection of providers. Implemented in Infrastructure without loading aggregates.
/// </summary>
public interface IProviderQueries
{
    Task<IReadOnlyList<ProviderRow>> ListAsync(CancellationToken cancellationToken);
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
            summaries.Add(new ProviderSummary(row.Id, row.Name, row.Kind, row.BaseUrl, row.Auth, key));
        }

        return summaries;
    }
}

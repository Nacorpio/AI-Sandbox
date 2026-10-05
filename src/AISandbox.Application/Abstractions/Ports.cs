using AISandbox.Domain.Authoring.QuestionSets;
using AISandbox.Domain.Catalog.Providers;

namespace AISandbox.Application.Abstractions;

public interface IUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public interface IProviderRepository
{
    void Add(Provider provider);

    void Remove(Provider provider);

    Task<Provider?> GetAsync(ProviderId id, CancellationToken cancellationToken);
}

public enum SecretSource
{
    Missing,
    Stored,
    Environment,
}

/// <summary>
/// What the UI may know about a secret: where it comes from and a masked hint. Never the value.
/// </summary>
public sealed record SecretStatus(SecretSource Source, string? MaskedHint);

/// <summary>
/// Holds credentials outside the aggregates. Implementations encrypt at rest and let an
/// environment variable named by the reference override the stored value.
/// </summary>
public interface ISecretStore
{
    Task StoreAsync(SecretReference reference, string secret, CancellationToken cancellationToken);

    Task<string?> GetAsync(SecretReference reference, CancellationToken cancellationToken);

    Task<SecretStatus> DescribeAsync(SecretReference reference, CancellationToken cancellationToken);

    /// <summary>Removes the stored value, if any. An environment override is not affected.</summary>
    Task DeleteAsync(SecretReference reference, CancellationToken cancellationToken);
}

public interface IQuestionSetRepository
{
    void Add(QuestionSet questionSet);

    Task<QuestionSet?> GetAsync(QuestionSetId id, CancellationToken cancellationToken);
}

using AISandbox.Domain.Abstractions;

namespace AISandbox.Domain.Catalog.Providers;

/// <summary>
/// An organisation's endpoint family plus the credentials used to call it.
/// </summary>
public sealed class Provider : AggregateRoot<ProviderId>
{
    public const int MaxNameLength = 100;

    private Provider(
        ProviderId id,
        string name,
        ProviderKind kind,
        EndpointUri baseUrl,
        AuthScheme auth,
        SecretReference secret,
        DateTimeOffset createdAt)
        : base(id)
    {
        Name = name;
        Kind = kind;
        BaseUrl = baseUrl;
        Auth = auth;
        Secret = secret;
        CreatedAt = createdAt;
    }

    // Used by persistence to materialise the aggregate.
    private Provider()
        : base(default)
    {
        Name = null!;
        BaseUrl = null!;
        Auth = null!;
        Secret = null!;
    }

    public string Name { get; private set; }

    public ProviderKind Kind { get; private set; }

    public EndpointUri BaseUrl { get; private set; }

    public AuthScheme Auth { get; private set; }

    public SecretReference Secret { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Result<Provider> Register(
        string? name,
        ProviderKind kind,
        string? baseUrl,
        AuthScheme auth,
        string? environmentVariable,
        DateTimeOffset now)
    {
        var trimmedName = name?.Trim();
        if (string.IsNullOrEmpty(trimmedName))
        {
            return Error.Validation("name", "Name is required.");
        }

        if (trimmedName.Length > MaxNameLength)
        {
            return Error.Validation("name", $"Name must be at most {MaxNameLength} characters.");
        }

        if (!Enum.IsDefined(kind))
        {
            return Error.Validation("kind", "Unknown provider kind.");
        }

        var endpoint = EndpointUri.Create(baseUrl);
        if (endpoint.IsFailure)
        {
            return endpoint.Error!;
        }

        var id = ProviderId.New();
        var secret = SecretReference.ForProvider(id, string.IsNullOrWhiteSpace(environmentVariable) ? null : environmentVariable.Trim());
        var provider = new Provider(id, trimmedName, kind, endpoint.Value, auth, secret, now);
        provider.Raise(new ProviderRegistered(id, trimmedName, kind, now));
        return provider;
    }
}

public sealed record ProviderRegistered(ProviderId ProviderId, string Name, ProviderKind Kind, DateTimeOffset OccurredAt)
    : IDomainEvent;

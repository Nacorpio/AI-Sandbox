using AISandbox.Domain.Abstractions;

namespace AISandbox.Domain.Catalog.Providers;

/// <summary>
/// An organisation's endpoint family plus the credentials used to call it.
/// </summary>
public sealed class Provider : AggregateRoot<ProviderId>
{
    public const int MaxNameLength = 100;

    /// <summary>Path variable holding the Cloudflare account id.</summary>
    public const string AccountIdVariable = "account_id";

    private Provider(
        ProviderId id,
        string name,
        ProviderKind kind,
        EndpointUri baseUrl,
        AuthScheme auth,
        SecretReference secret,
        IReadOnlyDictionary<string, string> pathVariables,
        DateTimeOffset createdAt)
        : base(id)
    {
        Name = name;
        Kind = kind;
        BaseUrl = baseUrl;
        Auth = auth;
        Secret = secret;
        PathVariables = pathVariables;
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
        PathVariables = null!;
    }

    public string Name { get; private set; }

    public ProviderKind Kind { get; private set; }

    public EndpointUri BaseUrl { get; private set; }

    public AuthScheme Auth { get; private set; }

    public SecretReference Secret { get; private set; }

    /// <summary>
    /// Values substituted into endpoint paths, such as the Cloudflare <c>account_id</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string> PathVariables { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Result<Provider> Register(
        string? name,
        ProviderKind kind,
        string? baseUrl,
        AuthScheme auth,
        string? environmentVariable,
        IReadOnlyDictionary<string, string>? pathVariables,
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

        var variables = ValidatePathVariables(kind, pathVariables);
        if (variables.IsFailure)
        {
            return variables.Error!;
        }

        var id = ProviderId.New();
        var secret = SecretReference.ForProvider(id, string.IsNullOrWhiteSpace(environmentVariable) ? null : environmentVariable.Trim());
        var provider = new Provider(id, trimmedName, kind, endpoint.Value, auth, secret, variables.Value, now);
        provider.Raise(new ProviderRegistered(id, trimmedName, kind, now));
        return provider;
    }

    private static Result<IReadOnlyDictionary<string, string>> ValidatePathVariables(
        ProviderKind kind,
        IReadOnlyDictionary<string, string>? pathVariables)
    {
        var cleaned = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in pathVariables ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrEmpty(key) || !key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                return Error.Validation("pathVariables", $"Path variable name '{key}' may only contain letters, digits and underscores.");
            }

            var trimmed = value?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            if (trimmed.Any(c => char.IsWhiteSpace(c) || c is '/' or '?' or '#' or '\\'))
            {
                return Error.Validation("pathVariables", $"Path variable '{key}' must not contain spaces or path separators.");
            }

            cleaned[key] = trimmed;
        }

        if (kind == ProviderKind.CloudflareWorkersAi && !cleaned.ContainsKey(AccountIdVariable))
        {
            return Error.Validation("pathVariables", "An account id is required for Cloudflare Workers AI.");
        }

        return cleaned;
    }
}

public sealed record ProviderRegistered(ProviderId ProviderId, string Name, ProviderKind Kind, DateTimeOffset OccurredAt)
    : IDomainEvent;

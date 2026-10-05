using AISandbox.Domain.Abstractions;

namespace AISandbox.Domain.Catalog.Providers;

public readonly record struct ProviderId(Guid Value)
{
    public static ProviderId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

public enum ProviderKind
{
    TypeSafe,
    CloudflareWorkersAi,
    OpenRouter,
    Custom,
}

/// <summary>
/// Absolute http(s) base address of a provider's API.
/// </summary>
public sealed record EndpointUri
{
    private EndpointUri(Uri value) => Value = value;

    public Uri Value { get; }

    public static Result<EndpointUri> Create(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Error.Validation("baseUrl", "Base URL is required.");
        }

        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return Error.Validation("baseUrl", "Base URL must be an absolute http or https address.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return Error.Validation("baseUrl", "Base URL must not contain credentials.");
        }

        return new EndpointUri(uri);
    }

    public override string ToString() => Value.ToString();
}

public enum AuthSchemeKind
{
    None,
    Bearer,
    Header,
}

/// <summary>
/// How the API key is attached to requests: as a bearer token, in a named header, or not at all.
/// </summary>
public sealed record AuthScheme
{
    private AuthScheme(AuthSchemeKind kind, string? headerName)
    {
        Kind = kind;
        HeaderName = headerName;
    }

    public AuthSchemeKind Kind { get; }

    public string? HeaderName { get; }

    public static AuthScheme None { get; } = new(AuthSchemeKind.None, null);

    public static AuthScheme Bearer { get; } = new(AuthSchemeKind.Bearer, null);

    public static Result<AuthScheme> Header(string? headerName) =>
        string.IsNullOrWhiteSpace(headerName)
            ? Error.Validation("auth", "A header name is required for header authentication.")
            : new AuthScheme(AuthSchemeKind.Header, headerName.Trim());

    public static Result<AuthScheme> Create(AuthSchemeKind kind, string? headerName) => kind switch
    {
        AuthSchemeKind.None => None,
        AuthSchemeKind.Bearer => Bearer,
        AuthSchemeKind.Header => Header(headerName),
        _ => Error.Validation("auth", "Unknown authentication scheme."),
    };
}

/// <summary>
/// A pointer to a credential held by the secret store. The aggregate never holds the key itself.
/// </summary>
/// <param name="Name">Opaque name of the stored secret.</param>
/// <param name="EnvironmentVariable">Optional environment variable that overrides the stored secret.</param>
public sealed record SecretReference(string Name, string? EnvironmentVariable)
{
    public static SecretReference ForProvider(ProviderId id, string? environmentVariable) =>
        new($"provider:{id}", environmentVariable);
}

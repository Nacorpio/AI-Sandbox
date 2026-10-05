using AISandbox.Domain.Catalog.Providers;

namespace AISandbox.Application.Features.Providers;

/// <summary>
/// Sensible starting values per provider kind, used to prefill the form and to pick the
/// environment variable that overrides a stored key.
/// </summary>
public static class ProviderDefaults
{
    public static string? EnvironmentVariableFor(ProviderKind kind) => kind switch
    {
        ProviderKind.TypeSafe => "TYPESAFE_API_KEY",
        ProviderKind.OpenRouter => "OPENROUTER_API_KEY",
        ProviderKind.CloudflareWorkersAi => "CLOUDFLARE_API_TOKEN",
        _ => null,
    };

    public static string? BaseUrlFor(ProviderKind kind) => kind switch
    {
        ProviderKind.TypeSafe => "https://api.typesafe.ai/v1",
        ProviderKind.OpenRouter => "https://openrouter.ai/api/v1",
        ProviderKind.CloudflareWorkersAi => "https://api.cloudflare.com/client/v4",
        _ => null,
    };
}

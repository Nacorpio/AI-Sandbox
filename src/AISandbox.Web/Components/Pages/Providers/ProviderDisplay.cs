using AISandbox.Domain.Catalog.Providers;

namespace AISandbox.Web.Components.Pages.Providers;

internal static class ProviderDisplay
{
    public static string KindName(ProviderKind kind) => kind switch
    {
        ProviderKind.TypeSafe => "TypeSafe",
        ProviderKind.CloudflareWorkersAi => "Cloudflare Workers AI",
        ProviderKind.OpenRouter => "OpenRouter",
        _ => "Custom",
    };

    public static string AuthName(AuthSchemeKind kind) => kind switch
    {
        AuthSchemeKind.Bearer => "Bearer token",
        AuthSchemeKind.Header => "Custom header",
        _ => "None",
    };
}

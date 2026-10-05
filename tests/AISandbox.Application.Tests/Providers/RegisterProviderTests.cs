using AISandbox.Application.Abstractions;
using AISandbox.Application.Features.Providers;
using AISandbox.Application.Tests.Support;
using AISandbox.Domain.Catalog.Providers;

namespace AISandbox.Application.Tests.Providers;

public sealed class RegisterProviderTests
{
    private const string OpenRouterKey = "sk-or-v1-0123456789abcdef0123456789abcdef";

    private static RegisterProvider OpenRouter(string? apiKey = OpenRouterKey, string? baseUrl = "https://openrouter.ai/api/v1") =>
        new("OpenRouter", ProviderKind.OpenRouter, baseUrl, AuthSchemeKind.Bearer, null, apiKey);

    private static Task<IReadOnlyList<ProviderSummary>> ListAsync(SandboxHost host) =>
        host.QueryAsync<ListProviders, IReadOnlyList<ProviderSummary>>(new ListProviders());

    [Fact]
    public async Task Registered_provider_is_listed_with_a_masked_stored_key()
    {
        await using var host = await SandboxHost.StartAsync();

        var result = await host.SendAsync<RegisterProvider, ProviderId>(OpenRouter());

        Assert.True(result.IsSuccess);
        var provider = Assert.Single(await ListAsync(host));
        Assert.Equal(result.Value, provider.Id);
        Assert.Equal("OpenRouter", provider.Name);
        Assert.Equal(ProviderKind.OpenRouter, provider.Kind);
        Assert.Equal("https://openrouter.ai/api/v1", provider.BaseUrl);
        Assert.Equal(SecretSource.Stored, provider.Key.Source);
        Assert.Equal("••••cdef", provider.Key.MaskedHint);
    }

    [Fact]
    public async Task Api_key_is_encrypted_at_rest()
    {
        await using var host = await SandboxHost.StartAsync();

        await host.SendAsync<RegisterProvider, ProviderId>(OpenRouter());

        var stored = Assert.Single(await host.ReadColumnAsync("SELECT Ciphertext FROM Secrets"));
        Assert.DoesNotContain(OpenRouterKey, stored);
        Assert.DoesNotContain("0123456789abcdef", stored);
    }

    [Fact]
    public async Task Environment_variable_overrides_the_stored_key()
    {
        await using var host = await SandboxHost.StartAsync(new Dictionary<string, string?>
        {
            ["OPENROUTER_API_KEY"] = "sk-or-v1-fromenvironment0000000000009999",
        });

        await host.SendAsync<RegisterProvider, ProviderId>(OpenRouter());

        var provider = Assert.Single(await ListAsync(host));
        Assert.Equal(SecretSource.Environment, provider.Key.Source);
        Assert.Equal("••••9999", provider.Key.MaskedHint);
    }

    [Fact]
    public async Task Provider_without_key_reports_it_missing()
    {
        await using var host = await SandboxHost.StartAsync();

        await host.SendAsync<RegisterProvider, ProviderId>(OpenRouter(apiKey: null));

        var provider = Assert.Single(await ListAsync(host));
        Assert.Equal(SecretSource.Missing, provider.Key.Source);
        Assert.Null(provider.Key.MaskedHint);
        Assert.Empty(await host.ReadColumnAsync("SELECT Ciphertext FROM Secrets"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://example.com")]
    [InlineData("https://user:pass@example.com")]
    public async Task Invalid_base_url_is_rejected_and_nothing_is_saved(string? baseUrl)
    {
        await using var host = await SandboxHost.StartAsync();

        var result = await host.SendAsync<RegisterProvider, ProviderId>(OpenRouter(baseUrl: baseUrl));

        Assert.True(result.IsFailure);
        Assert.Equal("validation.baseUrl", result.Error!.Code);
        Assert.Empty(await ListAsync(host));
        Assert.Empty(await host.ReadColumnAsync("SELECT Ciphertext FROM Secrets"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task Name_is_required(string? name)
    {
        await using var host = await SandboxHost.StartAsync();

        var result = await host.SendAsync<RegisterProvider, ProviderId>(OpenRouter() with { Name = name });

        Assert.Equal("validation.name", result.Error!.Code);
    }

    [Fact]
    public async Task Header_authentication_requires_a_header_name()
    {
        await using var host = await SandboxHost.StartAsync();

        var result = await host.SendAsync<RegisterProvider, ProviderId>(
            OpenRouter() with { AuthKind = AuthSchemeKind.Header, AuthHeaderName = " " });

        Assert.Equal("validation.auth", result.Error!.Code);
    }

    [Fact]
    public async Task Header_authentication_round_trips()
    {
        await using var host = await SandboxHost.StartAsync();

        await host.SendAsync<RegisterProvider, ProviderId>(
            new RegisterProvider("Custom", ProviderKind.Custom, "https://models.example.com", AuthSchemeKind.Header, "X-Api-Key", "abc"));

        var provider = Assert.Single(await ListAsync(host));
        Assert.Equal(AuthSchemeKind.Header, provider.Auth);
        Assert.Equal(SecretSource.Stored, provider.Key.Source);
        Assert.Equal("••••", provider.Key.MaskedHint);
    }
}

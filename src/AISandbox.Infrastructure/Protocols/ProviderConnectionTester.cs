using System.Net.Http.Headers;
using AISandbox.Application.Features.Providers;
using AISandbox.Domain.Catalog.Providers;

namespace AISandbox.Infrastructure.Protocols;

/// <summary>
/// Calls the cheapest read-only endpoint of each provider kind. Uses its own short-timeout client
/// so a test is never retried or slowed down by the pipeline that real calls go through.
/// </summary>
internal sealed class ProviderConnectionTester(IHttpClientFactory clients, TimeProvider time) : IProviderConnectionTester
{
    public const string ClientName = "provider-tests";

    public async Task<ConnectionTestResult> TestAsync(ConnectionTestRequest request, CancellationToken cancellationToken)
    {
        var url = TargetFor(request);
        using var message = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyAuth(message, request);

        var started = time.GetTimestamp();
        try
        {
            using var response = await clients.CreateClient(ClientName)
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var latency = time.GetElapsedTime(started);
            var status = (int)response.StatusCode;
            var ok = response.IsSuccessStatusCode;
            var message2 = ok
                ? $"OK, {status}, {(long)latency.TotalMilliseconds} ms"
                : $"{status} {response.ReasonPhrase ?? response.StatusCode.ToString()}".TrimEnd();
            return new ConnectionTestResult(ok, message2, status, latency);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ConnectionTestResult(false, "Timed out: the provider did not respond.", null, time.GetElapsedTime(started));
        }
        catch (HttpRequestException exception)
        {
            return new ConnectionTestResult(false, $"Could not connect: {exception.Message}", null, time.GetElapsedTime(started));
        }
    }

    private static Uri TargetFor(ConnectionTestRequest request) => request.Kind switch
    {
        ProviderKind.CloudflareWorkersAi when request.PathVariables.TryGetValue(Provider.AccountIdVariable, out var account) =>
            ProviderHttp.Combine(request.BaseUrl, $"accounts/{Uri.EscapeDataString(account)}/ai/models/search?per_page=1"),
        ProviderKind.CloudflareWorkersAi => ProviderHttp.Combine(request.BaseUrl, "user/tokens/verify"),
        ProviderKind.TypeSafe or ProviderKind.OpenRouter => ProviderHttp.Combine(request.BaseUrl, "models"),
        _ => request.BaseUrl.Value,
    };

    private static void ApplyAuth(HttpRequestMessage message, ConnectionTestRequest request)
    {
        if (string.IsNullOrEmpty(request.ApiKey))
        {
            return;
        }

        switch (request.Auth.Kind)
        {
            case AuthSchemeKind.Bearer:
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);
                break;
            case AuthSchemeKind.Header:
                message.Headers.TryAddWithoutValidation(request.Auth.HeaderName!, request.ApiKey);
                break;
        }
    }
}

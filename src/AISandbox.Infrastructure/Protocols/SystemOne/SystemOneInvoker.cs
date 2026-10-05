using System.Text.Json;
using AISandbox.Application.Abstractions;
using AISandbox.Domain.Catalog.Models;
using AISandbox.Domain.Experimentation;

namespace AISandbox.Infrastructure.Protocols.SystemOne;

/// <summary>
/// Calls a System One endpoint directly: POST {baseUrl}/systemone.
/// </summary>
internal sealed class SystemOneInvoker(ProviderHttp http) : IModelInvoker
{
    public ProtocolId Protocol => ProtocolId.SystemOne;

    public async Task<InvocationOutcome> InvokeAsync(InvocationRequest request, CancellationToken cancellationToken)
    {
        var body = SystemOneContract.BuildRequest(request.RemoteId.Value, request.Input);
        var (response, failure) = await http.PostJsonAsync(
            ProviderHttp.Combine(request.BaseUrl, "systemone"), request, body, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        try
        {
            using var document = JsonDocument.Parse(response!.Body);
            var parsed = SystemOneContract.ParseResponse(document.RootElement);
            return new InvocationSucceeded(new InvocationSuccess(
                parsed.Output, parsed.Usage, null, response.Latency, parsed.ResolvedModel, body, response.Body, response.Attempts));
        }
        catch (Exception exception) when (exception is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            return ProviderHttp.InvalidResponse(body, response!, exception.Message);
        }
    }
}

using System.Text.Json;
using AISandbox.Application.Abstractions;
using AISandbox.Domain.Abstractions;
using AISandbox.Domain.Catalog.Models;
using AISandbox.Domain.Experimentation;

namespace AISandbox.Infrastructure.Protocols.SystemOne;

/// <summary>
/// Calls OpenRouter's stable System One endpoint: POST {baseUrl}/systemone. The response adds
/// <c>id</c>, <c>provider</c> and <c>usage.cost</c> (USD charged), which becomes the execution cost.
/// </summary>
internal sealed class OpenRouterDecisionsInvoker(ProviderHttp http) : IModelInvoker
{
    public ProtocolId Protocol => ProtocolId.OpenRouterDecisions;

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
            Money? cost = parsed.Cost is { } amount ? new Money(amount, Money.Usd) : null;
            return new InvocationSucceeded(new InvocationSuccess(
                parsed.Output, parsed.Usage, cost, response.Latency, parsed.ResolvedModel, body, response.Body));
        }
        catch (Exception exception) when (exception is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            return ProviderHttp.InvalidResponse(body, response!, exception.Message);
        }
    }
}

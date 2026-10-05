using System.Text.Json;
using AISandbox.Application.Abstractions;
using AISandbox.Domain.Catalog.Models;
using AISandbox.Domain.Catalog.Providers;
using AISandbox.Domain.Experimentation;

namespace AISandbox.Infrastructure.Protocols.SystemOne;

/// <summary>
/// Calls Cloudflare Workers AI: POST {baseUrl}/accounts/{account_id}/ai/run/@cf/cloudflare/{model}.
/// The body is the System One body (its <c>model</c> field is the selector) and the response is
/// wrapped in Cloudflare's <c>{ result, success, errors, messages }</c> envelope.
/// </summary>
internal sealed class WorkersAiInvoker(ProviderHttp http) : IModelInvoker
{
    public ProtocolId Protocol => ProtocolId.WorkersAi;

    public async Task<InvocationOutcome> InvokeAsync(InvocationRequest request, CancellationToken cancellationToken)
    {
        var body = SystemOneContract.BuildRequest(request.RemoteId.Value, request.Input);
        if (request.PathVariables is null
            || !request.PathVariables.TryGetValue(Provider.AccountIdVariable, out var accountId)
            || string.IsNullOrWhiteSpace(accountId))
        {
            return new InvocationFailed(
                new ExecutionError("configuration", "The Cloudflare provider has no account id. Add it to the provider and try again.", null),
                null, null, null);
        }

        var path = $"accounts/{Uri.EscapeDataString(accountId)}/ai/run/@cf/cloudflare/{request.RemoteId.Value}";
        var (response, failure) = await http.PostJsonAsync(ProviderHttp.Combine(request.BaseUrl, path), request, body, cancellationToken);
        if (failure is not null)
        {
            return Unwrap(failure);
        }

        try
        {
            using var document = JsonDocument.Parse(response!.Body);
            var root = document.RootElement;
            if (EnvelopeError(root) is { } error)
            {
                return new InvocationFailed(error, response.Latency, body, response.Body, response.Attempts);
            }

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("result", out var result))
            {
                return ProviderHttp.InvalidResponse(body, response, "missing 'result' in the Cloudflare envelope");
            }

            var parsed = SystemOneContract.ParseResponse(result);
            return new InvocationSucceeded(new InvocationSuccess(
                parsed.Output, parsed.Usage, null, response.Latency, parsed.ResolvedModel, body, response.Body, response.Attempts));
        }
        catch (Exception exception) when (exception is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            return ProviderHttp.InvalidResponse(body, response!, exception.Message);
        }
    }

    /// <summary>Non-2xx responses carry the envelope too; prefer its error over the generic http one.</summary>
    private static InvocationFailed Unwrap(InvocationFailed failure)
    {
        if (failure.RawResponse is null || failure.Error.Code == ExecutionError.RateLimitedCode)
        {
            return failure;
        }

        try
        {
            using var document = JsonDocument.Parse(failure.RawResponse);
            return EnvelopeError(document.RootElement, failure.Error.HttpStatus) is { } error ? failure with { Error = error } : failure;
        }
        catch (JsonException)
        {
            return failure;
        }
    }

    private static ExecutionError? EnvelopeError(JsonElement root, int? httpStatus = null)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var failed = root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False;
        if (!failed && httpStatus is null)
        {
            return null;
        }

        if (!root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() == 0)
        {
            return failed ? new ExecutionError("cloudflare.unknown", "Cloudflare reported a failure without an error message.", httpStatus) : null;
        }

        var first = errors[0];
        var message = first.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        var code = first.TryGetProperty("code", out var c) ? c.ToString() : "unknown";
        return new ExecutionError($"cloudflare.{code}", string.IsNullOrWhiteSpace(message) ? "Cloudflare reported an error." : message, httpStatus);
    }
}

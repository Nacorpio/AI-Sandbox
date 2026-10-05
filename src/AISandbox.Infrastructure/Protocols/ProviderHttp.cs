using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AISandbox.Application.Abstractions;
using AISandbox.Domain.Catalog.Providers;
using AISandbox.Domain.Experimentation;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace AISandbox.Infrastructure.Protocols;

/// <summary>
/// Sends one JSON request to a provider and turns transport problems into
/// <see cref="InvocationFailed"/> outcomes, so each protocol only deals with its own contract.
/// </summary>
internal sealed class ProviderHttp(IHttpClientFactory clients, TimeProvider time)
{
    public const string ClientName = "providers";
    private const int MaxErrorMessageLength = 500;

    public sealed record Response(string Body, Latency Latency, int Attempts = 1);

    public async Task<(Response? Response, InvocationFailed? Failure)> PostJsonAsync(
        Uri url,
        InvocationRequest request,
        string body,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        ApplyAuth(message, request);
        var attempts = new AttemptCounter();
        message.Options.Set(AttemptCounter.Key, attempts);

        var client = clients.CreateClient(ClientName);
        var started = time.GetTimestamp();
        TimeSpan? firstByte = null;
        try
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            firstByte = time.GetElapsedTime(started);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var latency = new Latency(time.GetElapsedTime(started), firstByte);

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                var error = status == 429
                    ? new ExecutionError(ExecutionError.RateLimitedCode, DescribeRateLimit(status, responseBody, response), status)
                    : new ExecutionError($"http.{status}", DescribeError(status, responseBody), status);
                return (null, new InvocationFailed(error, latency, body, responseBody, attempts.Count));
            }

            return (new Response(responseBody, latency, attempts.Count), null);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutRejectedException && !cancellationToken.IsCancellationRequested)
        {
            return (null, Failed("timeout", "The provider did not respond in time."));
        }
        catch (BrokenCircuitException)
        {
            return (null, Failed(ExecutionError.CircuitOpenCode, "Too many recent failures from this provider; calls are paused briefly. Try again shortly."));
        }
        catch (HttpRequestException exception)
        {
            return (null, Failed("network", exception.Message));
        }

        InvocationFailed Failed(string code, string message) =>
            new(new ExecutionError(code, message, null), new Latency(time.GetElapsedTime(started), firstByte), body, null, attempts.Count);
    }

    public static InvocationFailed InvalidResponse(string requestBody, Response response, string reason) =>
        new(new ExecutionError("response.invalid", $"The provider's response did not match the protocol: {reason}", null),
            response.Latency, requestBody, response.Body, response.Attempts);

    public static Uri Combine(EndpointUri baseUrl, string path) =>
        new($"{baseUrl.Value.ToString().TrimEnd('/')}/{path.TrimStart('/')}");

    private static void ApplyAuth(HttpRequestMessage message, InvocationRequest request)
    {
        if (request.ApiKey is null)
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

    private static string DescribeRateLimit(int status, string body, HttpResponseMessage response)
    {
        var message = DescribeError(status, body);
        var retryAfter = response.Headers.RetryAfter is { } header
            ? header.Delta is { } delta ? $"{(int)Math.Ceiling(delta.TotalSeconds)} s" : header.Date?.ToString("R")
            : null;
        return retryAfter is null ? message : $"{message} (retry-after: {retryAfter})";
    }

    /// <summary>
    /// Pulls a readable message out of common error shapes ({"error":{"message"}}, {"error":"..."},
    /// {"message"}, {"detail"}), falling back to the status code.
    /// </summary>
    private static string DescribeError(int status, string body)
    {
        string? message = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            message = root.ValueKind != JsonValueKind.Object ? null
                : root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var nested) ? nested.GetString()
                : root.TryGetProperty("error", out error) && error.ValueKind == JsonValueKind.String ? error.GetString()
                : root.TryGetProperty("message", out var plain) && plain.ValueKind == JsonValueKind.String ? plain.GetString()
                : root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String ? detail.GetString()
                : null;
        }
        catch (JsonException)
        {
            message = string.IsNullOrWhiteSpace(body) ? null : body;
        }

        message = string.IsNullOrWhiteSpace(message) ? $"The provider returned HTTP {status}." : $"HTTP {status}: {message}";
        return message.Length > MaxErrorMessageLength ? message[..MaxErrorMessageLength] + "…" : message;
    }
}

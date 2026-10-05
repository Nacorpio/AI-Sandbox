using System.Buffers;
using System.Text;
using System.Text.Json;
using AISandbox.Application.Abstractions;
using AISandbox.Domain.Abstractions;
using AISandbox.Domain.Catalog.Models;
using AISandbox.Domain.Experimentation;

namespace AISandbox.Infrastructure.Protocols.Chat;

/// <summary>
/// Calls an OpenAI-compatible endpoint: POST {baseUrl}/chat/completions. Reads the answer,
/// the reasoning (<c>reasoning</c> or <c>reasoning_content</c>), token usage and, when the
/// provider reports one (OpenRouter), the cost in USD.
/// </summary>
internal sealed class OpenAiChatInvoker(ProviderHttp http) : IModelInvoker
{
    public ProtocolId Protocol => ProtocolId.OpenAiChat;

    public async Task<InvocationOutcome> InvokeAsync(InvocationRequest request, CancellationToken cancellationToken)
    {
        if (request.Input.Chat is not { } prompt)
        {
            return new InvocationFailed(new ExecutionError("input.missing", "The run has no prompt for this chat model.", null), null, null, null);
        }

        var options = request.ChatOptions ?? new ChatOptions();
        var sendSchema = prompt.OutputSchema is not null && request.Capabilities?.SupportsStructuredOutputs == true;
        var body = BuildRequest(request.RemoteId.Value, prompt, options, sendSchema);

        var (response, failure) = await http.PostJsonAsync(
            ProviderHttp.Combine(request.BaseUrl, "chat/completions"), request, body, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        try
        {
            using var document = JsonDocument.Parse(response!.Body);
            var parsed = Parse(document.RootElement, prompt.OutputSchema is not null);
            return new InvocationSucceeded(new InvocationSuccess(
                parsed.Output, parsed.Usage, parsed.Cost, response.Latency, parsed.ResolvedModel, body, response.Body));
        }
        catch (Exception exception) when (exception is JsonException or FormatException or KeyNotFoundException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            return ProviderHttp.InvalidResponse(body, response!, exception.Message);
        }
    }

    internal static string BuildRequest(string model, ChatPrompt prompt, ChatOptions options, bool sendJsonSchema)
    {
        var system = prompt.System;
        if (prompt.OutputSchema is { } schema && !sendJsonSchema)
        {
            // Without native structured outputs, ask for the shape in words.
            system = $"{system}\n\nRespond only with JSON that matches this JSON Schema:\n{schema.Json}".Trim();
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            writer.WriteStartArray("messages");
            if (!string.IsNullOrWhiteSpace(system))
            {
                WriteMessage(writer, "system", system);
            }

            WriteMessage(writer, "user", prompt.User);
            writer.WriteEndArray();

            if (options.Temperature is { } temperature)
            {
                writer.WriteNumber("temperature", temperature);
            }

            if (options.MaxTokens is { } maxTokens)
            {
                writer.WriteNumber("max_tokens", maxTokens);
            }

            if (!string.IsNullOrWhiteSpace(options.ReasoningEffort))
            {
                writer.WriteString("reasoning_effort", options.ReasoningEffort);
            }

            if (sendJsonSchema)
            {
                writer.WriteStartObject("response_format");
                writer.WriteString("type", "json_schema");
                writer.WriteStartObject("json_schema");
                writer.WriteString("name", "response");
                writer.WritePropertyName("schema");
                writer.WriteRawValue(prompt.OutputSchema!.Json);
                writer.WriteBoolean("strict", true);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            else if (options.ResponseFormat == ResponseFormat.JsonObject)
            {
                writer.WriteStartObject("response_format");
                writer.WriteString("type", "json_object");
                writer.WriteEndObject();
            }

            if (options.Upstream is { } upstream)
            {
                WriteProviderPreference(writer, upstream);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // OpenRouter's "provider" routing object.
    private static void WriteProviderPreference(Utf8JsonWriter writer, UpstreamPreference upstream)
    {
        switch (upstream.Kind)
        {
            case UpstreamPreferenceKind.Cheapest:
                writer.WriteStartObject("provider");
                writer.WriteString("sort", "price");
                writer.WriteEndObject();
                break;
            case UpstreamPreferenceKind.Fastest:
                writer.WriteStartObject("provider");
                writer.WriteString("sort", "throughput");
                writer.WriteEndObject();
                break;
            case UpstreamPreferenceKind.Pinned when !string.IsNullOrWhiteSpace(upstream.Name):
                writer.WriteStartObject("provider");
                writer.WriteStartArray("order");
                writer.WriteStringValue(upstream.Name.Trim());
                writer.WriteEndArray();
                writer.WriteBoolean("allow_fallbacks", false);
                writer.WriteEndObject();
                break;
        }
    }

    private static void WriteMessage(Utf8JsonWriter writer, string role, string content)
    {
        writer.WriteStartObject();
        writer.WriteString("role", role);
        writer.WriteString("content", content);
        writer.WriteEndObject();
    }

    private sealed record Parsed(ChatOutput Output, TokenUsage Usage, Money? Cost, string? ResolvedModel);

    private static Parsed Parse(JsonElement root, bool expectStructured)
    {
        var message = root.GetProperty("choices")[0].GetProperty("message");
        var text = ReadContent(message);
        var reasoning = ReadString(message, "reasoning") ?? ReadString(message, "reasoning_content");

        string? structuredJson = null;
        if (expectStructured && TryParseJson(text, out var json))
        {
            structuredJson = json;
        }

        var usage = TokenUsage.None;
        Money? cost = null;
        int? reasoningTokens = null;
        if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
        {
            usage = new TokenUsage(ReadInt(u, "prompt_tokens") ?? 0, ReadInt(u, "completion_tokens") ?? 0);
            if (u.TryGetProperty("cost", out var c) && c.ValueKind == JsonValueKind.Number)
            {
                cost = new Money(c.GetDecimal(), Money.Usd);
            }

            if (u.TryGetProperty("completion_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object)
            {
                reasoningTokens = ReadInt(details, "reasoning_tokens");
            }
        }

        return new Parsed(new ChatOutput(text, structuredJson, reasoning, reasoningTokens), usage, cost, ReadString(root, "model"));
    }

    private static string ReadContent(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content))
        {
            return string.Empty;
        }

        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString() ?? string.Empty,
            JsonValueKind.Array => string.Concat(content.EnumerateArray()
                .Select(part => part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null)),
            _ => string.Empty,
        };
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(value.GetString())
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    // Models sometimes wrap JSON in a markdown fence even when asked not to.
    private static bool TryParseJson(string text, out string json)
    {
        var candidate = text.Trim();
        if (candidate.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = candidate.IndexOf('\n');
            var lastFence = candidate.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine > 0 && lastFence > firstLine)
            {
                candidate = candidate[(firstLine + 1)..lastFence].Trim();
            }
        }

        try
        {
            using var document = JsonDocument.Parse(candidate);
            json = document.RootElement.GetRawText();
            return document.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
        }
        catch (JsonException)
        {
            json = string.Empty;
            return false;
        }
    }
}

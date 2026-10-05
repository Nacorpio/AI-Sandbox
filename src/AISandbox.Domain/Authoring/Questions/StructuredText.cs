using System.Text.Encodings.Web;
using System.Text.Json;
using AISandbox.Domain.Abstractions;

namespace AISandbox.Domain.Authoring.Questions;

/// <summary>
/// The value of an instruction, option, level or criterion: plain text, or any JSON object or
/// array. Kept as canonical JSON so it can be sent to a provider exactly as authored.
/// </summary>
public sealed record StructuredText
{
    // Keep apostrophes and non-ASCII text readable in stored and sent payloads. Output is JSON, never HTML.
    private static readonly JsonSerializerOptions TextOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private StructuredText(string json, bool isPlainText)
    {
        Json = json;
        IsPlainText = isPlainText;
    }

    /// <summary>
    /// The value as JSON: a string literal for plain text, otherwise the object or array.
    /// </summary>
    public string Json { get; }

    public bool IsPlainText { get; }

    public static Result<StructuredText> FromText(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? Error.Validation("text", "Text is required.")
            : new StructuredText(JsonSerializer.Serialize(text.Trim(), TextOptions), isPlainText: true);

    /// <summary>
    /// Accepts a JSON string, object or array. Anything else (numbers, booleans, null) is rejected.
    /// </summary>
    public static Result<StructuredText> FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Error.Validation("json", "JSON is required.");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.ValueKind switch
            {
                JsonValueKind.String => FromText(root.GetString()),
                JsonValueKind.Object or JsonValueKind.Array => new StructuredText(root.GetRawText(), isPlainText: false),
                _ => Error.Validation("json", "Structured text must be a string, an object or an array."),
            };
        }
        catch (JsonException exception)
        {
            return Error.Validation("json", $"Invalid JSON: {exception.Message}");
        }
    }

    /// <summary>
    /// Plain text as typed, or the JSON for structured values.
    /// </summary>
    public string Display => IsPlainText ? JsonSerializer.Deserialize<string>(Json)! : Json;

    public override string ToString() => Display;
}

using System.Buffers;
using System.Text;
using System.Text.Json;
using AISandbox.Domain.Authoring.Questions;
using AISandbox.Domain.Experimentation;

namespace AISandbox.Infrastructure.Protocols.SystemOne;

/// <summary>
/// Builds and reads System One bodies. Shared by every protocol that wraps the System One
/// contract (direct, Workers AI, OpenRouter).
/// </summary>
internal static class SystemOneContract
{
    /// <param name="extraProperties">Writes protocol-specific top-level properties, such as Workers AI's model selector.</param>
    public static string BuildRequest(string model, RunInput input, Action<Utf8JsonWriter>? extraProperties = null)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            extraProperties?.Invoke(writer);
            writer.WritePropertyName("state");
            writer.WriteRawValue((input.State ?? throw new InvalidOperationException("The run has no state for a decision model.")).Json);
            writer.WriteStartObject("questions");
            foreach (var question in input.Questions)
            {
                WriteQuestion(writer, question);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteQuestion(Utf8JsonWriter writer, Question question)
    {
        writer.WriteStartObject(question.Key.Value);
        writer.WriteString("type", question.Type.ToString().ToLowerInvariant());
        writer.WritePropertyName("instructions");
        writer.WriteRawValue(question.Instructions.Json);

        switch (question)
        {
            case ChoiceQuestion choice:
                writer.WriteStartObject("criteria");
                foreach (var option in choice.Options)
                {
                    writer.WritePropertyName(option.Name);
                    WriteEntry(writer, option.Description);
                }

                writer.WriteEndObject();
                break;
            case ScoreQuestion score:
                writer.WriteStartArray("criteria");
                foreach (var level in score.Levels)
                {
                    writer.WriteRawValue(level.Json);
                }

                writer.WriteEndArray();
                break;
            case NoulQuestion { WhenTrue: not null } or NoulQuestion { WhenFalse: not null }:
                var noul = (NoulQuestion)question;
                writer.WriteStartObject("criteria");
                writer.WritePropertyName("true");
                WriteEntry(writer, noul.WhenTrue);
                writer.WritePropertyName("false");
                WriteEntry(writer, noul.WhenFalse);
                writer.WriteEndObject();
                break;
        }

        writer.WriteEndObject();
    }

    private static void WriteEntry(Utf8JsonWriter writer, StructuredText? entry)
    {
        if (entry is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteRawValue(entry.Json);
        }
    }

    public sealed record ParsedResponse(DecisionOutput Output, TokenUsage Usage, string? ResolvedModel, decimal? Cost = null);

    /// <summary>
    /// Reads the System One response body. Throws <see cref="JsonException"/> or
    /// <see cref="FormatException"/> when the body does not follow the contract.
    /// </summary>
    public static ParsedResponse ParseResponse(JsonElement root)
    {
        if (!root.TryGetProperty("answers", out var answersElement) || answersElement.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("Response has no 'answers' object.");
        }

        var answers = new Dictionary<string, Answer>(StringComparer.Ordinal);
        foreach (var property in answersElement.EnumerateObject())
        {
            answers[property.Name] = ParseAnswer(property.Name, property.Value);
        }

        var usage = TokenUsage.None;
        decimal? cost = null;
        if (root.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object)
        {
            usage = new TokenUsage(ReadInt(usageElement, "input_tokens"), ReadInt(usageElement, "output_tokens"));
            if (usageElement.TryGetProperty("cost", out var costElement) && costElement.ValueKind == JsonValueKind.Number)
            {
                cost = costElement.GetDecimal();
            }
        }

        var resolvedModel = root.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String
            ? model.GetString()
            : null;

        return new ParsedResponse(new DecisionOutput(answers), usage, resolvedModel, cost);
    }

    private static Answer ParseAnswer(string key, JsonElement element)
    {
        var type = element.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
        return type switch
        {
            "choice" => new ChoiceAnswer(
                element.GetProperty("choice").GetString() ?? throw new FormatException($"Answer '{key}' has no choice."),
                ReadOptionalDouble(element, "confidence"),
                ReadNumberMap(element, "probabilities")),
            "score" => new ScoreAnswer(
                element.GetProperty("score").GetDouble(),
                ReadOptionalDouble(element, "confidence"),
                ReadLegend(element),
                ReadNumberMap(element, "probabilities")),
            "noul" => new NoulAnswer(element.GetProperty("noul").GetDouble()),
            _ => throw new FormatException($"Answer '{key}' has unknown type '{type}'."),
        };
    }

    private static int ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

    private static double? ReadOptionalDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static Dictionary<string, double> ReadNumberMap(JsonElement element, string name)
    {
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                map[property.Name] = property.Value.GetDouble();
            }
        }

        return map;
    }

    private static Dictionary<string, string> ReadLegend(JsonElement element)
    {
        var legend = new Dictionary<string, string>(StringComparer.Ordinal);
        if (element.TryGetProperty("legend", out var value) && value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                legend[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()!
                    : property.Value.GetRawText();
            }
        }

        return legend;
    }
}

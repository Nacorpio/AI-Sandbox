using System.Text.Json;
using System.Text.RegularExpressions;
using AISandbox.Domain.Abstractions;
using AISandbox.Domain.Catalog.Models;
using AISandbox.Domain.Experimentation;

namespace AISandbox.Domain.Authoring.Prompts;

public readonly record struct PromptTemplateId(Guid Value)
{
    public static PromptTemplateId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// The <c>{{placeholder}}</c> syntax shared by system and user prompts. Names are letters, digits
/// and underscores.
/// </summary>
public static partial class PlaceholderSyntax
{
    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_]+)\s*\}\}")]
    private static partial Regex Pattern();

    public static IReadOnlyList<string> In(params string?[] texts) =>
        texts.Where(t => t is not null)
            .SelectMany(t => Pattern().Matches(t!).Select(m => m.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    public static string Fill(string text, IReadOnlyDictionary<string, string> values) =>
        Pattern().Replace(text, m => values[m.Groups[1].Value]);
}

/// <summary>
/// A reusable prompt for chat models: an optional system prompt, a user prompt with
/// <c>{{placeholders}}</c> and an optional JSON schema for the output.
/// </summary>
public sealed class PromptTemplate : AggregateRoot<PromptTemplateId>
{
    public const int MaxNameLength = 100;

    private PromptTemplate(PromptTemplateId id, DateTimeOffset now)
        : base(id)
    {
        Name = null!;
        UserPrompt = null!;
        CreatedAt = now;
        UpdatedAt = now;
    }

    // Used by persistence to materialise the aggregate.
    private PromptTemplate()
        : this(default, default)
    {
    }

    public string Name { get; private set; }

    public string? SystemPrompt { get; private set; }

    public string UserPrompt { get; private set; }

    public JsonSchemaDocument? OutputSchema { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<string> Placeholders => PlaceholderSyntax.In(SystemPrompt, UserPrompt);

    public static Result<PromptTemplate> Create(
        string? name,
        string? systemPrompt,
        string? userPrompt,
        string? outputSchemaJson,
        DateTimeOffset now)
    {
        var template = new PromptTemplate(PromptTemplateId.New(), now);
        var applied = template.Apply(name, systemPrompt, userPrompt, outputSchemaJson);
        return applied.IsFailure ? applied.Error! : template;
    }

    public Result Update(string? name, string? systemPrompt, string? userPrompt, string? outputSchemaJson, DateTimeOffset now)
    {
        var applied = Apply(name, systemPrompt, userPrompt, outputSchemaJson);
        if (applied.IsSuccess)
        {
            UpdatedAt = now;
        }

        return applied;
    }

    /// <summary>
    /// Fills every placeholder. A placeholder without a value (or with a blank one) is an error.
    /// Values for unknown placeholders are ignored.
    /// </summary>
    public Result<ChatPrompt> Render(IReadOnlyDictionary<string, string>? values)
    {
        var missing = Placeholders
            .Where(p => values is null || !values.TryGetValue(p, out var v) || string.IsNullOrWhiteSpace(v))
            .ToList();
        if (missing.Count > 0)
        {
            return Error.Validation("placeholders", $"Missing value for {string.Join(", ", missing.Select(m => $"{{{{{m}}}}}"))}.");
        }

        return new ChatPrompt(
            SystemPrompt is null ? null : PlaceholderSyntax.Fill(SystemPrompt, values!),
            PlaceholderSyntax.Fill(UserPrompt, values!),
            OutputSchema,
            new PromptTemplateRef(Id, Name));
    }

    private Result Apply(string? name, string? systemPrompt, string? userPrompt, string? outputSchemaJson)
    {
        var trimmedName = name?.Trim();
        if (string.IsNullOrEmpty(trimmedName) || trimmedName.Length > MaxNameLength)
        {
            return Result.Failure(Error.Validation("name", $"Name is required and must be at most {MaxNameLength} characters."));
        }

        if (string.IsNullOrWhiteSpace(userPrompt))
        {
            return Result.Failure(Error.Validation("userPrompt", "User prompt is required."));
        }

        JsonSchemaDocument? schema = null;
        if (!string.IsNullOrWhiteSpace(outputSchemaJson))
        {
            try
            {
                using var document = JsonDocument.Parse(outputSchemaJson);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return Result.Failure(Error.Validation("outputSchema", "Output schema must be a JSON object."));
                }

                schema = new JsonSchemaDocument(document.RootElement.GetRawText());
            }
            catch (JsonException exception)
            {
                return Result.Failure(Error.Validation("outputSchema", $"Output schema is not valid JSON: {exception.Message}"));
            }
        }

        Name = trimmedName;
        SystemPrompt = string.IsNullOrWhiteSpace(systemPrompt) ? null : systemPrompt.Trim();
        UserPrompt = userPrompt.Trim();
        OutputSchema = schema;
        return Result.Success();
    }
}

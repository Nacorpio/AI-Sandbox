using AISandbox.Domain.Abstractions;

namespace AISandbox.Domain.Catalog.Models;

public readonly record struct ModelDefinitionId(Guid Value)
{
    public static ModelDefinitionId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

public enum ModelKind
{
    Chat,
    Decision,
}

/// <summary>
/// Identifies a wire contract. Each protocol has exactly one invoker.
/// </summary>
public sealed record ProtocolId
{
    private ProtocolId(string value) => Value = value;

    public string Value { get; }

    public static ProtocolId SystemOne { get; } = new("systemone");

    public static ProtocolId WorkersAi { get; } = new("workers-ai");

    public static ProtocolId OpenRouterDecisions { get; } = new("openrouter-decisions");

    public static ProtocolId OpenAiChat { get; } = new("openai-chat");

    public static ProtocolId Custom { get; } = new("custom");

    public static IReadOnlyList<ProtocolId> All { get; } = [SystemOne, WorkersAi, OpenRouterDecisions, OpenAiChat, Custom];

    public bool IsDecisionProtocol => this == SystemOne || this == WorkersAi || this == OpenRouterDecisions;

    public static Result<ProtocolId> Parse(string? value) =>
        All.FirstOrDefault(p => p.Value == value) is { } protocol
            ? protocol
            : Error.Validation("protocol", $"Unknown protocol '{value}'.");

    public override string ToString() => Value;
}

/// <summary>
/// The model name the provider expects in its request, for example "jev-latest".
/// </summary>
public sealed record RemoteModelId
{
    private RemoteModelId(string value) => Value = value;

    public string Value { get; }

    public static Result<RemoteModelId> Create(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 200
            ? Error.Validation("remoteId", "Remote model id is required and must be at most 200 characters.")
            : new RemoteModelId(value.Trim());

    public override string ToString() => Value;
}

/// <summary>
/// What a model route accepts. Stored per provider plus model, because limits differ by route.
/// </summary>
public sealed record ModelCapabilities(
    bool AcceptsImages,
    int MaxQuestions,
    int? ContextTokens,
    IReadOnlyList<string> QuestionTypes)
{
    public static ModelCapabilities ChatDefaults { get; } = new(false, 0, null, []);
}

/// <summary>
/// A JSON Schema document kept as raw text. Parsing and validation happen in Infrastructure.
/// </summary>
public sealed record JsonSchemaDocument(string Json)
{
    public static JsonSchemaDocument Empty { get; } = new("{}");
}

public sealed record TemplateOrigin(string TemplateId, int Version);

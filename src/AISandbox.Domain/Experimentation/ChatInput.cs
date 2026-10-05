using AISandbox.Domain.Authoring.Prompts;
using AISandbox.Domain.Catalog.Models;

namespace AISandbox.Domain.Experimentation;

/// <summary>
/// Where a prompt came from, when it was rendered from a saved template.
/// </summary>
public sealed record PromptTemplateRef(PromptTemplateId Id, string Name);

/// <summary>
/// What a chat model is asked: the rendered system and user text and the schema the answer should
/// follow, if any.
/// </summary>
public sealed record ChatPrompt(
    string? System,
    string User,
    JsonSchemaDocument? OutputSchema = null,
    PromptTemplateRef? Template = null);

public enum UpstreamPreferenceKind
{
    Cheapest,
    Fastest,
    Pinned,
}

/// <summary>
/// Which upstream provider an aggregator such as OpenRouter should route to.
/// </summary>
public sealed record UpstreamPreference(UpstreamPreferenceKind Kind, string? Name = null);

public enum ResponseFormat
{
    Text,
    JsonObject,
}

/// <summary>
/// Per-execution knobs for a chat model. Unset values are left to the provider's defaults.
/// </summary>
public sealed record ChatOptions(
    double? Temperature = null,
    int? MaxTokens = null,
    string? ReasoningEffort = null,
    ResponseFormat? ResponseFormat = null,
    UpstreamPreference? Upstream = null);

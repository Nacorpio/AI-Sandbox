using AISandbox.Domain.Experimentation;

namespace AISandbox.Web.Components.Shared;

/// <summary>
/// The "More options" form of one chat model in the run dialog.
/// </summary>
public sealed class ChatOptionsDraft
{
    public static readonly string[] ReasoningEfforts = ["low", "medium", "high"];

    public double? Temperature { get; set; }

    public int? MaxTokens { get; set; }

    public string? ReasoningEffort { get; set; }

    public bool JsonObject { get; set; }

    public UpstreamPreferenceKind? Upstream { get; set; }

    public string? PinnedName { get; set; }

    public ChatOptions ToOptions() => new(
        Temperature,
        MaxTokens,
        string.IsNullOrWhiteSpace(ReasoningEffort) ? null : ReasoningEffort,
        JsonObject ? ResponseFormat.JsonObject : null,
        Upstream is { } kind ? new UpstreamPreference(kind, kind == UpstreamPreferenceKind.Pinned ? PinnedName : null) : null);
}

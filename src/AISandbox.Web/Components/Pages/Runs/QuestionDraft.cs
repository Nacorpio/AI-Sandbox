using System.Text.RegularExpressions;
using AISandbox.Application.Features.Runs;
using AISandbox.Domain.Authoring.Questions;

namespace AISandbox.Web.Components.Pages.Runs;

/// <summary>
/// Mutable editing state for one question in the run dialog.
/// </summary>
public sealed partial class QuestionDraft
{
    public QuestionType Type { get; set; } = QuestionType.Noul;

    public string? Key { get; set; }

    public string? Instructions { get; set; }

    public List<OptionDraft> Options { get; } = [new(), new()];

    public List<TextDraft> Levels { get; } = [new(), new(), new()];

    public string? WhenTrue { get; set; }

    public string? WhenFalse { get; set; }

    /// <summary>
    /// Suggests a key from the instructions while the user has not typed one.
    /// </summary>
    public void SuggestKey()
    {
        if (!string.IsNullOrWhiteSpace(Key) || string.IsNullOrWhiteSpace(Instructions) || Instructions.TrimStart().StartsWith('{'))
        {
            return;
        }

        var words = NonWord().Split(Instructions.ToLowerInvariant()).Where(w => w.Length > 0).Take(4);
        Key = string.Join('_', words) is { Length: > 0 } key ? key[..Math.Min(key.Length, 60)] : null;
    }

    public QuestionInput ToInput() => new(
        Type,
        Key,
        Instructions,
        Options.Select(o => new OptionInput(o.Name, o.Description)).ToList(),
        Levels.Select(l => l.Text).ToList(),
        WhenTrue,
        WhenFalse);

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonWord();
}

public sealed class OptionDraft
{
    public string? Name { get; set; }

    public string? Description { get; set; }
}

public sealed class TextDraft
{
    public string? Text { get; set; }
}

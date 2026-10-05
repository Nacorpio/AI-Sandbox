using System.Text.RegularExpressions;
using AISandbox.Application.Features.Runs;
using AISandbox.Domain.Authoring.Questions;

namespace AISandbox.Web.Components.Shared;

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

    public static QuestionDraft FromInput(QuestionInput input)
    {
        var draft = new QuestionDraft { Type = input.Type, Key = input.Key, Instructions = input.Instructions, WhenTrue = input.WhenTrue, WhenFalse = input.WhenFalse };
        if (input.Options is { Count: > 0 })
        {
            draft.Options.Clear();
            draft.Options.AddRange(input.Options.Select(o => new OptionDraft { Name = o.Name, Description = o.Description }));
        }

        if (input.Levels is { Count: > 0 })
        {
            draft.Levels.Clear();
            draft.Levels.AddRange(input.Levels.Select(l => new TextDraft { Text = l }));
        }

        return draft;
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

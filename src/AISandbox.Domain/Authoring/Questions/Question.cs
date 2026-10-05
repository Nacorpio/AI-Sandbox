using System.Text.RegularExpressions;
using AISandbox.Domain.Abstractions;

namespace AISandbox.Domain.Authoring.Questions;

/// <summary>
/// Names a question within a request. Follows the strictest provider rule known (Clef):
/// letters, digits, underscore, dot and hyphen, 1 to 100 characters.
/// </summary>
public sealed partial record QuestionKey
{
    private QuestionKey(string value) => Value = value;

    public string Value { get; }

    public static Result<QuestionKey> Create(string? value) =>
        value is not null && KeyPattern().IsMatch(value)
            ? new QuestionKey(value)
            : Error.Validation("key", $"Question key '{value}' must be 1–100 letters, digits, '_', '.' or '-'.");

    public override string ToString() => Value;

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,100}$")]
    private static partial Regex KeyPattern();
}

public enum QuestionType
{
    Choice,
    Score,
    Noul,
}

/// <summary>
/// One typed decision request. The set of subtypes is closed: choice, score and noul.
/// </summary>
public abstract record Question(QuestionKey Key, StructuredText Instructions)
{
    public abstract QuestionType Type { get; }
}

/// <summary>
/// Pick one named option. Option descriptions are optional.
/// </summary>
public sealed record ChoiceQuestion : Question
{
    public const int MinOptions = 2;

    private ChoiceQuestion(QuestionKey key, StructuredText instructions, IReadOnlyList<ChoiceOption> options)
        : base(key, instructions) => Options = options;

    public IReadOnlyList<ChoiceOption> Options { get; }

    public override QuestionType Type => QuestionType.Choice;

    public static Result<ChoiceQuestion> Create(QuestionKey key, StructuredText instructions, IReadOnlyList<ChoiceOption> options)
    {
        if (options.Count < MinOptions)
        {
            return Error.Validation("options", $"Choice question '{key}' needs at least {MinOptions} options.");
        }

        if (options.Select(o => o.Name).Distinct(StringComparer.Ordinal).Count() != options.Count)
        {
            return Error.Validation("options", $"Choice question '{key}' has duplicate option names.");
        }

        return new ChoiceQuestion(key, instructions, options);
    }
}

public sealed record ChoiceOption
{
    private ChoiceOption(string name, StructuredText? description)
    {
        Name = name;
        Description = description;
    }

    public string Name { get; }

    public StructuredText? Description { get; }

    public static Result<ChoiceOption> Create(string? name, StructuredText? description) =>
        string.IsNullOrWhiteSpace(name)
            ? Error.Validation("options", "Option names cannot be empty.")
            : new ChoiceOption(name.Trim(), description);
}

/// <summary>
/// Place the state on an ordered rubric. Level 0 is the lowest.
/// </summary>
public sealed record ScoreQuestion : Question
{
    public const int MinLevels = 2;

    private ScoreQuestion(QuestionKey key, StructuredText instructions, IReadOnlyList<StructuredText> levels)
        : base(key, instructions) => Levels = levels;

    public IReadOnlyList<StructuredText> Levels { get; }

    public override QuestionType Type => QuestionType.Score;

    public static Result<ScoreQuestion> Create(QuestionKey key, StructuredText instructions, IReadOnlyList<StructuredText> levels) =>
        levels.Count < MinLevels
            ? Error.Validation("levels", $"Score question '{key}' needs at least {MinLevels} levels.")
            : new ScoreQuestion(key, instructions, levels);
}

/// <summary>
/// Is the statement true? Optional descriptions of the true and false sides sharpen the boundary.
/// </summary>
public sealed record NoulQuestion(QuestionKey Key, StructuredText Instructions, StructuredText? WhenTrue, StructuredText? WhenFalse)
    : Question(Key, Instructions)
{
    public override QuestionType Type => QuestionType.Noul;
}

using AISandbox.Domain.Abstractions;
using AISandbox.Domain.Authoring.Questions;

namespace AISandbox.Application.Features.Runs;

public sealed record OptionInput(string? Name, string? Description);

/// <summary>
/// A question as typed in the run dialog. Text fields that start with '{' or '[' are read as JSON
/// so authors can use structured instructions, options and levels.
/// </summary>
public sealed record QuestionInput(
    QuestionType Type,
    string? Key,
    string? Instructions,
    IReadOnlyList<OptionInput>? Options = null,
    IReadOnlyList<string?>? Levels = null,
    string? WhenTrue = null,
    string? WhenFalse = null);

internal static class QuestionInputMapper
{
    public static Result<StructuredText> ToStructured(string? text, string field)
    {
        var trimmed = text?.Trim();
        var result = trimmed is ['{', ..] or ['[', ..]
            ? StructuredText.FromJson(trimmed)
            : StructuredText.FromText(trimmed);
        return result.IsSuccess ? result : Error.Validation(field, result.Error!.Message);
    }

    private static Result<StructuredText?> ToOptionalStructured(string? text, string field) =>
        string.IsNullOrWhiteSpace(text)
            ? Result<StructuredText?>.Success(null)
            : ToStructured(text, field).Map<StructuredText?>(s => s);

    /// <summary>
    /// The inverse of <see cref="ToQuestion"/>: a question as the editor shows it.
    /// </summary>
    public static QuestionInput ToInput(Question question) => question switch
    {
        ChoiceQuestion c => new QuestionInput(
            QuestionType.Choice, c.Key.Value, c.Instructions.Display,
            Options: c.Options.Select(o => new OptionInput(o.Name, o.Description?.Display)).ToList()),
        ScoreQuestion s => new QuestionInput(
            QuestionType.Score, s.Key.Value, s.Instructions.Display,
            Levels: s.Levels.Select(l => (string?)l.Display).ToList()),
        NoulQuestion n => new QuestionInput(
            QuestionType.Noul, n.Key.Value, n.Instructions.Display,
            WhenTrue: n.WhenTrue?.Display, WhenFalse: n.WhenFalse?.Display),
        _ => throw new ArgumentOutOfRangeException(nameof(question)),
    };

    public static Result<IReadOnlyList<Question>> ToQuestions(IReadOnlyList<QuestionInput>? inputs)
    {
        var questions = new List<Question>();
        foreach (var input in inputs ?? [])
        {
            var question = ToQuestion(input);
            if (question.IsFailure)
            {
                return question.Error!;
            }

            questions.Add(question.Value);
        }

        return Result<IReadOnlyList<Question>>.Success(questions);
    }

    public static Result<Question> ToQuestion(QuestionInput input)
    {
        var key = QuestionKey.Create(input.Key?.Trim());
        if (key.IsFailure)
        {
            return key.Error!;
        }

        var instructions = ToStructured(input.Instructions, "instructions");
        if (instructions.IsFailure)
        {
            return Error.Validation("instructions", $"Question '{key.Value}': instructions are required.");
        }

        return input.Type switch
        {
            QuestionType.Choice => ToChoice(key.Value, instructions.Value, input.Options ?? []),
            QuestionType.Score => ToScore(key.Value, instructions.Value, input.Levels ?? []),
            QuestionType.Noul => ToNoul(key.Value, instructions.Value, input),
            _ => Error.Validation("type", "Unknown question type."),
        };
    }

    private static Result<Question> ToChoice(QuestionKey key, StructuredText instructions, IReadOnlyList<OptionInput> inputs)
    {
        var options = new List<ChoiceOption>();
        foreach (var input in inputs.Where(o => !string.IsNullOrWhiteSpace(o.Name)))
        {
            var description = ToOptionalStructured(input.Description, "options");
            if (description.IsFailure)
            {
                return description.Error!;
            }

            var option = ChoiceOption.Create(input.Name, description.Value);
            if (option.IsFailure)
            {
                return option.Error!;
            }

            options.Add(option.Value);
        }

        return ChoiceQuestion.Create(key, instructions, options).Map<Question>(q => q);
    }

    private static Result<Question> ToScore(QuestionKey key, StructuredText instructions, IReadOnlyList<string?> inputs)
    {
        var levels = new List<StructuredText>();
        foreach (var input in inputs.Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            var level = ToStructured(input, "levels");
            if (level.IsFailure)
            {
                return level.Error!;
            }

            levels.Add(level.Value);
        }

        return ScoreQuestion.Create(key, instructions, levels).Map<Question>(q => q);
    }

    private static Result<Question> ToNoul(QuestionKey key, StructuredText instructions, QuestionInput input)
    {
        var whenTrue = ToOptionalStructured(input.WhenTrue, "criteria");
        if (whenTrue.IsFailure)
        {
            return whenTrue.Error!;
        }

        var whenFalse = ToOptionalStructured(input.WhenFalse, "criteria");
        if (whenFalse.IsFailure)
        {
            return whenFalse.Error!;
        }

        return new NoulQuestion(key, instructions, whenTrue.Value, whenFalse.Value);
    }
}

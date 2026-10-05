using AISandbox.Domain.Abstractions;
using AISandbox.Domain.Authoring.Questions;

namespace AISandbox.Domain.Experimentation;

/// <summary>
/// Immutable snapshot of what a run sends: the state and the questions, exactly as they were.
/// </summary>
public sealed record RunInput
{
    public const int MaxQuestions = 64;

    private RunInput(StructuredText state, IReadOnlyList<Question> questions)
    {
        State = state;
        Questions = questions;
    }

    public StructuredText State { get; }

    public IReadOnlyList<Question> Questions { get; }

    public static Result<RunInput> Create(StructuredText state, IReadOnlyList<Question> questions)
    {
        if (questions.Count is 0 or > MaxQuestions)
        {
            return Error.Validation("questions", $"A run needs between 1 and {MaxQuestions} questions.");
        }

        var duplicate = questions.GroupBy(q => q.Key.Value).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            return Error.Validation("questions", $"Question key '{duplicate.Key}' is used more than once.");
        }

        return new RunInput(state, questions);
    }
}

using AISandbox.Domain.Abstractions;
using AISandbox.Domain.Authoring.QuestionSets;
using AISandbox.Domain.Authoring.Questions;

namespace AISandbox.Domain.Experimentation;

/// <summary>
/// Immutable snapshot of what a run sends: the state and the questions, exactly as they were.
/// </summary>
public sealed record RunInput
{
    public const int MaxQuestions = QuestionRules.MaxQuestions;

    private RunInput(StructuredText state, IReadOnlyList<Question> questions, QuestionSetRef? questionSet)
    {
        State = state;
        Questions = questions;
        QuestionSet = questionSet;
    }

    public StructuredText State { get; }

    public IReadOnlyList<Question> Questions { get; }

    /// <summary>
    /// The saved question set (and version) the questions came from, when they came from one.
    /// </summary>
    public QuestionSetRef? QuestionSet { get; }

    public static Result<RunInput> Create(StructuredText state, IReadOnlyList<Question> questions, QuestionSetRef? questionSet = null) =>
        QuestionRules.Check(questions) is { } error ? error : new RunInput(state, questions, questionSet);
}

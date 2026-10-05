using AISandbox.Domain.Abstractions;
using AISandbox.Domain.Authoring.Questions;

namespace AISandbox.Domain.Authoring.QuestionSets;

public readonly record struct QuestionSetId(Guid Value)
{
    public static QuestionSetId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// Points at one version of a question set. Runs keep it so they can say which version they used.
/// </summary>
public sealed record QuestionSetRef(QuestionSetId Id, int Version);

/// <summary>
/// The rules every ordered list of questions must obey, in a set or in a run.
/// </summary>
public static class QuestionRules
{
    public const int MaxQuestions = 64;

    public static Error? Check(IReadOnlyList<Question> questions)
    {
        if (questions.Count is 0 or > MaxQuestions)
        {
            return Error.Validation("questions", $"Between 1 and {MaxQuestions} questions are required.");
        }

        var duplicate = questions.GroupBy(q => q.Key.Value).FirstOrDefault(g => g.Count() > 1);
        return duplicate is null
            ? null
            : Error.Validation("questions", $"Question key '{duplicate.Key}' is used more than once.");
    }
}

/// <summary>
/// A named, ordered, versioned group of questions. Every save that replaces the content creates
/// the next version; runs pin the version they used.
/// </summary>
public sealed class QuestionSet : AggregateRoot<QuestionSetId>
{
    public const int MaxNameLength = 100;

    private QuestionSet(QuestionSetId id, string name, IReadOnlyList<Question> questions, DateTimeOffset now)
        : base(id)
    {
        Name = name;
        Questions = questions;
        Version = 1;
        CreatedAt = now;
        UpdatedAt = now;
    }

    // Used by persistence to materialise the aggregate.
    private QuestionSet()
        : base(default)
    {
        Name = null!;
        Questions = null!;
    }

    public string Name { get; private set; }

    public int Version { get; private set; }

    public IReadOnlyList<Question> Questions { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public QuestionSetRef Ref => new(Id, Version);

    public static Result<QuestionSet> Create(string? name, IReadOnlyList<Question> questions, DateTimeOffset now)
    {
        var check = Validate(name, questions);
        return check.IsFailure
            ? check.Error!
            : new QuestionSet(QuestionSetId.New(), check.Value, questions.ToList(), now);
    }

    /// <summary>
    /// Replaces the name and questions and bumps the version.
    /// </summary>
    public Result Update(string? name, IReadOnlyList<Question> questions, DateTimeOffset now)
    {
        var check = Validate(name, questions);
        if (check.IsFailure)
        {
            return Result.Failure(check.Error!);
        }

        Name = check.Value;
        Questions = questions.ToList();
        Version++;
        UpdatedAt = now;
        return Result.Success();
    }

    private static Result<string> Validate(string? name, IReadOnlyList<Question> questions)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return Error.Validation("name", "Name is required.");
        }

        if (trimmed.Length > MaxNameLength)
        {
            return Error.Validation("name", $"Name must be at most {MaxNameLength} characters.");
        }

        return QuestionRules.Check(questions) is { } error ? error : trimmed;
    }
}

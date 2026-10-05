using AISandbox.Application.Abstractions;
using AISandbox.Application.Features.Runs;
using AISandbox.Domain.Abstractions;
using AISandbox.Domain.Authoring.QuestionSets;

namespace AISandbox.Application.Features.QuestionSets;

public sealed record CreateQuestionSet(string? Name, IReadOnlyList<QuestionInput> Questions);

/// <summary>
/// Replaces the name and questions of a set and bumps its version.
/// </summary>
public sealed record UpdateQuestionSet(QuestionSetId Id, string? Name, IReadOnlyList<QuestionInput> Questions);

public sealed record ListQuestionSets;

public sealed record GetQuestionSet(QuestionSetId Id);

public sealed record QuestionSetSummary(QuestionSetId Id, string Name, int Version, int QuestionCount, DateTimeOffset UpdatedAt);

public sealed record QuestionSetView(
    QuestionSetId Id,
    string Name,
    int Version,
    IReadOnlyList<QuestionInput> Questions,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Read-side projection of question sets. Implemented in Infrastructure.
/// </summary>
public interface IQuestionSetQueries
{
    Task<IReadOnlyList<QuestionSetSummary>> ListAsync(CancellationToken cancellationToken);
}

public sealed class CreateQuestionSetHandler(IQuestionSetRepository sets, IUnitOfWork unitOfWork, TimeProvider time)
    : ICommandHandler<CreateQuestionSet, QuestionSetId>
{
    public async Task<Result<QuestionSetId>> HandleAsync(CreateQuestionSet command, CancellationToken cancellationToken)
    {
        var questions = QuestionInputMapper.ToQuestions(command.Questions);
        if (questions.IsFailure)
        {
            return questions.Error!;
        }

        var set = QuestionSet.Create(command.Name, questions.Value, time.GetUtcNow());
        if (set.IsFailure)
        {
            return set.Error!;
        }

        sets.Add(set.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return set.Value.Id;
    }
}

public sealed class UpdateQuestionSetHandler(IQuestionSetRepository sets, IUnitOfWork unitOfWork, TimeProvider time)
    : ICommandHandler<UpdateQuestionSet, QuestionSetRef>
{
    public async Task<Result<QuestionSetRef>> HandleAsync(UpdateQuestionSet command, CancellationToken cancellationToken)
    {
        var set = await sets.GetAsync(command.Id, cancellationToken);
        if (set is null)
        {
            return Error.NotFound("Question set");
        }

        var questions = QuestionInputMapper.ToQuestions(command.Questions);
        if (questions.IsFailure)
        {
            return questions.Error!;
        }

        var updated = set.Update(command.Name, questions.Value, time.GetUtcNow());
        if (updated.IsFailure)
        {
            return updated.Error!;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return set.Ref;
    }
}

public sealed class ListQuestionSetsHandler(IQuestionSetQueries queries)
    : IQueryHandler<ListQuestionSets, IReadOnlyList<QuestionSetSummary>>
{
    public Task<IReadOnlyList<QuestionSetSummary>> HandleAsync(ListQuestionSets query, CancellationToken cancellationToken) =>
        queries.ListAsync(cancellationToken);
}

public sealed class GetQuestionSetHandler(IQuestionSetRepository sets) : IQueryHandler<GetQuestionSet, QuestionSetView?>
{
    public async Task<QuestionSetView?> HandleAsync(GetQuestionSet query, CancellationToken cancellationToken)
    {
        var set = await sets.GetAsync(query.Id, cancellationToken);
        return set is null
            ? null
            : new QuestionSetView(
                set.Id,
                set.Name,
                set.Version,
                set.Questions.Select(QuestionInputMapper.ToInput).ToList(),
                set.UpdatedAt);
    }
}

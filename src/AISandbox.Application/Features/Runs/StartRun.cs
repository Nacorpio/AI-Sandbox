using AISandbox.Application.Abstractions;
using AISandbox.Domain.Abstractions;
using AISandbox.Domain.Authoring.QuestionSets;
using AISandbox.Domain.Catalog.Models;
using AISandbox.Domain.Experimentation;

namespace AISandbox.Application.Features.Runs;

/// <param name="State">The content to evaluate. Text, or JSON when it starts with '{' or '['.</param>
/// <param name="Questions">Inline questions. Ignored when a saved set is given.</param>
/// <param name="QuestionSetId">A saved question set; its current version is used and pinned on the run.</param>
public sealed record StartRun(
    string? State,
    IReadOnlyList<QuestionInput> Questions,
    IReadOnlyList<ModelDefinitionId> ModelIds,
    QuestionSetId? QuestionSetId = null);

/// <summary>
/// Starts a run: records it with every execution pending, hands it to the background and returns
/// its id at once. Models are then called in parallel (see <see cref="RunExecutor"/>); provider
/// failures are recorded on the run, so the command succeeds whenever the input is valid.
/// </summary>
public sealed class StartRunHandler(
    IModelDefinitionRepository models,
    IRunRepository runs,
    IQuestionSetRepository questionSets,
    IRunScheduler scheduler,
    IUnitOfWork unitOfWork,
    TimeProvider time)
    : ICommandHandler<StartRun, RunId>
{
    public async Task<Result<RunId>> HandleAsync(StartRun command, CancellationToken cancellationToken)
    {
        var input = await BuildInputAsync(command, cancellationToken);
        if (input.IsFailure)
        {
            return input.Error!;
        }

        var selected = await models.GetManyAsync(command.ModelIds.Distinct().ToList(), cancellationToken);
        if (selected.Count != command.ModelIds.Distinct().Count())
        {
            return Error.NotFound("One or more selected models");
        }

        var unsupported = selected.FirstOrDefault(m => m.Kind != ModelKind.Decision);
        if (unsupported is not null)
        {
            return Error.Validation("models", $"{unsupported.DisplayName} is a chat model; chat runs are not supported yet.");
        }

        var run = Run.Start(input.Value, selected.Select(ModelSnapshot.Of).ToList(), time.GetUtcNow());
        if (run.IsFailure)
        {
            return run.Error!;
        }

        runs.Add(run.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        scheduler.Enqueue(run.Value.Id);
        return run.Value.Id;
    }

    private async Task<Result<RunInput>> BuildInputAsync(StartRun command, CancellationToken cancellationToken)
    {
        var state = QuestionInputMapper.ToStructured(command.State, "state");
        if (state.IsFailure)
        {
            return Error.Validation("state", string.IsNullOrWhiteSpace(command.State) ? "State is required." : state.Error!.Message);
        }

        if (command.QuestionSetId is { } setId)
        {
            var set = await questionSets.GetAsync(setId, cancellationToken);
            return set is null
                ? Error.NotFound("Question set")
                : RunInput.Create(state.Value, set.Questions, set.Ref);
        }

        var questions = QuestionInputMapper.ToQuestions(command.Questions);
        return questions.IsFailure ? questions.Error! : RunInput.Create(state.Value, questions.Value);
    }
}

using AISandbox.Application.Abstractions;
using AISandbox.Application.Features.Prompts;
using AISandbox.Domain.Abstractions;
using AISandbox.Domain.Authoring.Prompts;
using AISandbox.Domain.Authoring.QuestionSets;
using AISandbox.Domain.Authoring.Questions;
using AISandbox.Domain.Catalog.Models;
using AISandbox.Domain.Experimentation;

namespace AISandbox.Application.Features.Runs;

/// <summary>
/// The prompt for chat models: a saved template filled with <paramref name="Values"/>, or one-off
/// <paramref name="System"/> and <paramref name="User"/> text.
/// </summary>
public sealed record ChatPromptInput(
    PromptTemplateId? TemplateId = null,
    IReadOnlyDictionary<string, string>? Values = null,
    string? System = null,
    string? User = null,
    string? OutputSchema = null);

/// <param name="State">The content to evaluate. Text, or JSON when it starts with '{' or '['.</param>
/// <param name="Questions">Inline questions. Ignored when a saved set is given.</param>
/// <param name="QuestionSetId">A saved question set; its current version is used and pinned on the run.</param>
/// <param name="Prompt">The prompt chat models answer. Required when a chat model is selected.</param>
/// <param name="ChatOptions">Options per chat model; models without an entry use provider defaults.</param>
public sealed record StartRun(
    string? State,
    IReadOnlyList<QuestionInput> Questions,
    IReadOnlyList<ModelDefinitionId> ModelIds,
    QuestionSetId? QuestionSetId = null,
    ChatPromptInput? Prompt = null,
    IReadOnlyDictionary<ModelDefinitionId, ChatOptions>? ChatOptions = null);

/// <summary>
/// Starts a run: records it with every execution pending, hands it to the background and returns
/// its id at once. Models are then called in parallel (see <see cref="RunExecutor"/>); provider
/// failures are recorded on the run, so the command succeeds whenever the input is valid.
/// </summary>
public sealed class StartRunHandler(
    IModelDefinitionRepository models,
    IRunRepository runs,
    IQuestionSetRepository questionSets,
    IPromptTemplateRepository promptTemplates,
    IRunScheduler scheduler,
    IUnitOfWork unitOfWork,
    TimeProvider time)
    : ICommandHandler<StartRun, RunId>
{
    public async Task<Result<RunId>> HandleAsync(StartRun command, CancellationToken cancellationToken)
    {
        var selected = await models.GetManyAsync(command.ModelIds.Distinct().ToList(), cancellationToken);
        if (selected.Count != command.ModelIds.Distinct().Count())
        {
            return Error.NotFound("One or more selected models");
        }

        // Each part of the input is kept only when a selected model needs it. With no models the
        // decision part is still checked, so the usual input errors come before "select a model".
        var chatModels = selected.Where(m => m.Kind == ModelKind.Chat).ToList();
        var needsDecision = selected.Count == 0 || selected.Any(m => m.Kind == ModelKind.Decision);

        QuestionSetRef? setRef = null;
        StructuredText? state = null;
        IReadOnlyList<Question> questions = [];
        if (needsDecision)
        {
            var decision = await BuildDecisionAsync(command, cancellationToken);
            if (decision.IsFailure)
            {
                return decision.Error!;
            }

            (state, questions, setRef) = decision.Value;
        }

        ChatPrompt? prompt = null;
        if (chatModels.Count > 0)
        {
            var built = await BuildPromptAsync(command.Prompt, chatModels, cancellationToken);
            if (built.IsFailure)
            {
                return built.Error!;
            }

            prompt = built.Value;
        }

        var options = (command.ChatOptions ?? new Dictionary<ModelDefinitionId, ChatOptions>())
            .Where(o => chatModels.Any(m => m.Id == o.Key))
            .ToDictionary(o => o.Key, o => o.Value);
        var input = RunInput.Create(state, questions, setRef, prompt, options);
        if (input.IsFailure)
        {
            return input.Error!;
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

    private async Task<Result<(StructuredText State, IReadOnlyList<Question> Questions, QuestionSetRef? Set)>> BuildDecisionAsync(
        StartRun command,
        CancellationToken cancellationToken)
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
                : (state.Value, set.Questions, set.Ref);
        }

        var questions = QuestionInputMapper.ToQuestions(command.Questions);
        return questions.IsFailure ? questions.Error! : (state.Value, questions.Value, null);
    }

    private async Task<Result<ChatPrompt>> BuildPromptAsync(
        ChatPromptInput? input,
        IReadOnlyList<ModelDefinition> chatModels,
        CancellationToken cancellationToken)
    {
        if (input is null || (input.TemplateId is null && string.IsNullOrWhiteSpace(input.User)))
        {
            return Error.Validation("prompt", $"{chatModels[0].DisplayName} is a chat model and needs a prompt.");
        }

        if (input.TemplateId is { } id)
        {
            var template = await promptTemplates.GetAsync(id, cancellationToken);
            return template is null ? Error.NotFound("Prompt template") : template.Render(input.Values);
        }

        var system = string.IsNullOrWhiteSpace(input.System) ? null : input.System.Trim();
        return new ChatPrompt(system, input.User!.Trim(), SchemaOrNull(input.OutputSchema));
    }

    private static JsonSchemaDocument? SchemaOrNull(string? schema) =>
        string.IsNullOrWhiteSpace(schema) ? null : new JsonSchemaDocument(schema.Trim());
}

using System.Diagnostics;
using AISandbox.Application.Abstractions;
using AISandbox.Application.Telemetry;
using AISandbox.Domain.Abstractions;
using AISandbox.Domain.Authoring.QuestionSets;
using AISandbox.Domain.Catalog.Models;
using AISandbox.Domain.Catalog.Providers;
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
/// Starts a run and executes every selected model. Provider failures are recorded on the run, so
/// the command succeeds whenever the input is valid.
/// </summary>
public sealed class StartRunHandler(
    IModelDefinitionRepository models,
    IProviderRepository providers,
    IRunRepository runs,
    IQuestionSetRepository questionSets,
    ISecretStore secrets,
    IModelInvokerResolver invokers,
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

        using var activity = ApplicationTelemetry.Runs.StartActivity("run");
        activity?.SetTag("run.id", run.Value.Id.ToString());
        foreach (var execution in run.Value.Executions)
        {
            var model = selected.First(m => m.Id == execution.Model.ModelId);
            await ExecuteAsync(run.Value, execution.Id, model, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

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

    private async Task ExecuteAsync(Run run, ExecutionId executionId, ModelDefinition model, CancellationToken cancellationToken)
    {
        using var activity = ApplicationTelemetry.Runs.StartActivity("execution");
        activity?.SetTag("model.remote_id", model.RemoteId.Value);
        activity?.SetTag("model.protocol", model.Protocol.Value);

        run.MarkRunning(executionId, time.GetUtcNow());

        var provider = await providers.GetAsync(model.ProviderId, cancellationToken);
        var invoker = invokers.For(model.Protocol);
        if (provider is null || invoker is null)
        {
            var reason = provider is null ? "Its provider no longer exists." : $"No invoker speaks the '{model.Protocol}' protocol yet.";
            run.RecordFailure(executionId, new ExecutionError("configuration", reason, null), null, null, null, time.GetUtcNow());
            return;
        }

        var apiKey = await secrets.GetAsync(provider.Secret, cancellationToken);
        if (apiKey is null && provider.Auth.Kind != AuthSchemeKind.None)
        {
            run.RecordFailure(
                executionId,
                new ExecutionError("credentials.missing", $"Provider '{provider.Name}' has no API key.", null),
                null, null, null, time.GetUtcNow());
            return;
        }

        var outcome = await invoker.InvokeAsync(
            new InvocationRequest(provider.BaseUrl, provider.Auth, apiKey, model.RemoteId, run.Input),
            cancellationToken);

        switch (outcome)
        {
            case InvocationSucceeded succeeded:
                run.RecordSuccess(executionId, succeeded.Success, time.GetUtcNow());
                activity?.SetTag("tokens.input", succeeded.Success.Usage.InputTokens);
                break;
            case InvocationFailed failed:
                run.RecordFailure(executionId, failed.Error, failed.Latency, failed.RawRequest, failed.RawResponse, time.GetUtcNow());
                activity?.SetStatus(ActivityStatusCode.Error, failed.Error.Code);
                break;
        }
    }
}

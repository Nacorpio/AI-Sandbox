using System.Diagnostics;
using AISandbox.Application.Abstractions;
using AISandbox.Application.Telemetry;
using AISandbox.Domain.Catalog.Models;
using AISandbox.Domain.Catalog.Providers;
using AISandbox.Domain.Experimentation;

namespace AISandbox.Application.Features.Runs;

/// <summary>
/// Calls all models of a run at the same time. The provider calls run concurrently; every change
/// to the run is serialised, because the aggregate and its DbContext are not thread-safe.
/// One execution failing never stops the others.
/// </summary>
public sealed class RunExecutor(
    IRunRepository runs,
    IModelDefinitionRepository models,
    IProviderRepository providers,
    ISecretStore secrets,
    IModelInvokerResolver invokers,
    IProviderLimiter limiter,
    IRunNotifier notifier,
    IUnitOfWork unitOfWork,
    TimeProvider time)
    : IRunExecutor
{
    private sealed record Plan(
        ExecutionId ExecutionId,
        ModelDefinition? Model,
        Provider? Provider,
        IModelInvoker? Invoker,
        string? ApiKey,
        ExecutionError? ConfigurationError);

    public async Task ExecuteAsync(RunId runId, CancellationToken cancellationToken)
    {
        var run = await runs.GetAsync(runId, CancellationToken.None);
        if (run is null || run.Status == RunStatus.Completed)
        {
            return;
        }

        using var activity = ApplicationTelemetry.Runs.StartActivity("run");
        activity?.SetTag("run.id", runId.ToString());

        var plans = await PlanAsync(run);
        var gate = new SemaphoreSlim(1, 1);
        await Task.WhenAll(plans.Select(plan => ExecuteOneAsync(run, plan, gate, cancellationToken)));
    }

    // Resolves everything an execution needs one by one, before any concurrent work starts.
    private async Task<List<Plan>> PlanAsync(Run run)
    {
        var unfinished = run.Executions.Where(e => !e.IsFinished).ToList();
        var definitions = await models.GetManyAsync(unfinished.Select(e => e.Model.ModelId).ToList(), CancellationToken.None);
        var plans = new List<Plan>();
        foreach (var execution in unfinished)
        {
            var model = definitions.FirstOrDefault(m => m.Id == execution.Model.ModelId);
            var provider = await providers.GetAsync(execution.Model.ProviderId, CancellationToken.None);
            var invoker = model is null ? null : invokers.For(model.Protocol);
            if (model is null || provider is null || invoker is null)
            {
                var reason = provider is null || model is null
                    ? "Its provider or model no longer exists."
                    : $"No invoker speaks the '{model.Protocol}' protocol yet.";
                plans.Add(new Plan(execution.Id, model, provider, invoker, null, new ExecutionError("configuration", reason, null)));
                continue;
            }

            var apiKey = await secrets.GetAsync(provider.Secret, CancellationToken.None);
            ExecutionError? missingKey = apiKey is null && provider.Auth.Kind != AuthSchemeKind.None
                ? new ExecutionError("credentials.missing", $"Provider '{provider.Name}' has no API key.", null)
                : null;
            plans.Add(new Plan(execution.Id, model, provider, invoker, apiKey, missingKey));
        }

        return plans;
    }

    private async Task ExecuteOneAsync(Run run, Plan plan, SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        try
        {
            if (plan.ConfigurationError is { } configurationError)
            {
                await ChangeAsync(run, gate, r => r.RecordFailure(plan.ExecutionId, configurationError, null, null, null, time.GetUtcNow()));
                return;
            }

            var model = plan.Model!;
            var provider = plan.Provider!;
            using var lease = await limiter.AcquireAsync(provider.Id, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await ChangeAsync(run, gate, r => r.MarkRunning(plan.ExecutionId, time.GetUtcNow()));

            using var activity = ApplicationTelemetry.Runs.StartActivity("execution");
            activity?.SetTag("model.remote_id", model.RemoteId.Value);
            activity?.SetTag("model.protocol", model.Protocol.Value);

            var outcome = await plan.Invoker!.InvokeAsync(
                new InvocationRequest(provider.BaseUrl, provider.Auth, plan.ApiKey, model.RemoteId, run.Input, provider.PathVariables,
                    run.Input.ChatOptionsFor(model.Id), model.Capabilities),
                cancellationToken);

            switch (outcome)
            {
                case InvocationSucceeded succeeded:
                    await ChangeAsync(run, gate, r => r.RecordSuccess(plan.ExecutionId, succeeded.Success, time.GetUtcNow()));
                    activity?.SetTag("tokens.input", succeeded.Success.Usage.InputTokens);
                    break;
                case InvocationFailed failed:
                    await ChangeAsync(run, gate, r => r.RecordFailure(plan.ExecutionId, failed.Error, failed.Latency, failed.RawRequest, failed.RawResponse, time.GetUtcNow(), failed.Attempts));
                    activity?.SetStatus(ActivityStatusCode.Error, failed.Error.Code);
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RecordUnlessFinishedAsync(run, plan, gate, ExecutionError.Cancelled);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await RecordUnlessFinishedAsync(run, plan, gate, new ExecutionError("internal", exception.Message, null));
        }
    }

    private Task RecordUnlessFinishedAsync(Run run, Plan plan, SemaphoreSlim gate, ExecutionError error) =>
        ChangeAsync(run, gate, r =>
        {
            if (!r.Executions.First(e => e.Id == plan.ExecutionId).IsFinished)
            {
                r.RecordFailure(plan.ExecutionId, error, null, null, null, time.GetUtcNow());
            }
        });

    // Applies one change to the run, saves it and tells subscribers, one caller at a time.
    private async Task ChangeAsync(Run run, SemaphoreSlim gate, Action<Run> change)
    {
        await gate.WaitAsync(CancellationToken.None);
        try
        {
            change(run);
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            foreach (var runEvent in run.DomainEvents.OfType<IRunEvent>())
            {
                notifier.Publish(runEvent);
            }

            run.ClearDomainEvents();
        }
        finally
        {
            gate.Release();
        }
    }
}

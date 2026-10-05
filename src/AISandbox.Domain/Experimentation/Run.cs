using AISandbox.Domain.Abstractions;
using AISandbox.Domain.Catalog.Models;
using AISandbox.Domain.Catalog.Providers;

namespace AISandbox.Domain.Experimentation;

public readonly record struct RunId(Guid Value)
{
    public static RunId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

public readonly record struct ExecutionId(Guid Value)
{
    public static ExecutionId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

public enum RunStatus
{
    Running,
    Completed,
}

public enum ExecutionStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
}

/// <summary>
/// The model definition as it was when the run started. Later edits to the definition, including
/// price changes, never alter a recorded execution.
/// </summary>
public sealed record ModelSnapshot(
    ModelDefinitionId ModelId,
    string DisplayName,
    ProviderId ProviderId,
    string Protocol,
    string RemoteId,
    PricingSchedule Pricing)
{
    public static ModelSnapshot Of(ModelDefinition model) =>
        new(model.Id, model.DisplayName, model.ProviderId, model.Protocol.Value, model.RemoteId.Value, model.Pricing);
}

/// <summary>
/// What an invoker observed for one successful call.
/// </summary>
public sealed record InvocationSuccess(
    NormalizedOutput Output,
    TokenUsage Usage,
    Money? ProviderReportedCost,
    Latency Latency,
    string? ResolvedModel,
    string RawRequest,
    string RawResponse);

/// <summary>
/// One input sent to one or more models at the same time.
/// </summary>
public sealed class Run : AggregateRoot<RunId>
{
    private readonly List<ModelExecution> _executions = [];

    private Run()
        : base(default) => Input = null!;

    private Run(RunId id, RunInput input, DateTimeOffset now)
        : base(id)
    {
        Input = input;
        CreatedAt = now;
        Status = RunStatus.Running;
    }

    public RunInput Input { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public RunStatus Status { get; private set; }

    public IReadOnlyList<ModelExecution> Executions => _executions;

    public static Result<Run> Start(RunInput input, IReadOnlyList<ModelSnapshot> models, DateTimeOffset now)
    {
        if (models.Count == 0)
        {
            return Error.Validation("models", "Select at least one model.");
        }

        if (models.Select(m => m.ModelId).Distinct().Count() != models.Count)
        {
            return Error.Validation("models", "Each model can be selected only once per run.");
        }

        var run = new Run(RunId.New(), input, now);
        run._executions.AddRange(models.Select(m => new ModelExecution(ExecutionId.New(), m)));
        run.Raise(new RunStarted(run.Id, models.Count, now));
        return run;
    }

    public void MarkRunning(ExecutionId executionId, DateTimeOffset now) => Find(executionId).MarkRunning(now);

    public void RecordSuccess(ExecutionId executionId, InvocationSuccess success, DateTimeOffset now)
    {
        var execution = Find(executionId);
        execution.Succeed(success, now);
        Raise(new ExecutionCompleted(Id, executionId, now));
        CompleteIfDone(now);
    }

    public void RecordFailure(ExecutionId executionId, ExecutionError error, Latency? latency, string? rawRequest, string? rawResponse, DateTimeOffset now)
    {
        var execution = Find(executionId);
        execution.Fail(error, latency, rawRequest, rawResponse, now);
        Raise(new ExecutionFailed(Id, executionId, error.Code, now));
        CompleteIfDone(now);
    }

    private ModelExecution Find(ExecutionId executionId) =>
        _executions.FirstOrDefault(e => e.Id == executionId)
        ?? throw new InvalidOperationException($"Execution {executionId} is not part of run {Id}.");

    private void CompleteIfDone(DateTimeOffset now)
    {
        if (Status == RunStatus.Completed || _executions.Any(e => !e.IsFinished))
        {
            return;
        }

        Status = RunStatus.Completed;
        CompletedAt = now;
        Raise(new RunCompleted(Id, now));
    }
}

/// <summary>
/// One model's result inside a run.
/// </summary>
public sealed class ModelExecution : Entity<ExecutionId>
{
    private ModelExecution()
        : base(default) => Model = null!;

    internal ModelExecution(ExecutionId id, ModelSnapshot model)
        : base(id)
    {
        Model = model;
        Status = ExecutionStatus.Pending;
    }

    public ModelSnapshot Model { get; private set; }

    public ExecutionStatus Status { get; private set; }

    public NormalizedOutput? Output { get; private set; }

    public TokenUsage? Usage { get; private set; }

    public Money? Cost { get; private set; }

    public CostSource? CostSource { get; private set; }

    public Latency? Latency { get; private set; }

    public string? ResolvedModel { get; private set; }

    public string? RawRequest { get; private set; }

    public string? RawResponse { get; private set; }

    public ExecutionError? Error { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public bool IsFinished => Status is ExecutionStatus.Succeeded or ExecutionStatus.Failed;

    internal void MarkRunning(DateTimeOffset now)
    {
        EnsureNotFinished();
        Status = ExecutionStatus.Running;
        StartedAt ??= now;
        Attempts++;
    }

    internal void Succeed(InvocationSuccess success, DateTimeOffset now)
    {
        EnsureNotFinished();
        Status = ExecutionStatus.Succeeded;
        Output = success.Output;
        Usage = success.Usage;
        (Cost, CostSource) = success.ProviderReportedCost is { } reported
            ? (reported, Experimentation.CostSource.ProviderReported)
            : (Model.Pricing.CostOf(success.Usage.InputTokens, success.Usage.OutputTokens), Experimentation.CostSource.Calculated);
        Latency = success.Latency;
        ResolvedModel = success.ResolvedModel;
        RawRequest = success.RawRequest;
        RawResponse = success.RawResponse;
        CompletedAt = now;
    }

    internal void Fail(ExecutionError error, Latency? latency, string? rawRequest, string? rawResponse, DateTimeOffset now)
    {
        EnsureNotFinished();
        Status = ExecutionStatus.Failed;
        Error = error;
        Latency = latency;
        RawRequest = rawRequest;
        RawResponse = rawResponse;
        CompletedAt = now;
    }

    private void EnsureNotFinished()
    {
        if (IsFinished)
        {
            throw new InvalidOperationException($"Execution {Id} has already finished.");
        }
    }
}

public sealed record RunStarted(RunId RunId, int ModelCount, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record ExecutionCompleted(RunId RunId, ExecutionId ExecutionId, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record ExecutionFailed(RunId RunId, ExecutionId ExecutionId, string ErrorCode, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record RunCompleted(RunId RunId, DateTimeOffset OccurredAt) : IDomainEvent;

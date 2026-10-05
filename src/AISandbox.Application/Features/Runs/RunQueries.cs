using AISandbox.Application.Abstractions;
using AISandbox.Domain.Abstractions;
using AISandbox.Domain.Experimentation;

namespace AISandbox.Application.Features.Runs;

public sealed record GetRun(RunId RunId);

public sealed record QuestionView(string Key, string Type, string Instructions);

public sealed record ExecutionView(
    ExecutionId Id,
    string ModelName,
    string Protocol,
    string RemoteId,
    ExecutionStatus Status,
    NormalizedOutput? Output,
    TokenUsage? Usage,
    Money? Cost,
    CostSource? CostSource,
    Latency? Latency,
    string? ResolvedModel,
    ExecutionError? Error,
    string? RawRequest,
    string? RawResponse);

public sealed record RunView(
    RunId Id,
    DateTimeOffset CreatedAt,
    RunStatus Status,
    string State,
    IReadOnlyList<QuestionView> Questions,
    IReadOnlyList<ExecutionView> Executions);

public sealed class GetRunHandler(IRunRepository runs) : IQueryHandler<GetRun, RunView?>
{
    public async Task<RunView?> HandleAsync(GetRun query, CancellationToken cancellationToken)
    {
        var run = await runs.GetAsync(query.RunId, cancellationToken);
        return run is null ? null : ToView(run);
    }

    internal static RunView ToView(Run run) => new(
        run.Id,
        run.CreatedAt,
        run.Status,
        run.Input.State.Display,
        run.Input.Questions.Select(q => new QuestionView(q.Key.Value, q.Type.ToString(), q.Instructions.Display)).ToList(),
        run.Executions.Select(e => new ExecutionView(
            e.Id,
            e.Model.DisplayName,
            e.Model.Protocol,
            e.Model.RemoteId,
            e.Status,
            e.Output,
            e.Usage,
            e.Cost,
            e.CostSource,
            e.Latency,
            e.ResolvedModel,
            e.Error,
            e.RawRequest,
            e.RawResponse)).ToList());
}

public sealed record ListRuns(int Limit = 50);

public sealed record RunSummary(RunId Id, DateTimeOffset CreatedAt, RunStatus Status, string StatePreview, IReadOnlyList<string> Models, Money? TotalCost);

public interface IRunQueries
{
    Task<IReadOnlyList<RunSummary>> ListAsync(int limit, CancellationToken cancellationToken);
}

public sealed class ListRunsHandler(IRunQueries queries) : IQueryHandler<ListRuns, IReadOnlyList<RunSummary>>
{
    public Task<IReadOnlyList<RunSummary>> HandleAsync(ListRuns query, CancellationToken cancellationToken) =>
        queries.ListAsync(Math.Clamp(query.Limit, 1, 500), cancellationToken);
}

using AISandbox.Domain.Abstractions;

namespace AISandbox.Application.Abstractions;

/// <summary>
/// A use case that changes state. Expected failures come back as a failed <see cref="Result{T}"/>.
/// </summary>
public interface ICommandHandler<in TCommand, TResult>
{
    Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken cancellationToken);
}

/// <summary>
/// A use case that only reads. Queries project straight to view models and skip the aggregates.
/// </summary>
public interface IQueryHandler<in TQuery, TResult>
{
    Task<TResult> HandleAsync(TQuery query, CancellationToken cancellationToken);
}

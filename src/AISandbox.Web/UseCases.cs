using AISandbox.Application.Abstractions;
using AISandbox.Domain.Abstractions;

namespace AISandbox.Web;

/// <summary>
/// Runs each use case in its own DI scope. A Blazor Server circuit lives for the whole session,
/// so injecting handlers directly would keep one DbContext alive across every click.
/// </summary>
public sealed class UseCases(IServiceScopeFactory scopes)
{
    public async Task<Result<TResult>> SendAsync<TCommand, TResult>(TCommand command, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<TCommand, TResult>>();
        return await handler.HandleAsync(command, cancellationToken);
    }

    public async Task<TResult> QueryAsync<TQuery, TResult>(TQuery query, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<IQueryHandler<TQuery, TResult>>();
        return await handler.HandleAsync(query, cancellationToken);
    }
}

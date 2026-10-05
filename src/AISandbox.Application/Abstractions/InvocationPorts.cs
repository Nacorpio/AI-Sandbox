using AISandbox.Domain.Catalog.Models;
using AISandbox.Domain.Catalog.Providers;
using AISandbox.Domain.Experimentation;

namespace AISandbox.Application.Abstractions;

/// <summary>
/// Everything an invoker needs to call one model once. The key is resolved by the caller so
/// invokers never touch the secret store.
/// </summary>
public sealed record InvocationRequest(
    EndpointUri BaseUrl,
    AuthScheme Auth,
    string? ApiKey,
    RemoteModelId RemoteId,
    RunInput Input);

public abstract record InvocationOutcome;

public sealed record InvocationSucceeded(InvocationSuccess Success) : InvocationOutcome;

public sealed record InvocationFailed(ExecutionError Error, Latency? Latency, string? RawRequest, string? RawResponse) : InvocationOutcome;

/// <summary>
/// Speaks one protocol. Invokers report provider errors as <see cref="InvocationFailed"/> and
/// throw only for cancellation and bugs.
/// </summary>
public interface IModelInvoker
{
    ProtocolId Protocol { get; }

    Task<InvocationOutcome> InvokeAsync(InvocationRequest request, CancellationToken cancellationToken);
}

public interface IModelInvokerResolver
{
    IModelInvoker? For(ProtocolId protocol);
}

public interface IRunRepository
{
    void Add(Run run);

    Task<Run?> GetAsync(RunId id, CancellationToken cancellationToken);
}

using AISandbox.Domain.Catalog.Providers;
using AISandbox.Domain.Experimentation;

namespace AISandbox.Application.Abstractions;

/// <summary>
/// Hands a started run to the background so the caller gets its id straight away.
/// </summary>
public interface IRunScheduler
{
    /// <summary>
    /// Queues the run for execution.
    /// </summary>
    void Enqueue(RunId runId);

    /// <summary>
    /// Stops the run's in-flight and queued executions. Returns false when this process is not
    /// executing the run.
    /// </summary>
    bool Cancel(RunId runId);

    /// <summary>
    /// Completes once the run has finished executing. Returns immediately for runs that are not
    /// being executed.
    /// </summary>
    Task WaitForCompletionAsync(RunId runId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Executes every unfinished model execution of a run, in parallel, and records each result as
/// it arrives.
/// </summary>
public interface IRunExecutor
{
    Task ExecuteAsync(RunId runId, CancellationToken cancellationToken);
}

/// <summary>
/// Shares a budget of simultaneous calls between all models of one provider.
/// </summary>
public interface IProviderLimiter
{
    /// <summary>
    /// Waits for a free slot on the provider. Dispose the result to give the slot back.
    /// </summary>
    ValueTask<IDisposable> AcquireAsync(ProviderId providerId, CancellationToken cancellationToken);

    /// <summary>
    /// Forgets what is known about the provider's limits so the next call reads its current policy.
    /// </summary>
    void Invalidate(ProviderId providerId);
}

/// <summary>
/// Pushes what happens inside a run to whoever is watching it, such as the run page.
/// </summary>
public interface IRunNotifier
{
    void Publish(IRunEvent runEvent);

    /// <summary>
    /// Calls <paramref name="handler"/> for every later event of the run. Dispose to stop.
    /// </summary>
    IDisposable Subscribe(RunId runId, Action<IRunEvent> handler);
}

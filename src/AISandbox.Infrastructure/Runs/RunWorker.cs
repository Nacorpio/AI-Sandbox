using AISandbox.Application.Abstractions;
using AISandbox.Domain.Experimentation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AISandbox.Infrastructure.Runs;

/// <summary>
/// Takes queued runs and executes each in its own DI scope, so runs never share a DbContext and
/// several runs can be in flight at once.
/// </summary>
internal sealed class RunWorker(RunScheduler scheduler, IServiceScopeFactory scopes, ILogger<RunWorker> logger) : BackgroundService
{
    private readonly HashSet<Task> _inFlight = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var runId in scheduler.Reader.ReadAllAsync(stoppingToken))
            {
                var task = ExecuteRunAsync(runId, stoppingToken);
                lock (_inFlight)
                {
                    _inFlight.Add(task);
                }

                _ = task.ContinueWith(
                    finished =>
                    {
                        lock (_inFlight)
                        {
                            _inFlight.Remove(finished);
                        }
                    },
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        Task[] pending;
        lock (_inFlight)
        {
            pending = [.. _inFlight];
        }

        await Task.WhenAll(pending);
    }

    private async Task ExecuteRunAsync(RunId runId, CancellationToken stoppingToken)
    {
        var entry = scheduler.Find(runId);
        if (entry is null)
        {
            return;
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(entry.Cancellation.Token, stoppingToken);
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IRunExecutor>().ExecuteAsync(runId, linked.Token);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Run {RunId} failed unexpectedly.", runId);
        }
        finally
        {
            scheduler.Finish(runId);
        }
    }
}

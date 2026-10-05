using AISandbox.Application.Abstractions;
using AISandbox.Domain.Abstractions;
using AISandbox.Domain.Experimentation;

namespace AISandbox.Application.Features.Runs;

public sealed record CancelRun(RunId RunId);

/// <summary>
/// Cancels a running run. In-flight calls are aborted and their executions recorded as failed with
/// the <c>cancelled</c> error. Cancelling a finished run does nothing.
/// </summary>
public sealed class CancelRunHandler(
    IRunScheduler scheduler,
    IRunRepository runs,
    IRunNotifier notifier,
    IUnitOfWork unitOfWork,
    TimeProvider time)
    : ICommandHandler<CancelRun, RunId>
{
    public async Task<Result<RunId>> HandleAsync(CancelRun command, CancellationToken cancellationToken)
    {
        var run = await runs.GetAsync(command.RunId, cancellationToken);
        if (run is null)
        {
            return Error.NotFound("Run");
        }

        if (run.Status == RunStatus.Completed || scheduler.Cancel(run.Id))
        {
            return run.Id;
        }

        // Nothing is executing it (for example the app restarted mid-run): close it here.
        run.Cancel(time.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);
        foreach (var runEvent in run.DomainEvents.OfType<IRunEvent>())
        {
            notifier.Publish(runEvent);
        }

        run.ClearDomainEvents();
        return run.Id;
    }
}

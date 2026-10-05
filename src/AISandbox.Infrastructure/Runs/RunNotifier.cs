using System.Collections.Concurrent;
using AISandbox.Application.Abstractions;
using AISandbox.Domain.Experimentation;

namespace AISandbox.Infrastructure.Runs;

/// <summary>
/// In-process publish/subscribe per run. A failing subscriber never affects the run or the others.
/// </summary>
internal sealed class RunNotifier : IRunNotifier
{
    private readonly ConcurrentDictionary<RunId, ConcurrentDictionary<Guid, Action<IRunEvent>>> _subscribers = new();

    public void Publish(IRunEvent runEvent)
    {
        if (!_subscribers.TryGetValue(runEvent.RunId, out var handlers))
        {
            return;
        }

        foreach (var handler in handlers.Values)
        {
            try
            {
                handler(runEvent);
            }
            catch (Exception)
            {
                // A broken listener (for example a closed browser circuit) must not stop the run.
            }
        }
    }

    public IDisposable Subscribe(RunId runId, Action<IRunEvent> handler)
    {
        var id = Guid.NewGuid();
        _subscribers.GetOrAdd(runId, _ => new()).TryAdd(id, handler);
        return new Subscription(() =>
        {
            if (_subscribers.TryGetValue(runId, out var handlers))
            {
                handlers.TryRemove(id, out _);
                if (handlers.IsEmpty)
                {
                    _subscribers.TryRemove(runId, out _);
                }
            }
        });
    }

    private sealed class Subscription(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}

using System.Collections.Concurrent;
using System.Threading.Channels;
using AISandbox.Application.Abstractions;
using AISandbox.Domain.Experimentation;

namespace AISandbox.Infrastructure.Runs;

/// <summary>
/// In-memory queue of runs waiting for <see cref="RunWorker"/>. Also remembers each run until it
/// finishes so it can be cancelled or awaited.
/// </summary>
internal sealed class RunScheduler : IRunScheduler
{
    private readonly Channel<RunId> _queue = Channel.CreateUnbounded<RunId>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<RunId, Entry> _entries = new();

    internal sealed class Entry
    {
        public CancellationTokenSource Cancellation { get; } = new();

        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal ChannelReader<RunId> Reader => _queue.Reader;

    public void Enqueue(RunId runId)
    {
        _entries[runId] = new Entry();
        _queue.Writer.TryWrite(runId);
    }

    public bool Cancel(RunId runId)
    {
        if (!_entries.TryGetValue(runId, out var entry))
        {
            return false;
        }

        try
        {
            entry.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        return true;
    }

    public Task WaitForCompletionAsync(RunId runId, CancellationToken cancellationToken = default) =>
        _entries.TryGetValue(runId, out var entry) ? entry.Completion.Task.WaitAsync(cancellationToken) : Task.CompletedTask;

    internal Entry? Find(RunId runId) => _entries.GetValueOrDefault(runId);

    internal void Finish(RunId runId)
    {
        if (_entries.TryRemove(runId, out var entry))
        {
            entry.Completion.TrySetResult();
        }
    }
}

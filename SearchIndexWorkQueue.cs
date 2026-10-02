namespace DesktopPlus;

/// <summary>One cooperative background I/O worker shared by indexing and folder warm-up.</summary>
internal sealed class SearchIndexWorkQueue
{
    internal static SearchIndexWorkQueue Shared { get; } = new();
    private readonly object _sync = new();
    private readonly PriorityQueue<Work, (int Priority, long Sequence)> _pending = new();
    private long _sequence;
    private bool _running;

    private sealed record Work(Func<Task> Action, CancellationToken Token, TaskCompletionSource Completion);

    internal Task RunAsync(Func<Task> action, CancellationToken token = default, int priority = 3)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            _pending.Enqueue(new Work(action, token, completion), (priority, ++_sequence));
            if (!_running)
            {
                _running = true;
                _ = Task.Run(PumpAsync);
            }
        }
        return completion.Task;
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            Work work;
            lock (_sync)
            {
                if (!_pending.TryDequeue(out work!, out _))
                {
                    _running = false;
                    return;
                }
            }
            try
            {
                work.Token.ThrowIfCancellationRequested();
                await work.Action().ConfigureAwait(false);
                work.Completion.TrySetResult();
            }
            catch (OperationCanceledException) { work.Completion.TrySetCanceled(); }
            catch (Exception exception) { work.Completion.TrySetException(exception); }
        }
    }
}

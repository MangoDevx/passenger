namespace Passenger.Debugger;

/// <summary>The engine's one lock, held by a request or the runtime's event loop, one at a time: not re-entrant, and
/// let go the moment its hold is disposed. A read (what a stop shows: frames, values, evaluations) waits behind every
/// other holder, even one that asked after it, so a continue, a step or a stop is never queued behind the stop's own
/// reads; the others go in turn.</summary>
public sealed class EngineLock
{
    private readonly Lock gate = new();
    private readonly LinkedList<TaskCompletionSource<IDisposable>> commands = new();
    private readonly LinkedList<TaskCompletionSource<IDisposable>> reads = new();
    private bool held;

    public Task<IDisposable> LockAsync(CancellationToken cancel = default) => Take(commands, cancel);

    /// <summary>The lock for a read, which it gets once no other holder waits.</summary>
    public Task<IDisposable> ReadLockAsync(CancellationToken cancel = default) => Take(reads, cancel);

    private Task<IDisposable> Take(LinkedList<TaskCompletionSource<IDisposable>> queue, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        TaskCompletionSource<IDisposable> waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        LinkedListNode<TaskCompletionSource<IDisposable>> place;
        lock (gate)
        {
            if (!held)
            {
                held = true;
                return Task.FromResult<IDisposable>(new Held(this));
            }
            place = queue.AddLast(waiting);
        }
        if (cancel.CanBeCanceled)
        {
            // Given up only while still waiting: once granted, the hold is the caller's to let go.
            cancel.Register(() =>
            {
                lock (gate)
                {
                    if (place.List is null)
                    {
                        return;
                    }
                    queue.Remove(place);
                }
                waiting.TrySetCanceled(cancel);
            });
        }
        return waiting.Task;
    }

    private void Release()
    {
        TaskCompletionSource<IDisposable> next;
        lock (gate)
        {
            LinkedList<TaskCompletionSource<IDisposable>> queue = commands.Count > 0 ? commands : reads;
            if (queue.First is not { } first)
            {
                held = false;
                return;
            }
            queue.RemoveFirst();
            next = first.Value;
        }
        next.SetResult(new Held(this));
    }

    /// <summary>A hold of the lock, let go once however often it is disposed.</summary>
    private sealed class Held(EngineLock owner) : IDisposable
    {
        private EngineLock? releasing = owner;

        public void Dispose() => Interlocked.Exchange(ref releasing, null)?.Release();
    }
}

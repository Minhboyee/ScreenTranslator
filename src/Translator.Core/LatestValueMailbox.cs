namespace Translator.Core;

public sealed class LatestValueMailbox<T> : IDisposable
{
    private readonly object gate = new();
    private TaskCompletionSource<bool>? signal;
    private T? value;
    private bool hasValue;
    private bool completed;

    public bool TryPublish(T nextValue)
    {
        return TryPublish(nextValue, out _);
    }

    public bool TryPublish(T nextValue, out T? replacedValue)
    {
        TaskCompletionSource<bool>? valueSignal;

        lock (gate)
        {
            if (completed)
            {
                replacedValue = default;
                return false;
            }

            replacedValue = hasValue ? value : default;
            value = nextValue;
            hasValue = true;
            valueSignal = signal;
            signal = null;
        }

        valueSignal?.TrySetResult(true);
        return true;
    }

    public bool TryRead(out T? nextValue)
    {
        lock (gate)
        {
            if (!hasValue)
            {
                nextValue = default;
                return false;
            }

            nextValue = value;
            value = default;
            hasValue = false;
            return true;
        }
    }

    public async ValueTask<T> ReadAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task waitTask;

            lock (gate)
            {
                if (hasValue)
                {
                    var nextValue = value;
                    value = default;
                    hasValue = false;
                    return nextValue!;
                }

                if (completed)
                {
                    throw new MailboxCompletedException();
                }

                signal ??= new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                waitTask = signal.Task;
            }

            await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Complete()
    {
        TaskCompletionSource<bool>? valueSignal;

        lock (gate)
        {
            if (completed)
            {
                return;
            }

            completed = true;
            valueSignal = signal;
            signal = null;
        }

        valueSignal?.TrySetResult(true);
    }

    public void Dispose()
    {
        Complete();
    }
}

public sealed class MailboxCompletedException : InvalidOperationException
{
    public MailboxCompletedException()
        : base("The mailbox has been completed.")
    {
    }
}

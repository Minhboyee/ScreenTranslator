namespace Translator.Windows;

internal sealed class LatestOcrFrameScheduler<TFrame> : IAsyncDisposable
    where TFrame : class, IDisposable
{
    private readonly object gate = new();
    private readonly Func<TFrame, long, Task> processAsync;
    private readonly Func<long, bool> isCurrentEpoch;
    private readonly TimeSpan sampleInterval;
    private readonly Func<DateTimeOffset> clock;
    private readonly CancellationTokenSource stopCancellation = new();
    private TFrame? pendingFrame;
    private long pendingEpoch;
    private DateTimeOffset lastStart;
    private bool hasStarted;
    private Task? workerTask;
    private TaskCompletionSource? intervalWake;
    private Task? shutdownTask;
    private bool stopped;

    internal Action? WorkerRetirementProbe { get; set; }

    public LatestOcrFrameScheduler(
        Func<TFrame, long, Task> processAsync,
        Func<long, bool> isCurrentEpoch,
        TimeSpan sampleInterval,
        Func<DateTimeOffset>? clock = null)
    {
        this.processAsync = processAsync ?? throw new ArgumentNullException(nameof(processAsync));
        this.isCurrentEpoch = isCurrentEpoch ?? throw new ArgumentNullException(nameof(isCurrentEpoch));
        if (sampleInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleInterval));
        }

        this.sampleInterval = sampleInterval;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public bool Submit(TFrame frame, long epoch, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(frame);
        TFrame? replaced = null;

        lock (gate)
        {
            if (stopped || !isCurrentEpoch(epoch))
            {
                replaced = frame;
            }
            else
            {
                replaced = pendingFrame;
                pendingFrame = frame;
                pendingEpoch = epoch;
                StartWorkerLocked();
            }
        }

        replaced?.Dispose();

        return !ReferenceEquals(replaced, frame);
    }

    internal bool StartEligible(DateTimeOffset now)
    {
        lock (gate)
        {
            if (stopped || pendingFrame is null || !IsEligible(now))
            {
                return false;
            }

            intervalWake?.TrySetResult();
            return true;
        }
    }

    public ValueTask DisposeAsync()
    {
        TFrame? pending;
        Task? worker;
        Task shutdown;

        lock (gate)
        {
            if (shutdownTask is not null)
            {
                return new ValueTask(shutdownTask);
            }

            stopped = true;
            stopCancellation.Cancel();
            pending = pendingFrame;
            pendingFrame = null;
            pendingEpoch = 0;
            intervalWake?.TrySetResult();
            intervalWake = null;
            worker = workerTask;
            shutdown = ShutdownAsync(pending, worker);
            shutdownTask = shutdown;
        }

        return new ValueTask(shutdown);
    }

    private bool IsEligible(DateTimeOffset now)
    {
        return !hasStarted || now - lastStart >= sampleInterval;
    }

    private void StartWorkerLocked()
    {
        if (workerTask is not null)
        {
            return;
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        workerTask = completion.Task;
        _ = RunWorkerAsync(completion);
    }

    private async Task ShutdownAsync(TFrame? pending, Task? worker)
    {
        pending?.Dispose();
        if (worker is not null)
        {
            await worker.ConfigureAwait(false);
        }

        stopCancellation.Dispose();
    }

    private async Task RunWorkerAsync(TaskCompletionSource completion)
    {
        try
        {
            while (true)
            {
                TFrame? frame = null;
                long epoch = 0;
                TaskCompletionSource? wake = null;
                TimeSpan remaining = TimeSpan.Zero;
                bool retireWorker = false;

                lock (gate)
                {
                    if (stopped || pendingFrame is null)
                    {
                        if (ReferenceEquals(workerTask, completion.Task))
                        {
                            workerTask = null;
                        }

                        retireWorker = true;
                    }
                    else
                    {
                        var now = clock();
                        if (hasStarted && !IsEligible(now))
                        {
                            remaining = sampleInterval - (now - lastStart);
                            wake = new TaskCompletionSource(
                                TaskCreationOptions.RunContinuationsAsynchronously);
                            intervalWake = wake;
                        }
                        else
                        {
                            frame = pendingFrame;
                            epoch = pendingEpoch;
                            pendingFrame = null;
                            pendingEpoch = 0;
                            hasStarted = true;
                            lastStart = now;
                        }
                    }
                }

                if (retireWorker)
                {
                    WorkerRetirementProbe?.Invoke();
                    return;
                }

                if (wake is not null)
                {
                    try
                    {
                        var delay = Task.Delay(remaining, stopCancellation.Token);
                        await Task.WhenAny(delay, wake.Task).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    finally
                    {
                        lock (gate)
                        {
                            if (ReferenceEquals(intervalWake, wake))
                            {
                                intervalWake = null;
                            }
                        }
                    }

                    continue;
                }

                try
                {
                    if (isCurrentEpoch(epoch))
                    {
                        await processAsync(frame!, epoch).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // A failed OCR frame must not stop the latest-value pipeline.
                }
                finally
                {
                    frame!.Dispose();
                }
            }
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(workerTask, completion.Task))
                {
                    workerTask = null;
                    intervalWake?.TrySetResult();
                    intervalWake = null;
                }
            }
            completion.TrySetResult();
        }
    }
}

namespace Translator.Core;

public sealed class TranslationSession : IAsyncDisposable, IDisposable
{
    private readonly object gate = new();
    private readonly ITextTranslator translator;
    private readonly ITranslationMemory memory;
    private readonly LatestValueMailbox<PendingTranslation> mailbox = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly Dictionary<long, ActiveTranslation> activeTranslations = new();
    private readonly Dictionary<TranslationMemoryKey, InFlightTranslation> inFlight = new();
    private readonly HashSet<Task> runningWork = new();
    private readonly Task pumpTask;
    private Task? disposalTask;
    private long currentGeneration;
    private long? lastPublishedGeneration;
    private bool disposed;

    public TranslationSession(ITextTranslator translator, ITranslationMemory? memory = null)
    {
        this.translator = translator ?? throw new ArgumentNullException(nameof(translator));
        this.memory = memory ?? new TranslationMemoryCache();
        pumpTask = PumpAsync();
    }

    public event Action<TranslationPublication>? ResultPublished;

    public long CurrentGeneration
    {
        get
        {
            lock (gate)
            {
                return currentGeneration;
            }
        }
    }

    public long? LastPublishedGeneration
    {
        get
        {
            lock (gate)
            {
                return lastPublishedGeneration;
            }
        }
    }

    public Task<TranslationPublication?> SubmitAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var completion = new TaskCompletionSource<TranslationPublication?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        PendingTranslation pending;
        List<CancellationTokenSource>? cancellations = null;
        PendingTranslation? replaced;

        lock (gate)
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(TranslationSession));
            }

            pending = new PendingTranslation(++currentGeneration, request, completion);

            foreach (var active in activeTranslations.Values)
            {
                if (active.Pending.Request.MemoryKey == request.MemoryKey)
                {
                    continue;
                }

                cancellations ??= new List<CancellationTokenSource>();
                cancellations.Add(active.Cancellation);
            }

            if (!mailbox.TryPublish(pending, out replaced))
            {
                throw new ObjectDisposedException(nameof(TranslationSession));
            }

            replaced?.Completion.TrySetResult(null);
        }

        if (cancellations is not null)
        {
            foreach (var cancellation in cancellations)
            {
                cancellation.Cancel();
            }
        }

        return pending.Completion.Task.WaitAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            disposalTask ??= DisposeCoreAsync();
            return new ValueTask(disposalTask);
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private async Task PumpAsync()
    {
        try
        {
            while (true)
            {
                PendingTranslation pending;
                try
                {
                    pending = await mailbox.ReadAsync(shutdown.Token).ConfigureAwait(false);
                }
                catch (MailboxCompletedException)
                {
                    return;
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                {
                    return;
                }

                var work = ProcessAsync(pending);
                lock (gate)
                {
                    runningWork.Add(work);
                }

                _ = ObserveWorkAsync(work);
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
        }
    }

    private async Task ProcessAsync(PendingTranslation pending)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        var active = new ActiveTranslation(pending, cancellation);

        lock (gate)
        {
            if (disposed || pending.Generation != currentGeneration)
            {
                pending.Completion.TrySetResult(null);
                return;
            }

            activeTranslations.Add(pending.Generation, active);
        }

        try
        {
            var translatedText = await GetTranslationAsync(
                    pending.Request,
                    cancellation.Token)
                .WaitAsync(cancellation.Token)
                .ConfigureAwait(false);

            PublishIfCurrent(pending, translatedText);
        }
        catch (OperationCanceledException)
        {
            pending.Completion.TrySetResult(null);
        }
        catch (Exception exception)
        {
            if (IsCurrent(pending))
            {
                pending.Completion.TrySetException(exception);
            }
            else
            {
                pending.Completion.TrySetResult(null);
            }
        }
        finally
        {
            lock (gate)
            {
                activeTranslations.Remove(pending.Generation);
            }
        }
    }

    private Task<string> GetTranslationAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        var key = request.MemoryKey;
        if (memory.TryGet(key, out var cachedText))
        {
            return Task.FromResult(cachedText);
        }

        lock (gate)
        {
            if (memory.TryGet(key, out cachedText))
            {
                return Task.FromResult(cachedText);
            }

            if (inFlight.TryGetValue(key, out var existing))
            {
                return existing.Task;
            }

            var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var task = TranslateAndCacheAsync(request, key, operationCancellation.Token);
            inFlight.Add(key, new InFlightTranslation(task, operationCancellation));
            _ = RemoveInFlightAsync(key, task);
            return task;
        }
    }

    private async Task<string> TranslateAndCacheAsync(
        TranslationRequest request,
        TranslationMemoryKey key,
        CancellationToken cancellationToken)
    {
        var result = await translator.TranslateAsync(request, cancellationToken).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(result);
        memory.Set(key, result.TranslatedText);
        return result.TranslatedText;
    }

    private async Task RemoveInFlightAsync(TranslationMemoryKey key, Task<string> task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            InFlightTranslation? removed = null;
            lock (gate)
            {
                if (inFlight.TryGetValue(key, out var current) && current.Task == task)
                {
                    inFlight.Remove(key);
                    removed = current;
                }
            }

            removed?.Cancellation.Dispose();
        }
    }

    private void PublishIfCurrent(PendingTranslation pending, string translatedText)
    {
        lock (gate)
        {
            if (disposed || pending.Generation != currentGeneration)
            {
                pending.Completion.TrySetResult(null);
                return;
            }

            var result = new TranslationResult(pending.Request, translatedText);
            var publication = new TranslationPublication(pending.Generation, result);
            lastPublishedGeneration = pending.Generation;
            pending.Completion.TrySetResult(publication);

            try
            {
                ResultPublished?.Invoke(publication);
            }
            catch
            {
                // Event handlers must not change publication or session state.
            }
        }
    }

    private bool IsCurrent(PendingTranslation pending)
    {
        lock (gate)
        {
            return !disposed && pending.Generation == currentGeneration;
        }
    }

    private async Task ObserveWorkAsync(Task work)
    {
        try
        {
            await work.ConfigureAwait(false);
        }
        finally
        {
            lock (gate)
            {
                runningWork.Remove(work);
            }
        }
    }

    private async Task DisposeCoreAsync()
    {
        List<CancellationTokenSource> cancellations;

        lock (gate)
        {
            disposed = true;
            cancellations = activeTranslations.Values
                .Select(active => active.Cancellation)
                .ToList();
        }

        mailbox.Complete();
        shutdown.Cancel();

        foreach (var cancellation in cancellations)
        {
            cancellation.Cancel();
        }

        await pumpTask.ConfigureAwait(false);

        while (true)
        {
            Task[] work;
            lock (gate)
            {
                work = runningWork.ToArray();
            }

            if (work.Length == 0)
            {
                break;
            }

            await Task.WhenAll(work).ConfigureAwait(false);
        }

        while (mailbox.TryRead(out var pending))
        {
            pending?.Completion.TrySetResult(null);
        }

        shutdown.Dispose();
    }

    private sealed record PendingTranslation(
        long Generation,
        TranslationRequest Request,
        TaskCompletionSource<TranslationPublication?> Completion);

    private sealed record ActiveTranslation(
        PendingTranslation Pending,
        CancellationTokenSource Cancellation);

    private sealed record InFlightTranslation(
        Task<string> Task,
        CancellationTokenSource Cancellation);
}

using System.Collections.ObjectModel;
using Translator.Core;

namespace Translator_App_WinUI;

internal sealed class LatestValueHandoff<T>
{
    private readonly object gate = new();
    private T? value;
    private bool hasValue;

    public bool HasValue
    {
        get
        {
            lock (gate)
            {
                return hasValue;
            }
        }
    }

    public void Publish(T nextValue)
    {
        ArgumentNullException.ThrowIfNull(nextValue);
        lock (gate)
        {
            value = nextValue;
            hasValue = true;
        }
    }

    public bool TryTake(out T nextValue)
    {
        lock (gate)
        {
            if (!hasValue)
            {
                nextValue = default!;
                return false;
            }

            nextValue = value!;
            value = default;
            hasValue = false;
            return true;
        }
    }
}

internal sealed record LineTranslationRequest
{
    public LineTranslationRequest(
        string lineId,
        OcrText sourceLine,
        TranslationRequest request)
    {
        ArgumentNullException.ThrowIfNull(lineId);
        SourceLine = sourceLine ?? throw new ArgumentNullException(nameof(sourceLine));
        Request = request ?? throw new ArgumentNullException(nameof(request));
        LineId = lineId;
    }

    public LineTranslationRequest(OcrText sourceLine, TranslationRequest request)
        : this(string.Empty, sourceLine, request)
    {
    }

    // Kept for the app-side request contract. The coordinator deliberately does not use this
    // value as an identity; occurrence identity is derived from normalized text and its ordinal.
    public string LineId { get; }

    public OcrText SourceLine { get; }

    public TranslationRequest Request { get; }
}

internal enum LinePresentationState
{
    Pending,
    Success,
    Error
}

internal sealed record LinePresentationLine
{
    public LinePresentationLine(
        string occurrenceId,
        TranslationMemoryKey translationIdentity,
        OcrText sourceLine,
        LinePresentationState state,
        string? translatedText,
        Exception? error)
    {
        ArgumentNullException.ThrowIfNull(occurrenceId);
        SourceLine = sourceLine ?? throw new ArgumentNullException(nameof(sourceLine));
        OccurrenceId = occurrenceId;
        TranslationIdentity = translationIdentity;
        State = state;
        TranslatedText = translatedText;
        Error = error;
    }

    public string OccurrenceId { get; }

    public TranslationMemoryKey TranslationIdentity { get; }

    public OcrText SourceLine { get; }

    public LinePresentationState State { get; }

    public string? TranslatedText { get; }

    public Exception? Error { get; }
}

internal sealed record LinePresentationSnapshot
{
    public LinePresentationSnapshot(
        long generation,
        IEnumerable<LinePresentationLine> lines,
        bool isComplete,
        bool isClear = false,
        long revision = 0)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (generation < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        Generation = generation;
        Revision = revision;
        Lines = new ReadOnlyCollection<LinePresentationLine>(lines.ToArray());
        IsComplete = isComplete;
        IsClear = isClear;
    }

    public long Generation { get; }

    public long Revision { get; }

    public IReadOnlyList<LinePresentationLine> Lines { get; }

    public bool IsComplete { get; }

    public bool IsClear { get; }
}

internal static class PresentationSnapshotOrdering
{
    public static bool IsNewer(
        LinePresentationSnapshot snapshot,
        long generation,
        long revision) =>
        snapshot.Generation > generation ||
        (snapshot.Generation == generation && snapshot.Revision > revision);
}

/// <summary>
/// Keeps translation work keyed by translation identity while composing immutable snapshots from
/// the current OCR document. Canceled provider calls remain tracked until the provider task really
/// finishes, so cancellation that is ignored by a provider cannot exceed the global call bound.
/// </summary>
internal sealed class BoundedLineTranslationCoordinator : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly ITextTranslator translator;
    private readonly ITranslationMemory memory;
    private readonly int maxConcurrency;
    private readonly Dictionary<TranslationMemoryKey, ActiveCall> activeCalls = new();
    private readonly Dictionary<TranslationMemoryKey, TranslationOutcome> outcomes = new();
    private readonly Dictionary<TranslationMemoryKey, TranslationRequest> desiredRequests = new();
    private readonly HashSet<TranslationMemoryKey> desiredKeys = new();
    private readonly Queue<TranslationMemoryKey> pendingKeys = new();
    private readonly HashSet<Task> runningWork = new();
    private Task? disposalTask;
    private IReadOnlyList<DesiredLine> currentLines = Array.Empty<DesiredLine>();
    private LinePresentationSnapshot currentSnapshot = new(0, Array.Empty<LinePresentationLine>(), true, true);
    private long currentGeneration;
    private long presentationRevision;
    private bool disposed;

    public BoundedLineTranslationCoordinator(
        ITextTranslator translator,
        ITranslationMemory? memory = null,
        int maxConcurrency = 3)
    {
        this.translator = translator ?? throw new ArgumentNullException(nameof(translator));
        this.memory = memory ?? new TranslationMemoryCache();
        if (maxConcurrency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        }

        this.maxConcurrency = maxConcurrency;
    }

    public event Action<LinePresentationSnapshot>? PresentationPublished;

    internal LinePresentationSnapshot CurrentSnapshot
    {
        get
        {
            lock (gate)
            {
                return currentSnapshot;
            }
        }
    }

    internal int ActiveProviderCallCount
    {
        get
        {
            lock (gate)
            {
                return activeCalls.Count;
            }
        }
    }

    internal int PendingTranslationCount
    {
        get
        {
            lock (gate)
            {
                return pendingKeys.Count;
            }
        }
    }

    public LinePresentationSnapshot Reconcile(
        long generation,
        IReadOnlyList<LineTranslationRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (generation <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        if (requests.Count == 0)
        {
            return Clear(generation);
        }

        List<ActiveCall> callsToStart;
        LinePresentationSnapshot snapshot;

        lock (gate)
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(BoundedLineTranslationCoordinator));
            }

            if (generation < currentGeneration)
            {
                return currentSnapshot;
            }

            currentGeneration = generation;
            currentLines = CreateDesiredLines(requests);
            desiredKeys.Clear();
            desiredRequests.Clear();
            foreach (var line in currentLines)
            {
                desiredKeys.Add(line.TranslationIdentity);
                desiredRequests.TryAdd(line.TranslationIdentity, line.Request.Request);
            }

            CancelRemovedCallsLocked();
            RemoveObsoleteOutcomesLocked();
            RebuildPendingLocked();
            callsToStart = ReserveCallsLocked();
            snapshot = currentSnapshot = ComposeSnapshotLocked(isClear: false);
        }

        Publish(snapshot);
        StartCalls(callsToStart);
        return snapshot;
    }

    public LinePresentationSnapshot Clear(long generation)
    {
        if (generation <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        LinePresentationSnapshot snapshot;
        lock (gate)
        {
            if (disposed)
            {
                return currentSnapshot;
            }

            if (generation < currentGeneration)
            {
                return currentSnapshot;
            }

            currentGeneration = generation;
            currentLines = Array.Empty<DesiredLine>();
            desiredKeys.Clear();
            desiredRequests.Clear();
            pendingKeys.Clear();
            outcomes.Clear();
            CancelAllCallsLocked();
            snapshot = currentSnapshot = ComposeSnapshotLocked(isClear: true);
        }

        Publish(snapshot);
        return snapshot;
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            disposalTask ??= DisposeCoreAsync();
            return new ValueTask(disposalTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Task[] work;
        lock (gate)
        {
            disposed = true;
            CancelAllCallsLocked();
            work = runningWork.ToArray();
        }

        while (work.Length > 0)
        {
            try
            {
                await Task.WhenAll(work).ConfigureAwait(false);
            }
            catch
            {
                // ExecuteCallAsync reports provider failures through the snapshot and does not
                // allow them to escape. This also keeps disposal draining if a provider violates
                // that contract.
            }

            lock (gate)
            {
                work = runningWork.ToArray();
            }
        }
    }

    private static IReadOnlyList<DesiredLine> CreateDesiredLines(
        IReadOnlyList<LineTranslationRequest> requests)
    {
        var duplicateOrdinals = new Dictionary<string, int>(StringComparer.Ordinal);
        var lines = new DesiredLine[requests.Count];

        for (var index = 0; index < requests.Count; index++)
        {
            var request = requests[index] ?? throw new ArgumentNullException(nameof(requests));
            var normalizedText = request.SourceLine.Text.NormalizedValue;
            duplicateOrdinals.TryGetValue(normalizedText, out var ordinal);
            duplicateOrdinals[normalizedText] = ordinal + 1;
            var occurrenceId = $"{normalizedText}\u001f{ordinal}";
            lines[index] = new DesiredLine(
                occurrenceId,
                request,
                request.Request.MemoryKey);
        }

        return new ReadOnlyCollection<DesiredLine>(lines);
    }

    private void CancelRemovedCallsLocked()
    {
        foreach (var call in activeCalls.Values)
        {
            if (!desiredKeys.Contains(call.Key))
            {
                CancelCall(call);
            }
        }
    }

    private void CancelAllCallsLocked()
    {
        foreach (var call in activeCalls.Values)
        {
            CancelCall(call);
        }
    }

    private static void CancelCall(ActiveCall call)
    {
        try
        {
            call.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void RemoveObsoleteOutcomesLocked()
    {
        foreach (var key in outcomes.Keys.Where(key => !desiredKeys.Contains(key)).ToArray())
        {
            outcomes.Remove(key);
        }
    }

    private void RebuildPendingLocked()
    {
        pendingKeys.Clear();

        foreach (var key in desiredKeys)
        {
            if (outcomes.ContainsKey(key) || activeCalls.ContainsKey(key))
            {
                continue;
            }

            if (memory.TryGet(key, out var translatedText))
            {
                outcomes[key] = TranslationOutcome.Success(translatedText);
                continue;
            }

            pendingKeys.Enqueue(key);
        }
    }

    private List<ActiveCall> ReserveCallsLocked()
    {
        var calls = new List<ActiveCall>();
        while (activeCalls.Count < maxConcurrency && pendingKeys.Count > 0)
        {
            var key = pendingKeys.Dequeue();
            if (!desiredKeys.Contains(key) ||
                outcomes.ContainsKey(key) ||
                activeCalls.ContainsKey(key) ||
                !desiredRequests.TryGetValue(key, out var request))
            {
                continue;
            }

            var call = new ActiveCall(key, request);
            activeCalls.Add(key, call);
            calls.Add(call);
        }

        return calls;
    }

    private void StartCalls(IEnumerable<ActiveCall> calls)
    {
        foreach (var call in calls)
        {
            var shouldLaunch = false;
            List<ActiveCall>? replacementCalls = null;
            lock (gate)
            {
                if (disposed ||
                    !desiredKeys.Contains(call.Key) ||
                    !activeCalls.TryGetValue(call.Key, out var current) ||
                    !ReferenceEquals(current, call))
                {
                    activeCalls.Remove(call.Key);
                    CancelCall(call);
                    if (!disposed)
                    {
                        RebuildPendingLocked();
                        replacementCalls = ReserveCallsLocked();
                    }
                }
                else
                {
                    // ExecuteCallAsync cannot reach the provider until Launch is completed.
                    // Create and track it while holding the same gate used by disposal, then
                    // release it below. This closes the start/dispose race without calling a
                    // provider under the coordinator lock.
                    var work = ExecuteCallAsync(call);
                    call.Work = work;
                    runningWork.Add(work);
                    shouldLaunch = true;
                }
            }

            if (shouldLaunch)
            {
                call.Launch.TrySetResult(true);
            }

            if (replacementCalls is not null)
            {
                StartCalls(replacementCalls);
            }
        }
    }

    private async Task ExecuteCallAsync(ActiveCall call)
    {
        await call.Launch.Task.ConfigureAwait(false);

        TranslationResult? translation = null;
        Exception? error = null;
        try
        {
            translation = await translator.TranslateAsync(call.Request, call.Cancellation.Token)
                .ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(translation);
        }
        catch (Exception exception)
        {
            error = exception;
        }

        try
        {
            CompleteCall(call, translation, error);
        }
        finally
        {
            call.Cancellation.Dispose();
            lock (gate)
            {
                if (call.Work is not null)
                {
                    runningWork.Remove(call.Work);
                }
            }
        }
    }

    private void CompleteCall(
        ActiveCall call,
        TranslationResult? translation,
        Exception? error)
    {
        LinePresentationSnapshot? snapshot = null;
        List<ActiveCall> callsToStart;

        lock (gate)
        {
            if (!activeCalls.TryGetValue(call.Key, out var current) ||
                !ReferenceEquals(current, call))
            {
                // A call can only be replaced after it has finished, but keep this guard so a
                // late provider callback can never mutate current presentation state.
                return;
            }

            activeCalls.Remove(call.Key);
            if (translation is not null)
            {
                memory.Set(call.Key, translation.TranslatedText);
            }

            if (desiredKeys.Contains(call.Key))
            {
                if (translation is not null && !call.Cancellation.IsCancellationRequested)
                {
                    outcomes[call.Key] = TranslationOutcome.Success(translation.TranslatedText);
                }
                else if (call.Cancellation.IsCancellationRequested)
                {
                    outcomes.Remove(call.Key);
                }
                else
                {
                    outcomes[call.Key] = TranslationOutcome.FromError(
                        error ?? new InvalidOperationException("Translation did not return a result."));
                }
            }

            RebuildPendingLocked();
            callsToStart = ReserveCallsLocked();

            if (desiredKeys.Contains(call.Key))
            {
                snapshot = currentSnapshot = ComposeSnapshotLocked(isClear: false);
            }
        }

        if (snapshot is not null)
        {
            Publish(snapshot);
        }

        StartCalls(callsToStart);
    }

    private LinePresentationSnapshot ComposeSnapshotLocked(bool isClear)
    {
        var lines = new LinePresentationLine[currentLines.Count];
        var isComplete = true;
        for (var index = 0; index < currentLines.Count; index++)
        {
            var line = currentLines[index];
            var state = LinePresentationState.Pending;
            string? translatedText = null;
            Exception? error = null;

            if (outcomes.TryGetValue(line.TranslationIdentity, out var outcome))
            {
                state = outcome.State;
                translatedText = outcome.TranslatedText;
                error = outcome.Error;
            }

            if (state == LinePresentationState.Pending)
            {
                isComplete = false;
            }

            lines[index] = new LinePresentationLine(
                line.OccurrenceId,
                line.TranslationIdentity,
                line.Request.SourceLine,
                state,
                translatedText,
                error);
        }

        return new LinePresentationSnapshot(
            currentGeneration,
            lines,
            isComplete,
            isClear,
            ++presentationRevision);
    }

    private void Publish(LinePresentationSnapshot snapshot)
    {
        try
        {
            PresentationPublished?.Invoke(snapshot);
        }
        catch
        {
            // Presentation subscribers must not change provider or coordinator state.
        }
    }

    private sealed record DesiredLine(
        string OccurrenceId,
        LineTranslationRequest Request,
        TranslationMemoryKey TranslationIdentity);

    private sealed record TranslationOutcome(
        LinePresentationState State,
        string? TranslatedText,
        Exception? Error)
    {
        public static TranslationOutcome Success(string translatedText) =>
            new(LinePresentationState.Success, translatedText, null);

        public static TranslationOutcome FromError(Exception error) =>
            new(LinePresentationState.Error, null, error);
    }

    private sealed class ActiveCall
    {
        public ActiveCall(TranslationMemoryKey key, TranslationRequest request)
        {
            Key = key;
            Request = request;
            Cancellation = new CancellationTokenSource();
            Launch = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public TranslationMemoryKey Key { get; }

        public TranslationRequest Request { get; }

        public CancellationTokenSource Cancellation { get; }

        public TaskCompletionSource<bool> Launch { get; }

        public Task? Work { get; set; }
    }
}

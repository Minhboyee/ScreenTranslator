using System.Collections.Concurrent;
using Translator.Core;
using Translator_App_WinUI;

namespace Translator.Core.Tests;

public sealed class BoundedLineTranslationCoordinatorTests
{
    [Fact]
    public async Task Unchanged_line_retains_translation_when_bounds_change()
    {
        var translator = new ManualTranslator(ignoreCancellation: true);
        await using var coordinator = new BoundedLineTranslationCoordinator(translator);
        var first = Line("same", 10, 10);

        coordinator.Reconcile(1, [first]);
        (await translator.WaitForInvocationAsync("same")).Complete("translated");
        await WaitUntilAsync(() => coordinator.CurrentSnapshot.Lines[0].State == LinePresentationState.Success);

        var moved = Line(" same ", 100, 200);
        var retained = coordinator.Reconcile(2, [moved]);

        Assert.Equal(LinePresentationState.Success, retained.Lines[0].State);
        Assert.Equal("translated", retained.Lines[0].TranslatedText);
        Assert.Equal(100, retained.Lines[0].SourceLine.Bounds.Left);
        Assert.Equal(1, translator.InvocationCount);
    }

    [Fact]
    public async Task Removed_line_is_canceled_and_does_not_publish_after_completion()
    {
        var translator = new ManualTranslator(ignoreCancellation: true);
        await using var coordinator = new BoundedLineTranslationCoordinator(translator);
        var snapshots = new ConcurrentQueue<LinePresentationSnapshot>();
        coordinator.PresentationPublished += snapshots.Enqueue;

        coordinator.Reconcile(1, [Line("removed", 0, 0)]);
        var removed = await translator.WaitForInvocationAsync("removed");
        var current = coordinator.Reconcile(2, [Line("current", 20, 20)]);

        Assert.True(removed.CancellationObserved.Task.IsCompleted);
        Assert.Equal("current", current.Lines[0].SourceLine.Text.Value.Trim());
        removed.Complete("stale");
        await WaitUntilAsync(() => coordinator.ActiveProviderCallCount == 1);

        Assert.DoesNotContain(
            snapshots.Where(snapshot => snapshot.Generation == 2),
            snapshot => snapshot.Lines.Any(line => line.TranslatedText == "stale"));

        (await translator.WaitForInvocationAsync("current")).Complete("now");
        await WaitUntilAsync(() => coordinator.CurrentSnapshot.IsComplete);
        var cached = coordinator.Reconcile(3, [Line("removed", 80, 80)]);
        Assert.Equal(LinePresentationState.Success, cached.Lines[0].State);
        Assert.Equal("stale", cached.Lines[0].TranslatedText);
        Assert.Equal(2, translator.InvocationCount);
    }

    [Fact]
    public async Task Duplicate_occurrences_share_one_provider_call_and_result()
    {
        var translator = new ManualTranslator(ignoreCancellation: true);
        await using var coordinator = new BoundedLineTranslationCoordinator(translator);
        var first = Line("duplicate", 0, 0);
        var second = Line(" duplicate ", 50, 50);

        var pending = coordinator.Reconcile(1, [first, second]);
        Assert.All(pending.Lines, line => Assert.Equal(LinePresentationState.Pending, line.State));
        Assert.NotEqual(pending.Lines[0].OccurrenceId, pending.Lines[1].OccurrenceId);

        var invocation = await translator.WaitForInvocationAsync("duplicate");
        Assert.Equal(1, translator.InvocationCount);
        invocation.Complete("shared");
        await WaitUntilAsync(() => coordinator.CurrentSnapshot.IsComplete);

        var completed = coordinator.CurrentSnapshot;
        Assert.Equal(2, completed.Lines.Count);
        Assert.All(completed.Lines, line =>
        {
            Assert.Equal(LinePresentationState.Success, line.State);
            Assert.Equal("shared", line.TranslatedText);
        });
    }

    [Fact]
    public async Task Cancellation_ignoring_provider_result_cannot_render_stale_state()
    {
        var translator = new ManualTranslator(ignoreCancellation: true);
        await using var coordinator = new BoundedLineTranslationCoordinator(translator);
        var published = new ConcurrentQueue<LinePresentationSnapshot>();
        coordinator.PresentationPublished += published.Enqueue;

        coordinator.Reconcile(1, [Line("old", 0, 0)]);
        var old = await translator.WaitForInvocationAsync("old");
        coordinator.Reconcile(2, [Line("new", 0, 0)]);
        old.Complete("old result");

        await WaitUntilAsync(() => translator.InvocationCount == 2);
        Assert.DoesNotContain(
            published.Where(snapshot => snapshot.Generation == 2),
            snapshot => snapshot.Lines.Any(line => line.TranslatedText == "old result"));

        (await translator.WaitForInvocationAsync("new")).Complete("new result");
    }

    [Fact]
    public async Task Ignored_cancellation_keeps_actual_provider_calls_at_three()
    {
        var translator = new ManualTranslator(ignoreCancellation: true);
        await using var coordinator = new BoundedLineTranslationCoordinator(translator);
        coordinator.Reconcile(1, [Line("a"), Line("b"), Line("c")]);
        var a = await translator.WaitForInvocationAsync("a");
        var b = await translator.WaitForInvocationAsync("b");
        var c = await translator.WaitForInvocationAsync("c");

        coordinator.Reconcile(2, [Line("d"), Line("e"), Line("f")]);
        Assert.Equal(3, translator.MaxActiveCalls);
        Assert.Equal(3, coordinator.ActiveProviderCallCount);
        Assert.Equal(3, translator.InvocationCount);

        a.Complete("a");
        var d = await translator.WaitForInvocationAsync("d");
        Assert.Equal(3, coordinator.ActiveProviderCallCount);
        b.Complete("b");
        c.Complete("c");
        d.Complete("d");
        (await translator.WaitForInvocationAsync("e")).Complete("e");
        (await translator.WaitForInvocationAsync("f")).Complete("f");
    }

    [Fact]
    public async Task Reversed_publication_keeps_the_newer_snapshot_in_a_latest_value_handoff()
    {
        var translator = new ManualTranslator(ignoreCancellation: true);
        await using var coordinator = new BoundedLineTranslationCoordinator(translator);
        var first = coordinator.Reconcile(1, [Line("a")]);
        var firstInvocation = await translator.WaitForInvocationAsync("a");
        var firstSuccessRevision = first.Revision + 1;
        var firstPublicationEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstPublication = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new ConcurrentQueue<LinePresentationSnapshot>();

        coordinator.PresentationPublished += snapshot =>
        {
            if (snapshot.Revision == firstSuccessRevision)
            {
                firstPublicationEntered.TrySetResult(true);
                releaseFirstPublication.Task.GetAwaiter().GetResult();
            }

            published.Enqueue(snapshot);
        };

        firstInvocation.Complete("A");
        await firstPublicationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var newer = coordinator.Reconcile(2, [Line("b")]);
        releaseFirstPublication.TrySetResult(true);
        await translator.WaitForInvocationAsync("b");
        await WaitUntilAsync(() => published.Any(snapshot => snapshot.Revision == firstSuccessRevision));

        var publicationOrder = published.ToArray();
        if (publicationOrder.Length < 2 ||
            publicationOrder[0].Revision != newer.Revision ||
            publicationOrder[1].Revision != firstSuccessRevision)
        {
            throw new InvalidOperationException("The publication order was not reversed deterministically.");
        }

        var accepted = publicationOrder.Aggregate(
            (LinePresentationSnapshot?)null,
            (current, snapshot) => current is null ||
                                   PresentationSnapshotOrdering.IsNewer(
                                       snapshot,
                                       current.Generation,
                                       current.Revision)
                ? snapshot
                : current);
        if (accepted is null ||
            accepted.Generation != newer.Generation ||
            accepted.Revision != newer.Revision)
        {
            throw new InvalidOperationException("Older publication replaced the latest snapshot.");
        }

        (await translator.WaitForInvocationAsync("b")).Complete("B");
    }

    [Fact]
    public async Task Changed_pending_content_replaces_the_previous_successful_state()
    {
        var translator = new ManualTranslator(ignoreCancellation: true);
        await using var coordinator = new BoundedLineTranslationCoordinator(translator);

        coordinator.Reconcile(1, [Line("A")]);
        (await translator.WaitForInvocationAsync("A")).Complete("translated A");
        await WaitUntilAsync(() => coordinator.CurrentSnapshot.IsComplete);

        var replacement = coordinator.Reconcile(2, [Line("B")]);

        Assert.Single(replacement.Lines);
        Assert.Equal("B", replacement.Lines[0].SourceLine.Text.Value);
        Assert.Equal(LinePresentationState.Pending, replacement.Lines[0].State);
        Assert.Null(replacement.Lines[0].TranslatedText);
        Assert.Equal(2, replacement.Generation);
        (await translator.WaitForInvocationAsync("B")).Complete("translated B");
    }

    [Fact]
    public async Task Mixed_changed_content_retains_only_unchanged_successful_identities()
    {
        var translator = new ManualTranslator(ignoreCancellation: true);
        await using var coordinator = new BoundedLineTranslationCoordinator(translator);

        coordinator.Reconcile(1, [Line("A"), Line("B")]);
        (await translator.WaitForInvocationAsync("A")).Complete("translated A");
        (await translator.WaitForInvocationAsync("B")).Complete("translated B");
        await WaitUntilAsync(() => coordinator.CurrentSnapshot.IsComplete);

        var replacement = coordinator.Reconcile(2, [Line(" A ", 100, 100), Line("C")]);

        Assert.Equal(2, replacement.Lines.Count);
        Assert.Equal(LinePresentationState.Success, replacement.Lines[0].State);
        Assert.Equal("translated A", replacement.Lines[0].TranslatedText);
        Assert.Equal(LinePresentationState.Pending, replacement.Lines[1].State);
        Assert.Equal("C", replacement.Lines[1].SourceLine.Text.Value);
        Assert.DoesNotContain(replacement.Lines, line => line.TranslatedText == "translated B");
        (await translator.WaitForInvocationAsync("C")).Complete("translated C");
    }

    [Fact]
    public async Task Changed_error_content_replaces_the_previous_successful_state()
    {
        var translator = new ManualTranslator(ignoreCancellation: true);
        await using var coordinator = new BoundedLineTranslationCoordinator(translator);

        coordinator.Reconcile(1, [Line("A")]);
        (await translator.WaitForInvocationAsync("A")).Complete("translated A");
        await WaitUntilAsync(() => coordinator.CurrentSnapshot.IsComplete);

        var replacement = coordinator.Reconcile(2, [Line("B")]);
        var invocation = await translator.WaitForInvocationAsync("B");
        invocation.Fail(new InvalidOperationException("provider failed"));
        await WaitUntilAsync(() => coordinator.CurrentSnapshot.IsComplete);

        var completed = coordinator.CurrentSnapshot;
        Assert.Single(completed.Lines);
        Assert.Equal("B", completed.Lines[0].SourceLine.Text.Value);
        Assert.Equal(LinePresentationState.Error, completed.Lines[0].State);
        Assert.Null(completed.Lines[0].TranslatedText);
        Assert.NotNull(completed.Lines[0].Error);
        Assert.Equal(replacement.Revision + 1, completed.Revision);
    }

    [Fact]
    public async Task Stop_restart_race_keeps_the_old_drain_off_the_new_coordinator()
    {
        var oldTranslator = new ManualTranslator(ignoreCancellation: true);
        await using var oldCoordinator = new BoundedLineTranslationCoordinator(oldTranslator);
        oldCoordinator.Reconcile(1, [Line("old")]);
        var oldInvocation = await oldTranslator.WaitForInvocationAsync("old");

        var firstStop = oldCoordinator.DisposeAsync().AsTask();
        var joinedStop = oldCoordinator.DisposeAsync().AsTask();
        Assert.Same(firstStop, joinedStop);
        Assert.False(firstStop.IsCompleted);

        var newTranslator = new ManualTranslator(ignoreCancellation: true);
        await using var newCoordinator = new BoundedLineTranslationCoordinator(newTranslator);
        newCoordinator.Reconcile(1, [Line("new")]);
        var newInvocation = await newTranslator.WaitForInvocationAsync("new");

        oldInvocation.Complete("old result");
        await firstStop;

        Assert.Equal(1, newCoordinator.ActiveProviderCallCount);
        newInvocation.Complete("new result");
        await WaitUntilAsync(() => newCoordinator.CurrentSnapshot.IsComplete);
        Assert.Equal("new result", newCoordinator.CurrentSnapshot.Lines[0].TranslatedText);
    }

    [Fact]
    public void Latest_value_handoff_coalesces_to_one_value()
    {
        var handoff = new LatestValueHandoff<int>();

        handoff.Publish(1);
        handoff.Publish(2);

        Assert.True(handoff.TryTake(out var value));
        Assert.Equal(2, value);
        Assert.False(handoff.HasValue);
        Assert.False(handoff.TryTake(out _));
    }

    [Fact]
    public async Task Presentation_snapshot_lines_are_not_mutable_through_the_public_list()
    {
        var translator = new ManualTranslator(ignoreCancellation: true);
        await using var coordinator = new BoundedLineTranslationCoordinator(translator);
        var snapshot = coordinator.Reconcile(1, [Line("immutable")]);

        var list = Assert.IsAssignableFrom<IList<LinePresentationLine>>(snapshot.Lines);
        Assert.Throws<NotSupportedException>(() => list.Clear());
        (await translator.WaitForInvocationAsync("immutable")).Complete("done");
    }

    private static LineTranslationRequest Line(string text, int left = 0, int top = 0)
    {
        var request = new TranslationRequest(text, "en", "fr", "provider-v1");
        return new LineTranslationRequest(
            new OcrText(text, new PhysicalPixelRect(left, top, 20, 10)),
            request);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 300; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("Timed out waiting for the coordinator state.");
    }

    private sealed class ManualTranslator : ITextTranslator
    {
        private readonly bool ignoreCancellation;
        private readonly ConcurrentDictionary<string, Invocation> invocations = new(StringComparer.Ordinal);
        private int activeCalls;
        private int maxActiveCalls;

        public ManualTranslator(bool ignoreCancellation)
        {
            this.ignoreCancellation = ignoreCancellation;
        }

        public int InvocationCount => invocations.Count;

        public int MaxActiveCalls => Volatile.Read(ref maxActiveCalls);

        public Task<TranslationResult> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken = default)
        {
            var key = request.MemoryKey.NormalizedText;
            var invocation = new Invocation(request);
            if (!invocations.TryAdd(key, invocation))
            {
                throw new InvalidOperationException($"Duplicate invocation for '{key}'.");
            }

            var active = Interlocked.Increment(ref activeCalls);
            UpdateMaximum(active);
            cancellationToken.Register(() => invocation.CancellationObserved.TrySetResult(true));
            return AwaitInvocationAsync(invocation, cancellationToken);
        }

        public async Task<Invocation> WaitForInvocationAsync(string text)
        {
            for (var attempt = 0; attempt < 300; attempt++)
            {
                if (invocations.TryGetValue(TextNormalization.Normalize(text), out var invocation))
                {
                    return invocation;
                }

                await Task.Delay(10);
            }

            throw new TimeoutException($"No invocation for '{text}'.");
        }

        private async Task<TranslationResult> AwaitInvocationAsync(
            Invocation invocation,
            CancellationToken cancellationToken)
        {
            try
            {
                return ignoreCancellation
                    ? await invocation.Result.Task
                    : await invocation.Result.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref activeCalls);
            }
        }

        private void UpdateMaximum(int active)
        {
            while (true)
            {
                var current = Volatile.Read(ref maxActiveCalls);
                if (active <= current || Interlocked.CompareExchange(ref maxActiveCalls, active, current) == current)
                {
                    return;
                }
            }
        }

        public sealed class Invocation
        {
            public Invocation(TranslationRequest request)
            {
                Request = request;
                Result = new TaskCompletionSource<TranslationResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                CancellationObserved = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public TranslationRequest Request { get; }

            public TaskCompletionSource<TranslationResult> Result { get; }

            public TaskCompletionSource<bool> CancellationObserved { get; }

            public void Complete(string translatedText)
            {
                Result.TrySetResult(new TranslationResult(Request, translatedText));
            }

            public void Fail(Exception exception)
            {
                Result.TrySetException(exception);
            }
        }
    }
}

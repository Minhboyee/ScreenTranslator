using System.Collections.Concurrent;
using Translator.Core;

namespace Translator.Core.Tests;

public sealed class CoreTests
{
    [Fact]
    public void Mailbox_replaces_pending_value_instead_of_queueing()
    {
        using var mailbox = new LatestValueMailbox<int>();

        Assert.True(mailbox.TryPublish(1, out var firstReplacement));
        Assert.Equal(0, firstReplacement);
        Assert.True(mailbox.TryPublish(2, out var secondReplacement));
        Assert.Equal(1, secondReplacement);
        Assert.True(mailbox.TryRead(out var value));
        Assert.Equal(2, value);
        Assert.False(mailbox.TryRead(out _));
    }

    [Fact]
    public void Cache_key_normalizes_text_and_language_but_partitions_revision()
    {
        var cache = new TranslationMemoryCache();
        var first = new TranslationRequest("  Hello\nworld ", "EN", "FR", "provider-v1");
        var equivalent = new TranslationRequest("Hello   world", "en", "fr", "provider-v1");
        var differentRevision = new TranslationRequest("hello world", "en", "fr", "provider-v2");

        Assert.Equal(first.MemoryKey, equivalent.MemoryKey);
        Assert.NotEqual(first.MemoryKey, differentRevision.MemoryKey);

        cache.Set(first.MemoryKey, "bonjour");

        Assert.True(cache.TryGet(equivalent.MemoryKey, out var translation));
        Assert.Equal("bonjour", translation);
        Assert.False(cache.TryGet(differentRevision.MemoryKey, out _));
    }

    [Fact]
    public async Task Session_suppresses_a_result_that_finishes_after_supersession()
    {
        var translator = new ManualTranslator(ignoreCancellation: true);
        await using var session = new TranslationSession(translator);
        var publications = new List<TranslationPublication>();
        session.ResultPublished += publications.Add;

        var firstTask = session.SubmitAsync(Request("first"));
        var first = await translator.WaitForInvocationAsync("first");
        var secondTask = session.SubmitAsync(Request("second"));
        var second = await translator.WaitForInvocationAsync("second");

        first.Complete("stale");
        Assert.Null(await firstTask);
        Assert.Empty(publications);

        second.Complete("current");
        var publication = await secondTask;

        Assert.NotNull(publication);
        Assert.Equal(2, publication!.Generation);
        Assert.Equal("current", publication.Result.TranslatedText);
        Assert.Single(publications);
        Assert.Equal(2, session.LastPublishedGeneration);
    }

    [Fact]
    public async Task Session_cancels_superseded_translation_work()
    {
        var translator = new ManualTranslator(ignoreCancellation: false);
        await using var session = new TranslationSession(translator);

        var firstTask = session.SubmitAsync(Request("first"));
        var first = await translator.WaitForInvocationAsync("first");
        var secondTask = session.SubmitAsync(Request("second"));

        await first.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(await firstTask);

        var second = await translator.WaitForInvocationAsync("second");
        second.Complete("current");
        Assert.Equal("current", (await secondTask)!.Result.TranslatedText);
    }

    [Fact]
    public async Task Session_deduplicates_unchanged_text_after_first_translation()
    {
        var translator = new CountingTranslator();
        await using var session = new TranslationSession(translator);
        var request = Request("same text");

        var first = await session.SubmitAsync(request);
        var second = await session.SubmitAsync(new TranslationRequest(
            " same   text ",
            "EN",
            "fr",
            "provider-v1"));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal("translated", first!.Result.TranslatedText);
        Assert.Equal("translated", second!.Result.TranslatedText);
        Assert.Equal(1, translator.CallCount);
    }

    private static TranslationRequest Request(string text)
    {
        return new TranslationRequest(text, "en", "fr", "provider-v1");
    }

    private sealed class CountingTranslator : ITextTranslator
    {
        public int CallCount { get; private set; }

        public Task<TranslationResult> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new TranslationResult(request, "translated"));
        }
    }

    private sealed class ManualTranslator : ITextTranslator
    {
        private readonly bool ignoreCancellation;
        private readonly ConcurrentDictionary<string, Invocation> invocations = new();

        public ManualTranslator(bool ignoreCancellation)
        {
            this.ignoreCancellation = ignoreCancellation;
        }

        public Task<TranslationResult> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken = default)
        {
            var invocation = new Invocation(request);
            if (!invocations.TryAdd(request.Text.Value.Trim(), invocation))
            {
                throw new InvalidOperationException("Duplicate invocation in test.");
            }

            return TranslateCoreAsync(invocation, cancellationToken);
        }

        public async Task<Invocation> WaitForInvocationAsync(string text)
        {
            Invocation? invocation = null;
            while (!invocations.TryGetValue(text, out invocation))
            {
                await Task.Delay(10);
            }

            return invocation!;
        }

        private async Task<TranslationResult> TranslateCoreAsync(
            Invocation invocation,
            CancellationToken cancellationToken)
        {
            if (ignoreCancellation)
            {
                return await invocation.Result.Task;
            }

            try
            {
                return await invocation.Result.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                invocation.CancellationObserved.TrySetResult(true);
                throw;
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
        }
    }
}

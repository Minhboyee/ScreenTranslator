using System.Collections.ObjectModel;
using System.Globalization;
using Translator.Core;

namespace Translator.Windows;

public sealed record OcrWordSnapshot
{
    public OcrWordSnapshot(string text, PhysicalPixelRect bounds)
    {
        ArgumentNullException.ThrowIfNull(text);
        Text = text;
        Bounds = bounds;
    }

    public string Text { get; }

    public PhysicalPixelRect Bounds { get; }
}

public sealed record OcrLineSnapshot
{
    public OcrLineSnapshot(IEnumerable<OcrWordSnapshot> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        Words = new ReadOnlyCollection<OcrWordSnapshot>(words.ToArray());
    }

    public IReadOnlyList<OcrWordSnapshot> Words { get; }
}

public static class OcrDocumentMapper
{
    public static OcrResult MapLines(IEnumerable<OcrLineSnapshot> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var mappedLines = new List<OcrText>();

        foreach (var line in lines)
        {
            ArgumentNullException.ThrowIfNull(line);
            var words = line.Words
                .Where(word => !string.IsNullOrWhiteSpace(word.Text))
                .ToArray();
            if (words.Length == 0)
            {
                continue;
            }

            var left = words.Min(word => word.Bounds.Left);
            var top = words.Min(word => word.Bounds.Top);
            var right = words.Max(word => word.Bounds.Right);
            var bottom = words.Max(word => word.Bounds.Bottom);
            var bounds = new PhysicalPixelRect(left, top, checked(right - left), checked(bottom - top));
            var text = string.Join(' ', words.Select(word => word.Text.Trim()));
            mappedLines.Add(new OcrText(text, bounds));
        }

        return new OcrResult(mappedLines);
    }
}

public sealed class OcrDocumentDeduplicator
{
    private readonly object gate = new();
    private string? lastPresentation;

    public bool ShouldPublish(OcrResult document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var presentation = NormalizePresentation(document);

        lock (gate)
        {
            if (string.Equals(lastPresentation, presentation, StringComparison.Ordinal))
            {
                return false;
            }

            lastPresentation = presentation;
            return true;
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            lastPresentation = null;
        }
    }

    public static string Normalize(OcrResult document)
    {
        ArgumentNullException.ThrowIfNull(document);
        // Content identity deliberately ignores bounds, appearance, and OCR
        // line order. Sorting retains duplicate lines while making order-only
        // churn equivalent.
        return string.Join(
            '\u001f',
            document.Text
                .Select(text => TextNormalization.Normalize(text.Text.Value))
                .OrderBy(text => text, StringComparer.Ordinal));
    }

    public static string NormalizePresentation(OcrResult document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return string.Join(
            '\u001f',
            document.Text
                .Select(text => string.Join(
                    '\u001e',
                    TextNormalization.Normalize(text.Text.Value),
                    text.Bounds.Left.ToString(CultureInfo.InvariantCulture),
                    text.Bounds.Top.ToString(CultureInfo.InvariantCulture),
                    text.Bounds.Width.ToString(CultureInfo.InvariantCulture),
                    text.Bounds.Height.ToString(CultureInfo.InvariantCulture),
                    text.AppearanceHint is { } appearance
                        ? appearance.RelativeBackgroundLuminance.ToString("R", CultureInfo.InvariantCulture)
                        : string.Empty))
                .OrderBy(value => value, StringComparer.Ordinal));
    }
}

public sealed class OcrDocumentStabilitySelector
{
    public static readonly TimeSpan ChangedContentSettleWindow = TimeSpan.FromMilliseconds(225);
    public static readonly TimeSpan EmptyGracePeriod = TimeSpan.FromMilliseconds(600);

    private string? publishedContentIdentity;
    private string? publishedPresentationIdentity;
    private string? pendingContentIdentity;
    private OcrResult? pendingDocument;
    private DateTimeOffset pendingSince;
    private int pendingMatches;
    private DateTimeOffset? emptySince;
    private bool hasPublishedClear;

    public OcrResult? Observe(OcrResult document, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.Text.Count == 0)
        {
            pendingContentIdentity = null;
            pendingDocument = null;
            pendingMatches = 0;

            if (hasPublishedClear)
            {
                return null;
            }

            emptySince ??= observedAt;
            if (observedAt - emptySince.Value < EmptyGracePeriod)
            {
                return null;
            }

            publishedContentIdentity = null;
            publishedPresentationIdentity = null;
            hasPublishedClear = true;
            emptySince = null;
            return document;
        }

        emptySince = null;
        var contentIdentity = OcrDocumentDeduplicator.Normalize(document);
        var presentationIdentity = OcrDocumentDeduplicator.NormalizePresentation(document);
        if (hasPublishedClear || publishedContentIdentity is null)
        {
            publishedContentIdentity = contentIdentity;
            publishedPresentationIdentity = presentationIdentity;
            hasPublishedClear = false;
            ClearPending();
            return document;
        }

        if (string.Equals(publishedContentIdentity, contentIdentity, StringComparison.Ordinal))
        {
            // This also cancels an unconfirmed B in an A-B-A sequence.
            ClearPending();
            if (string.Equals(publishedPresentationIdentity, presentationIdentity, StringComparison.Ordinal))
            {
                return null;
            }

            publishedPresentationIdentity = presentationIdentity;
            return document;
        }

        if (pendingContentIdentity is null)
        {
            pendingContentIdentity = contentIdentity;
            pendingDocument = document;
            pendingSince = observedAt;
            pendingMatches = 1;
        }
        else if (string.Equals(pendingContentIdentity, contentIdentity, StringComparison.Ordinal))
        {
            pendingMatches++;
            pendingDocument = document;
        }
        else
        {
            pendingContentIdentity = contentIdentity;
            pendingDocument = document;
            pendingMatches = 1;
        }

        if (pendingMatches < 2 && observedAt - pendingSince < ChangedContentSettleWindow)
        {
            return null;
        }

        publishedContentIdentity = contentIdentity;
        publishedPresentationIdentity = OcrDocumentDeduplicator.NormalizePresentation(
            pendingDocument ?? document);
        var published = pendingDocument ?? document;
        ClearPending();
        return published;
    }

    public void Reset()
    {
        publishedContentIdentity = null;
        publishedPresentationIdentity = null;
        hasPublishedClear = false;
        emptySince = null;
        ClearPending();
    }

    private void ClearPending()
    {
        pendingContentIdentity = null;
        pendingDocument = null;
        pendingMatches = 0;
    }
}

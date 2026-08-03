using Microsoft.Graphics.Canvas.Text;
using Windows.Storage;

namespace Translator_App_WinUI;

public sealed record ImportedFontFamily(string FamilyName, string FontFamily);

public sealed record ImportedFontInfo(string FileName, string LocalPath, string DisplayFamily, bool IsFallback)
{
    public const string FallbackFontFamily = "Segoe UI Variable";

    public IReadOnlyList<ImportedFontFamily> Families { get; init; } = Array.Empty<ImportedFontFamily>();

    public IReadOnlyList<string> FamilyNames => Families.Select(family => family.FamilyName).ToArray();

    public IReadOnlyList<string> FontFamilies => Families.Select(family => family.FontFamily).ToArray();

    public string FontFamily => Families.FirstOrDefault()?.FontFamily ?? FallbackFontFamily;
}

public sealed class ImportedFontService
{
    public async Task<ImportedFontInfo?> ImportAsync(StorageFile file, CancellationToken cancellationToken = default)
    {
        if (!IsSupportedFont(file.Name))
            return null;

        cancellationToken.ThrowIfCancellationRequested();
        var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync("Fonts", CreationCollisionOption.OpenIfExists);
        var destination = Path.Combine(folder.Path, file.Name);
        StorageFile? copiedFile = null;

        try
        {
            copiedFile = await file.CopyAsync(folder, file.Name, NameCollisionOption.ReplaceExisting);
            cancellationToken.ThrowIfCancellationRequested();

            var fontUri = new Uri($"ms-appdata:///local/Fonts/{Uri.EscapeDataString(file.Name)}");
            using var fontSet = new CanvasFontSet(fontUri);
            var families = DiscoverFamilies(fontSet, fontUri);
            if (families.Count == 0)
                throw new InvalidDataException("The font contains no usable family names.");

            return new ImportedFontInfo(file.Name, destination, families[0].FamilyName, false)
            {
                Families = families
            };
        }
        catch (OperationCanceledException)
        {
            if (copiedFile is not null)
                await DeleteQuietlyAsync(copiedFile);
            throw;
        }
        catch (Exception)
        {
            if (copiedFile is not null)
                await DeleteQuietlyAsync(copiedFile);
            return null;
        }
    }

    private static bool IsSupportedFont(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return string.Equals(extension, ".ttf", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".otf", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<ImportedFontFamily> DiscoverFamilies(CanvasFontSet fontSet, Uri fontUri)
    {
        if (fontSet.Fonts.Count == 0)
            return Array.Empty<ImportedFontFamily>();

        var families = new List<ImportedFontFamily>();
        var familyNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var face in fontSet.Fonts)
        {
            var faceFamilyNames = face.FamilyNames.Values
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (faceFamilyNames.Length == 0)
                return Array.Empty<ImportedFontFamily>();

            foreach (var familyName in faceFamilyNames)
            {
                if (!familyNames.Add(familyName))
                    continue;

                families.Add(new ImportedFontFamily(familyName, $"{fontUri.AbsoluteUri}#{familyName}"));
            }
        }

        return families;
    }

    private static async Task DeleteQuietlyAsync(StorageFile file)
    {
        try
        {
            await file.DeleteAsync();
        }
        catch
        {
            // A failed import must not hide the original validation result.
        }
    }
}

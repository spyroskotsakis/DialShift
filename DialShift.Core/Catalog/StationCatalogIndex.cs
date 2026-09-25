using System.Collections.ObjectModel;

namespace DialShift.Core.Catalog;

/// <summary>The loaded catalog with its search keys folded once (D70). Immutable and thread-safe.</summary>
/// <remarks>Matching each search with culture-aware <c>CompareInfo.IndexOf(IgnoreCase | IgnoreNonSpace)</c> measured up
/// to 11.2 ms on the real catalog; matching these precomputed folded keys ordinally takes well under a millisecond
/// (docs/catalog-contracts.md §1). The index is built once, inside the load.</remarks>
public sealed class StationCatalogIndex
{
    private readonly StationCatalogEntry[] items;
    private readonly SearchKeys[] keys;

    /// <summary>No stations; the catalog of an unavailable load.</summary>
    public static StationCatalogIndex Empty { get; } = new([]);

    /// <summary>Copies <paramref name="entries"/> (order kept) and folds Name, NameLocal and City, extracts the
    /// FrequencyFm digits and computes the FrequencyFm band (<see cref="StationCatalogQuery.BandOf"/>) of every entry.
    /// O(n); throws ArgumentNullException for a null list or a null entry, and never throws on text content (§3.3, D77, D79).</summary>
    public StationCatalogIndex(IReadOnlyList<StationCatalogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        items = new StationCatalogEntry[entries.Count];
        keys = new SearchKeys[items.Length];
        for (var i = 0; i < items.Length; i++)
        {
            var entry = entries[i] ?? throw new ArgumentNullException(nameof(entries), $"Catalog entry {i} is null.");
            items[i] = entry;
            keys[i] = new SearchKeys(
                StationCatalogQuery.Fold(entry.Name),
                StationCatalogQuery.Fold(entry.NameLocal),
                StationCatalogQuery.Fold(entry.City),
                AsciiDigits(entry.FrequencyFm),
                StationCatalogQuery.BandOf(entry.FrequencyFm));
        }
        Entries = Array.AsReadOnly(items);
    }

    public IReadOnlyList<StationCatalogEntry> Entries { get; }

    /// <summary>The entries, in <see cref="Entries"/> order, for the query's scan.</summary>
    internal ReadOnlySpan<StationCatalogEntry> Items => items;

    /// <summary>The search keys of <see cref="Items"/>, position for position.</summary>
    internal ReadOnlySpan<SearchKeys> Keys => keys;

    /// <summary>The ASCII digits of <paramref name="frequency"/> in order (<c>"101.5"</c> → <c>"1015"</c>); the input
    /// itself when it has nothing else, so an empty or all-digit frequency allocates nothing.</summary>
    private static string AsciiDigits(string frequency)
    {
        var count = 0;
        foreach (var c in frequency)
            if (char.IsAsciiDigit(c)) count++;
        if (count == frequency.Length) return frequency;
        return count == 0 ? "" : string.Create(count, frequency, static (digits, source) =>
        {
            var at = 0;
            foreach (var c in source)
                if (char.IsAsciiDigit(c)) digits[at++] = c;
        });
    }

    /// <summary>One entry's precomputed search keys: the folded Name, NameLocal and City (§3.3 <c>Fold</c>), the
    /// FrequencyFm digits ("" when it has none, which never matches a frequency query) and the FrequencyFm band
    /// (<see cref="StationCatalogQuery.BandOf"/>, D79).</summary>
    internal readonly record struct SearchKeys(string Name, string NameLocal, string City, string FrequencyDigits, FrequencyBand Band);
}

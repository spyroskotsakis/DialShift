using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace DialShift.Core.Catalog;

/// <summary>Filter values; null means "All". Values compare ordinally with the entry's field (Country is the code).</summary>
public sealed record CatalogFilters(
    string? Country = null, string? City = null, string? Type = null, string? Genre = null, string? Language = null)
{
    public static CatalogFilters None { get; } = new();
}

/// <summary>The first <c>cap</c> matches in rank order, and how many entries matched in total.</summary>
public sealed record CatalogSearchResult(IReadOnlyList<StationCatalogEntry> Items, int TotalCount);

/// <summary>The fields that have a filter.</summary>
public enum CatalogField { Country, City, Type, Genre, Language }

/// <summary>One distinct filter value. Label is what the user sees: the country label for Country, else Value.</summary>
public sealed record CatalogFilterValue(string Value, string Label);

/// <summary>Pure filter-and-rank engine over a <see cref="StationCatalogIndex"/> (§3.3). No I/O.</summary>
/// <remarks>
/// <para><b>Matching (D70).</b> Text matches ordinally on keys folded by <see cref="Fold"/>, which ignores case,
/// diacritics and compatibility forms without consulting any culture. Only Name, NameLocal, City and the FrequencyFm
/// digits are searched; genre, notes and tags are not. Filters compare ordinally and AND with each other and the text.</para>
/// <para><b>Ranking.</b> Name prefix, then name substring, then NameLocal or City, then frequency; within a tier by votes
/// descending (null as 0), folded name, name, country, stream URL and catalog position, all ordinal, so the order is the
/// same on every OS and culture.</para>
/// <para><b>Cost.</b> One O(n) scan over the precomputed keys with a bounded top-<c>cap</c> selection: a search allocates
/// its query key, the selection and the result, never anything per entry.</para>
/// </remarks>
public static partial class StationCatalogQuery
{
    public const int DefaultCap = 50;

    /// <summary>Folds up to this many characters in a stack buffer; longer text rents one from the shared pool.</summary>
    private const int StackFoldLimit = 256;

    /// <summary>Returned by <see cref="Tier"/> for an entry that does not match the text.</summary>
    private const int NoMatch = -1;

    /// <summary>
    /// The entries of <paramref name="catalog"/> that pass <paramref name="filters"/> and match <paramref name="text"/>,
    /// ranked per docs/catalog-contracts.md §3.3: the first <paramref name="cap"/> of them, and how many matched in total.
    /// Null, empty or whitespace text matches every filtered entry, so the empty query lists the most-voted stations.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="catalog"/> or <paramref name="filters"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cap"/> is less than 1.</exception>
    public static CatalogSearchResult Search(StationCatalogIndex catalog, string? text, CatalogFilters filters, int cap = DefaultCap)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentOutOfRangeException.ThrowIfLessThan(cap, 1);

        var query = Fold(text ?? "");
        var frequency = FrequencyQueryDigits(text);
        var entries = catalog.Items;
        var keys = catalog.Keys;
        var capacity = Math.Min(cap, entries.Length);
        // Worst kept match first, so each better match replaces it in O(log cap).
        var kept = new PriorityQueue<int, Ranked>(capacity, new WorstFirst(catalog));
        var total = 0;
        for (var position = 0; position < entries.Length; position++)
        {
            var entry = entries[position];
            if (!Passes(filters, entry)) continue;
            var tier = Tier(keys[position], query, frequency);
            if (tier == NoMatch) continue;
            total++;
            var candidate = new Ranked(tier, entry.Votes ?? 0, position);
            if (kept.Count < capacity) kept.Enqueue(position, candidate);
            else if (kept.TryPeek(out _, out var worst) && Order(catalog, candidate, worst) < 0) kept.DequeueEnqueue(position, candidate);
        }

        var items = new StationCatalogEntry[kept.Count];
        for (var at = items.Length - 1; kept.TryDequeue(out var index, out _); at--) items[at] = entries[index];
        return new CatalogSearchResult(items, total);
    }

    /// <summary>
    /// The distinct non-empty values of <paramref name="field"/> across <paramref name="entries"/> (ordinal), for a flat
    /// filter list (D72). Label is the value, except for <see cref="CatalogField.Country"/>: the first non-empty
    /// CountryLabel of an entry with that code (list order), else the code. Ordered by the folded label, then the label,
    /// then the value, all ordinal.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> or one of its entries is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="field"/> is not a defined <see cref="CatalogField"/>.</exception>
    public static IReadOnlyList<CatalogFilterValue> AvailableValues(IReadOnlyList<StationCatalogEntry> entries, CatalogField field)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Func<StationCatalogEntry, string> select = field switch
        {
            CatalogField.Country => static e => e.Country,
            CatalogField.City => static e => e.City,
            CatalogField.Type => static e => e.Type,
            CatalogField.Genre => static e => e.Genre,
            CatalogField.Language => static e => e.Language,
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Not a catalog filter field."),
        };

        // Value → label, in first-seen order. A country's label stays empty until an entry with that code has one.
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i] ?? throw new ArgumentNullException(nameof(entries), $"Catalog entry {i} is null.");
            var value = select(entry);
            if (value.Length == 0) continue;
            ref var label = ref CollectionsMarshal.GetValueRefOrAddDefault(labels, value, out _);
            if (string.IsNullOrEmpty(label)) label = field == CatalogField.Country ? entry.CountryLabel : value;
        }

        var sorted = new (string Key, CatalogFilterValue Value)[labels.Count];
        var at = 0;
        foreach (var (value, label) in labels)
        {
            var shown = label.Length > 0 ? label : value;
            sorted[at++] = (Fold(shown), new CatalogFilterValue(value, shown));
        }
        Array.Sort(sorted, static (x, y) =>
        {
            var order = string.CompareOrdinal(x.Key, y.Key);
            if (order == 0) order = string.CompareOrdinal(x.Value.Label, y.Value.Label);
            return order != 0 ? order : string.CompareOrdinal(x.Value.Value, y.Value.Value);
        });
        return Array.ConvertAll(sorted, static s => s.Value);
    }

    /// <summary>The §3.3 normalization. Internal: used by the index and the query; the tests see it through InternalsVisibleTo.</summary>
    /// <remarks>
    /// Compatibility decomposition (FormKD), then non-spacing and enclosing marks removed, then invariant lower case with
    /// <c>ς→σ ß→ss æ→ae œ→oe ø→o ł→l đ→d ı→i</c>, then each whitespace run collapsed to one space and both ends trimmed.
    /// So <c>"München"</c> → <c>"munchen"</c>, <c>"ΑΘΗΝΑΣ"</c> and <c>"αθήνας"</c> → <c>"αθηνασ"</c>, <c>"ﬁp"</c> →
    /// <c>"fip"</c>. Returns <paramref name="value"/> itself when it is already folded.
    /// <para>Never throws (D77): FormKD rejects an unpaired surrogate, so each one becomes U+FFFD first; valid pairs are
    /// kept, and text without surrogates is normalized as is.</para>
    /// </remarks>
    internal static string Fold(string value)
    {
        if (value.Length == 0) return value;
        var decomposed = ReplaceUnpairedSurrogates(value).Normalize(NormalizationForm.FormKD);
        // ß, æ and œ become two characters each, so the folded text is at most twice as long as the decomposed text.
        var capacity = decomposed.Length * 2;
        char[]? rented = null;
        Span<char> buffer = capacity <= StackFoldLimit ? stackalloc char[StackFoldLimit] : (rented = ArrayPool<char>.Shared.Rent(capacity));
        try
        {
            var length = 0;
            var pendingSpace = false;
            foreach (var c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark) continue;
                if (char.IsWhiteSpace(c))
                {
                    // One space for the whole run, written only when more text follows: this collapses and trims.
                    pendingSpace = length > 0;
                    continue;
                }
                if (pendingSpace)
                {
                    buffer[length++] = ' ';
                    pendingSpace = false;
                }
                switch (char.ToLowerInvariant(c))
                {
                    case 'ς': buffer[length++] = 'σ'; break;
                    case 'ß': buffer[length++] = 's'; buffer[length++] = 's'; break;
                    case 'æ': buffer[length++] = 'a'; buffer[length++] = 'e'; break;
                    case 'œ': buffer[length++] = 'o'; buffer[length++] = 'e'; break;
                    case 'ø': buffer[length++] = 'o'; break;
                    case 'ł': buffer[length++] = 'l'; break;
                    case 'đ': buffer[length++] = 'd'; break;
                    case 'ı': buffer[length++] = 'i'; break;
                    case var lower: buffer[length++] = lower; break;
                }
            }
            var folded = buffer[..length];
            return folded.SequenceEqual(value) ? value : new string(folded);
        }
        finally
        {
            if (rented is not null) ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <summary><paramref name="value"/> with each unpaired high or low surrogate replaced by U+FFFD (D77), or
    /// <paramref name="value"/> itself when it has none, which is the common case and allocates nothing.</summary>
    private static string ReplaceUnpairedSurrogates(string value)
    {
        var first = FirstUnpairedSurrogate(value);
        if (first < 0) return value;
        return string.Create(value.Length, (value, first), static (text, state) =>
        {
            state.value.AsSpan().CopyTo(text);
            for (var i = state.first; i < text.Length; i++)
            {
                if (!char.IsSurrogate(text[i])) continue;
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
                else text[i] = '\uFFFD';
            }
        });
    }

    /// <summary>The index of the first unpaired surrogate in <paramref name="text"/>, or -1. A vectorized search finds the
    /// first surrogate, so text without one costs one fast pass and no allocation.</summary>
    /// <remarks>The search runs on the text as <c>ushort</c>: the <c>char</c> overload of <c>IndexOfAnyInRange</c> measured
    /// 96 bytes allocated per call on .NET 10, which would add an allocation to every fold.</remarks>
    private static int FirstUnpairedSurrogate(ReadOnlySpan<char> text)
    {
        var start = MemoryMarshal.Cast<char, ushort>(text).IndexOfAnyInRange((ushort)0xD800, (ushort)0xDFFF);
        if (start < 0) return -1;
        for (var i = start; i < text.Length; i++)
        {
            if (!char.IsSurrogate(text[i])) continue;
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
            else return i;
        }
        return -1;
    }

    /// <summary>
    /// The digits a frequency query matches against the start of an entry's frequency digits, or null when
    /// <paramref name="text"/> is not a frequency query: 2–4 digits, an optional <c>.</c>/<c>,</c> with up to 2
    /// decimals, and an optional <c>fm</c>/<c>mhz</c>/<c>khz</c> (<c>"101,5 FM"</c> → <c>"1015"</c>).
    /// </summary>
    private static string? FrequencyQueryDigits(string? text)
    {
        if (text is null) return null;
        var match = FrequencyQuery().Match(text.Trim());
        return match.Success ? string.Concat(match.Groups[1].ValueSpan, match.Groups[2].ValueSpan) : null;
    }

    [GeneratedRegex(@"^([0-9]{2,4})(?:[.,]([0-9]{0,2}))?(?:\s*(?:fm|mhz|khz))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FrequencyQuery();

    private static bool Passes(CatalogFilters filters, StationCatalogEntry entry) =>
        (filters.Country is null || string.Equals(filters.Country, entry.Country, StringComparison.Ordinal)) &&
        (filters.City is null || string.Equals(filters.City, entry.City, StringComparison.Ordinal)) &&
        (filters.Type is null || string.Equals(filters.Type, entry.Type, StringComparison.Ordinal)) &&
        (filters.Genre is null || string.Equals(filters.Genre, entry.Genre, StringComparison.Ordinal)) &&
        (filters.Language is null || string.Equals(filters.Language, entry.Language, StringComparison.Ordinal));

    /// <summary>The lowest §3.3 tier the entry satisfies, or <see cref="NoMatch"/>. An empty query puts every entry in tier 0.</summary>
    /// <param name="frequency">The query's frequency digits, never empty; null when the text is not a frequency query.
    /// An entry without frequency digits therefore never matches it.</param>
    private static int Tier(in StationCatalogIndex.SearchKeys keys, string query, string? frequency)
    {
        if (query.Length == 0) return 0;
        var at = keys.Name.IndexOf(query, StringComparison.Ordinal);
        if (at >= 0) return at == 0 ? 0 : 1;
        if (keys.NameLocal.Contains(query, StringComparison.Ordinal) || keys.City.Contains(query, StringComparison.Ordinal)) return 2;
        return frequency is not null && keys.FrequencyDigits.StartsWith(frequency, StringComparison.Ordinal) ? 3 : NoMatch;
    }

    /// <summary>The §3.3 total order: negative when <paramref name="x"/> ranks before <paramref name="y"/>. Never 0 for
    /// two different positions, so the result does not depend on the scan or the selection.</summary>
    private static int Order(StationCatalogIndex catalog, Ranked x, Ranked y)
    {
        var order = x.Tier.CompareTo(y.Tier);
        if (order != 0) return order;
        order = y.Votes.CompareTo(x.Votes);
        if (order != 0) return order;
        order = string.CompareOrdinal(catalog.Keys[x.Position].Name, catalog.Keys[y.Position].Name);
        if (order != 0) return order;
        StationCatalogEntry a = catalog.Items[x.Position], b = catalog.Items[y.Position];
        order = string.CompareOrdinal(a.Name, b.Name);
        if (order != 0) return order;
        order = string.CompareOrdinal(a.Country, b.Country);
        if (order != 0) return order;
        order = string.CompareOrdinal(a.StreamUrl, b.StreamUrl);
        return order != 0 ? order : x.Position.CompareTo(y.Position);
    }

    /// <summary>A match's cheap sort keys; <see cref="Position"/> is its index in <see cref="StationCatalogIndex.Entries"/>.</summary>
    private readonly record struct Ranked(int Tier, int Votes, int Position);

    /// <summary>Reverses <see cref="Order"/>, so the selection queue dequeues the worst kept match first.</summary>
    private sealed class WorstFirst(StationCatalogIndex catalog) : IComparer<Ranked>
    {
        public int Compare(Ranked x, Ranked y) => Order(catalog, y, x);
    }
}

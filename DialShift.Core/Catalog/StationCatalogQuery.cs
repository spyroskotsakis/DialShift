using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace DialShift.Core.Catalog;

/// <summary>Filter values; null means "All". Values compare ordinally with the entry's field (Country is the code);
/// Language matches when it equals any one of the entry's language names (§3.3, D84).</summary>
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

/// <summary>How a FrequencyFm value reads (§3.3, §5.4, D79): FM MHz, AM kHz, or neither.</summary>
public enum FrequencyBand { None, Fm, Kilohertz }

/// <summary>Pure filter-and-rank engine over a <see cref="StationCatalogIndex"/> (§3.3). No I/O.</summary>
/// <remarks>
/// <para><b>Matching (D70).</b> Text matches ordinally on keys folded by <see cref="Fold"/>, which ignores case,
/// diacritics and compatibility forms without consulting any culture. Only Name, NameLocal, City and the FrequencyFm
/// digits are searched; genre, language, notes and tags are not. Filters compare ordinally and AND with each other and
/// the text. Language is multi-valued (D84): its filter matches an entry when it equals any one of the entry's
/// <see cref="LanguageNames"/>, which the index splits once, so for a single name it is the equality of the other fields.</para>
/// <para><b>Frequency (D79).</b> A query such as <c>101.5</c>, <c>FM 101,50</c>, <c>1017 kHz</c> or bare <c>1017</c>
/// matches entries whose FrequencyFm digits start with the query's digits, restricted to the band the query implies
/// (<see cref="BandOf"/>): FM for a decimal separator, <c>fm</c> or <c>mhz</c>; kHz for <c>am</c> or <c>khz</c>; any band
/// for bare digits.</para>
/// <para><b>Ranking.</b> Name prefix, then name substring, then NameLocal or City, then frequency; within a tier by votes
/// descending (null as 0), folded name, name, country, stream URL and catalog position, all ordinal, so the order is the
/// same on every OS and culture.</para>
/// <para><b>Cost.</b> One O(n) scan over the precomputed keys with a bounded top-<c>cap</c> selection, and nothing allocated
/// per entry. A search allocates its query key (none when the text is already folded), the trimmed text when it has
/// leading or trailing white space, the selection and the result; text that parses as a frequency also allocates the
/// regex match with its groups and the frequency digits, about 1 KB together on .NET 10. None of it grows with the
/// catalog.</para>
/// </remarks>
public static partial class StationCatalogQuery
{
    public const int DefaultCap = 50;

    /// <summary>The separator of an entry's language names (§2.1, D84). Internal: the index splits on it, and the tests
    /// see it through InternalsVisibleTo.</summary>
    internal const string LanguageSeparator = ", ";

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
        var frequency = ParseFrequencyQuery(text);
        var entries = catalog.Items;
        var keys = catalog.Keys;
        var capacity = Math.Min(cap, entries.Length);
        // Worst kept match first, so each better match replaces it in O(log cap).
        var kept = new PriorityQueue<int, Ranked>(capacity, new WorstFirst(catalog));
        var total = 0;
        for (var position = 0; position < entries.Length; position++)
        {
            var entry = entries[position];
            ref readonly var entryKeys = ref keys[position];
            if (!Passes(filters, entry, entryKeys)) continue;
            var tier = Tier(entryKeys, query, frequency);
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
    /// filter list (D72). For <see cref="CatalogField.Language"/> the values are the individual language names of the
    /// entries (<see cref="LanguageNames"/>, D84), never a joined combination, so each is a value the Language filter
    /// matches. Label is the value, except for <see cref="CatalogField.Country"/>: the first non-empty CountryLabel of an
    /// entry with that code (list order), else the code. Ordered by the folded label, then the label, then the value, all
    /// ordinal. It takes the entry list, not the index, so it splits each Language itself; it runs once per load, off the
    /// search path.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> or one of its entries is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="field"/> is not a defined <see cref="CatalogField"/>.</exception>
    public static IReadOnlyList<CatalogFilterValue> AvailableValues(IReadOnlyList<StationCatalogEntry> entries, CatalogField field)
    {
        ArgumentNullException.ThrowIfNull(entries);
        // Null for Language, which is multi-valued and read through LanguageNames below (D84).
        Func<StationCatalogEntry, string>? select = field switch
        {
            CatalogField.Country => static e => e.Country,
            CatalogField.City => static e => e.City,
            CatalogField.Type => static e => e.Type,
            CatalogField.Genre => static e => e.Genre,
            CatalogField.Language => null,
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Not a catalog filter field."),
        };

        // Value → label, in first-seen order. A country's label stays empty until an entry with that code has one.
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i] ?? throw new ArgumentNullException(nameof(entries), $"Catalog entry {i} is null.");
            if (select is null)
            {
                foreach (var name in LanguageNames(entry.Language)) Add(labels, name, name);
            }
            else
            {
                var value = select(entry);
                Add(labels, value, field == CatalogField.Country ? entry.CountryLabel : value);
            }
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

        // Records a non-empty value; its label is the first non-empty one offered (a country's may come later).
        static void Add(Dictionary<string, string> labels, string value, string label)
        {
            if (value.Length == 0) return;
            ref var kept = ref CollectionsMarshal.GetValueRefOrAddDefault(labels, value, out _);
            if (string.IsNullOrEmpty(kept)) kept = label;
        }
    }

    /// <summary>The language names of an entry's Language (§3.3, D84): <paramref name="language"/> split on
    /// <see cref="LanguageSeparator"/>, ordinal, with <see cref="StringSplitOptions.None"/>, not trimmed and not folded.
    /// <c>"English, German"</c> → <c>["English", "German"]</c>; <c>"German,English"</c> → <c>["German,English"]</c>;
    /// <c>""</c> → <c>[""]</c>, so the filter <c>""</c> matches an entry without a language. Core maps, corrects and drops
    /// nothing: the pipeline normalizes the names (contracts §2.6).</summary>
    internal static string[] LanguageNames(string language) => language.Split(LanguageSeparator, StringSplitOptions.None);

    /// <summary>
    /// The §3.3 band of a FrequencyFm value, the one classification behind both the frequency query and the §5.4 label:
    /// <see cref="FrequencyBand.Fm"/> for an invariant decimal from 64 to 108 (<c>"101.5"</c>, <c>"87"</c>),
    /// <see cref="FrequencyBand.Kilohertz"/> for an integer of at least 150 (<c>"1593"</c>, <c>"8500"</c>), otherwise
    /// <see cref="FrequencyBand.None"/> (<c>""</c>, <c>"Shortwave"</c>, <c>"108.5"</c>, <c>"149"</c>, <c>"1593.0"</c>, and any
    /// value with a sign, white space or a thousands separator). The same on every culture.
    /// <para>This follows §3.3 step 3 literally, so it inherits <c>decimal.TryParse</c>'s acceptance of trailing NUL
    /// characters: <c>"101.5\0"</c> is <see cref="FrequencyBand.Fm"/>.</para>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="frequencyFm"/> is null. Never throws on content (D79).</exception>
    public static FrequencyBand BandOf(string frequencyFm)
    {
        ArgumentNullException.ThrowIfNull(frequencyFm);
        if (!decimal.TryParse(frequencyFm, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) return FrequencyBand.None;
        if (value is >= 64 and <= 108) return FrequencyBand.Fm;
        return value >= 150 && !frequencyFm.Contains('.') ? FrequencyBand.Kilohertz : FrequencyBand.None;
    }

    /// <summary>The §3.3 normalization. Internal: used by the index and the query; the tests see it through InternalsVisibleTo.</summary>
    /// <remarks>
    /// Compatibility decomposition (FormKD), then non-spacing and enclosing marks removed, then invariant lower case with
    /// <c>ς→σ ß→ss æ→ae œ→oe ø→o ł→l đ→d ı→i</c>, then each whitespace run collapsed to one space and both ends trimmed.
    /// So <c>"München"</c> → <c>"munchen"</c>, <c>"ΑΘΗΝΑΣ"</c> and <c>"αθήνας"</c> → <c>"αθηνασ"</c>, <c>"ﬁp"</c> →
    /// <c>"fip"</c>. Returns <paramref name="value"/> itself when it is already folded.
    /// <para>Never throws (D77): FormKD rejects an unpaired surrogate and U+FFFE, so each of them becomes U+FFFD first;
    /// valid pairs are kept, and text without either is normalized as is.</para>
    /// </remarks>
    internal static string Fold(string value)
    {
        if (value.Length == 0) return value;
        var decomposed = ReplaceInvalidCodeUnits(value).Normalize(NormalizationForm.FormKD);
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

    /// <summary><paramref name="value"/> with each code unit FormKD rejects replaced by U+FFFD (D77): every unpaired high
    /// or low surrogate and every U+FFFE. Returns <paramref name="value"/> itself when it has none, which is the common
    /// case and allocates nothing.</summary>
    private static string ReplaceInvalidCodeUnits(string value)
    {
        var first = FirstInvalidCodeUnit(value);
        if (first < 0) return value;
        return string.Create(value.Length, (value, first), static (text, state) =>
        {
            state.value.AsSpan().CopyTo(text);
            for (var i = state.first; i < text.Length; i++)
            {
                if (text[i] == '\uFFFE')
                {
                    text[i] = '\uFFFD';
                    continue;
                }
                if (!char.IsSurrogate(text[i])) continue;
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
                else text[i] = '\uFFFD';
            }
        });
    }

    /// <summary>The index of the first code unit FormKD rejects in <paramref name="text"/> (an unpaired surrogate or
    /// U+FFFE), or -1. Vectorized searches find the first surrogate and the first U+FFFE, so text without either costs two
    /// fast passes and no allocation.</summary>
    /// <remarks>U+FFFE is the only code point other than a surrogate that <c>Normalize(FormKD)</c> rejects: every one of
    /// U+0000\u2013U+10FFFF was probed on .NET 10 (ICU). The surrogate search runs on the text as <c>ushort</c>: the <c>char</c>
    /// overload of <c>IndexOfAnyInRange</c> measured 96 bytes allocated per call on .NET 10, which would add an allocation
    /// to every fold.</remarks>
    private static int FirstInvalidCodeUnit(ReadOnlySpan<char> text)
    {
        var nonCharacter = text.IndexOf('\uFFFE');
        var start = MemoryMarshal.Cast<char, ushort>(text).IndexOfAnyInRange((ushort)0xD800, (ushort)0xDFFF);
        if (start < 0) return nonCharacter;
        // Only an unpaired surrogate before the first U+FFFE can come first, so the pair check stops there.
        var end = nonCharacter < 0 ? text.Length : nonCharacter;
        for (var i = start; i < end; i++)
        {
            if (!char.IsSurrogate(text[i])) continue;
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
            else return i;
        }
        return nonCharacter;
    }

    /// <summary>
    /// The §3.3 frequency query of <paramref name="text"/> (D79), or null when it is not one: the digits that must start
    /// an entry's frequency digits and the band the entry must have, where <see cref="FrequencyBand.None"/> means any band.
    /// <c>"FM 101,50"</c> → (<c>"1015"</c>, Fm); <c>"1017 kHz"</c> → (<c>"1017"</c>, Kilohertz); <c>"1017"</c> →
    /// (<c>"1017"</c>, any); <c>"AM 101.7"</c> → null, because it implies both bands.
    /// </summary>
    private static FrequencyQuery? ParseFrequencyQuery(string? text)
    {
        if (text is null) return null;
        var match = FrequencyQueryPattern().Match(text.Trim());
        if (!match.Success) return null;
        FrequencyBand leading = TokenBand(match.Groups["L"]), trailing = TokenBand(match.Groups["T"]);
        var fm = match.Groups["S"].Success || leading == FrequencyBand.Fm || trailing == FrequencyBand.Fm;
        var kilohertz = leading == FrequencyBand.Kilohertz || trailing == FrequencyBand.Kilohertz;
        if (fm && kilohertz) return null;
        var decimals = match.Groups["R"].ValueSpan;
        // The catalog writes FM with one decimal, so a second decimal 0 adds nothing: 101.50 → 1015, while 101.0 stays 1010.
        if (decimals is [_, '0']) decimals = decimals[..1];
        var band = fm ? FrequencyBand.Fm : kilohertz ? FrequencyBand.Kilohertz : FrequencyBand.None;
        return new FrequencyQuery(string.Concat(match.Groups["I"].ValueSpan, decimals), band);
    }

    /// <summary>The band a matched <c>fm</c>/<c>mhz</c>/<c>am</c>/<c>khz</c> token implies, or None when the group did not
    /// match. Read from the first character, so every spelling the case-insensitive pattern accepts (including the Kelvin
    /// sign it equates with <c>k</c>) is classified without an allocation.</summary>
    private static FrequencyBand TokenBand(Group token) =>
        !token.Success ? FrequencyBand.None
        : token.ValueSpan[0] is 'f' or 'F' or 'm' or 'M' ? FrequencyBand.Fm
        : FrequencyBand.Kilohertz;

    /// <summary>§3.3: an optional leading band word L, the integer digits I, an optional separator S with decimals R, and
    /// an optional trailing band word or unit T. ASCII digits only; <c>\s</c> includes the no-break space.</summary>
    [GeneratedRegex(@"^(?:(?<L>fm|am)\s*)?(?<I>[0-9]{2,4})(?:(?<S>[.,])(?<R>[0-9]{0,2}))?(?:\s*(?<T>fm|mhz|am|khz))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FrequencyQueryPattern();

    /// <summary>The §3.3 filters: each non-null field equals the entry's, ordinally, except Language, which equals any one
    /// of the entry's precomputed language names (D84).</summary>
    private static bool Passes(CatalogFilters filters, StationCatalogEntry entry, in StationCatalogIndex.SearchKeys keys) =>
        (filters.Country is null || string.Equals(filters.Country, entry.Country, StringComparison.Ordinal)) &&
        (filters.City is null || string.Equals(filters.City, entry.City, StringComparison.Ordinal)) &&
        (filters.Type is null || string.Equals(filters.Type, entry.Type, StringComparison.Ordinal)) &&
        (filters.Genre is null || string.Equals(filters.Genre, entry.Genre, StringComparison.Ordinal)) &&
        (filters.Language is null || HasName(keys.LanguageNames, filters.Language));

    /// <summary>Whether <paramref name="names"/> holds <paramref name="name"/>, ordinally. A plain loop: allocates nothing.</summary>
    private static bool HasName(string[] names, string name)
    {
        foreach (var candidate in names)
            if (string.Equals(candidate, name, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>The lowest §3.3 tier the entry satisfies, or <see cref="NoMatch"/>. An empty query puts every entry in tier 0.</summary>
    /// <param name="frequency">The frequency query, whose digits are never empty; null when the text is not one. An entry
    /// without frequency digits therefore never matches it, and an entry of band None matches only an any-band query.</param>
    private static int Tier(in StationCatalogIndex.SearchKeys keys, string query, FrequencyQuery? frequency)
    {
        if (query.Length == 0) return 0;
        var at = keys.Name.IndexOf(query, StringComparison.Ordinal);
        if (at >= 0) return at == 0 ? 0 : 1;
        if (keys.NameLocal.Contains(query, StringComparison.Ordinal) || keys.City.Contains(query, StringComparison.Ordinal)) return 2;
        return frequency is { } f && (f.Band == FrequencyBand.None || f.Band == keys.Band) &&
               keys.FrequencyDigits.StartsWith(f.Digits, StringComparison.Ordinal) ? 3 : NoMatch;
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

    /// <summary>A parsed §3.3 frequency query: the digits an entry's frequency digits must start with, and the band the
    /// entry must have (<see cref="FrequencyBand.None"/> for any band).</summary>
    private readonly record struct FrequencyQuery(string Digits, FrequencyBand Band);

    /// <summary>A match's cheap sort keys; <see cref="Position"/> is its index in <see cref="StationCatalogIndex.Entries"/>.</summary>
    private readonly record struct Ranked(int Tier, int Votes, int Position);

    /// <summary>Reverses <see cref="Order"/>, so the selection queue dequeues the worst kept match first.</summary>
    private sealed class WorstFirst(StationCatalogIndex catalog) : IComparer<Ranked>
    {
        public int Compare(Ranked x, Ranked y) => Order(catalog, y, x);
    }
}

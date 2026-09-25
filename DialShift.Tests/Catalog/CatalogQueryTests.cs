using DialShift.Core.Catalog;
using static DialShift.Tests.Catalog.CatalogFixtures;
using static DialShift.Tests.TestHarness;

namespace DialShift.Tests.Catalog;

/// <summary>
/// The Core half of CAT-06..09 (docs/catalog-contracts.md §3.3, §8; D64, D70): <c>Fold</c>, text matching, frequency
/// queries, filters, <c>AvailableValues</c>, ranking, the cap and determinism. Every catalog is a small inline fixture.
/// <see cref="CatalogTests"/> runs these checks once under the invariant culture and once under tr-TR; the
/// <paramref name="c"/> suffix of each method names the culture in the check names.
/// </summary>
internal static class CatalogQueryTests
{
    public static void Run(string c)
    {
        FoldRules(c);
        TextMatching(c);
        FrequencyQueries(c);
        Filters(c);
        AvailableValues(c);
        Tiers(c);
        TieBreaks(c);
        CapAndTotal(c);
        ReferenceAndDeterminism(c);
        IndexContract(c);
        LoneSurrogates(c);
    }

    private static string F(string value) => StationCatalogQuery.Fold(value);

    // ─── CAT-06: Fold ───

    private static void FoldRules(string c)
    {
        Check($"CAT-06 Fold: the §3.3 examples (München, ΑΘΗΝΑΣ = αθήνας, Straße, whitespace, ﬁp){c}",
            F("München") == "munchen" && F("ΑΘΗΝΑΣ") == "αθηνασ" && F("αθήνας") == "αθηνασ" && F("Straße") == "strasse"
            && F("  Radio\t  FM ") == "radio fm" && F("ﬁp") == "fip");
        Check($"CAT-06 Fold: ς ß ẞ æ œ ø ł đ ı map per §3.3 step 3 (upper and lower case){c}",
            F("ΚΟΣΜΟΣ") == "κοσμοσ" && F("Κόσμος") == "κοσμοσ" && F("GROẞ") == "gross" && F("Æther") == "aether" && F("æ") == "ae"
            && F("Œuvre") == "oeuvre" && F("Øresund") == "oresund" && F("Łódź") == "lodz" && F("Đakovo") == "dakovo" && F("ıi") == "ii");
        Check($"CAT-06 Fold: Turkish İ, I and ı all fold to i, independent of the culture (İSTANBUL = ISTANBUL = ıstanbul = istanbul){c}",
            F("İSTANBUL") == "istanbul" && F("ISTANBUL") == "istanbul" && F("ıstanbul") == "istanbul" && F("İzmir") == "izmir");
        Check($"CAT-06 Fold: accents and umlauts drop (Chérie, Côte, Ökowelle, Αθηναϊκός, precomposed = decomposed){c}",
            F("Chérie") == "cherie" && F("Côte d'Azur") == "cote d'azur" && F("Ökowelle") == "okowelle" && F("Αθηναϊκός") == "αθηναικοσ"
            && F("Cafe\u0301") == F("Café") && F("Café") == "cafe");
        Check($"CAT-06 Fold: compatibility forms decompose (full-width, circled, ligatures, no-break and em spaces){c}",
            F("ＦＭ １０１") == "fm 101" && F("Ⓐ") == "a" && F("ﬃ") == "ffi" && F("Radio\u00A0FM") == "radio fm" && F("x\u2003\u2003y") == "x y");
        Check($"CAT-06 Fold: an enclosing mark (U+20DD) drops like a non-spacing mark{c}", F("A\u20DD") == "a");
        Check($"CAT-06 Fold: empty and whitespace-only text fold to \"\"{c}", F("") == "" && F(" \t\n\u00A0") == "");
        Check($"CAT-06 Fold: idempotent on every example{c}",
            new[] { "München", "ΑΘΗΝΑΣ", "Straße", "  Radio\t  FM ", "ﬁp", "İSTANBUL", "ＦＭ １０１", "Æther" }.All(s => F(F(s)) == F(s)));
        Check($"CAT-06 Fold: text past the 256-character stack buffer folds the same (128, 129 and 300 ß; a 402-character name){c}",
            F(new string('ß', 128)) == new string('s', 256) && F(new string('ß', 129)) == new string('s', 258)
            && F(new string('ẞ', 300)) == new string('s', 600) && F(LongName) == LongName.ToLowerInvariant().Replace('Ü', 'u').Replace('ü', 'u'));
    }

    /// <summary>A 402-character name (67 catalog names exceed 100 characters, the longest 399; §1).</summary>
    private static readonly string LongName = "Sender " + string.Join(" ", Enumerable.Repeat("Übertragung", 33));

    // ─── CAT-06: text matching ───

    /// <summary>Built on each use, as are the other fixture indexes, so the tr-TR run folds its keys under tr-TR.</summary>
    private static StationCatalogIndex Text => Index(
        E("Radio München Eins", city: "Ismaning", votes: 10),
        E("Αθήνα Ράδιο", "GR", city: "Αθήνα"),
        E("Chérie Côte", "FR", city: "Nice"),
        E("Evening Talk", "GR", nameLocal: "Βραδινή Κουβέντα", city: "Θεσσαλονίκη"),
        E("Κόσμος FM", "GR"),
        E("Bosporus Hits", "TR", city: "İstanbul"),
        E("Kölsch Welle", city: "Köln"),
        E("Große Freiheit", city: "Hamburg"),
        E("Radio   Spaced   Out", city: "Bremen"),
        E(LongName, city: "Berlin"),
        E("Nightline", url: "https://polka.example.org/live", genre: "Polka", notes: "polka all night", tag: "Music · Polka",
            region: "Polkaland", countryLabel: "Polkania", language: "Polkish", type: "Polka"));

    private static void TextMatching(string c)
    {
        CheckQueries($"CAT-06 case-insensitive: radio münchen / RADIO MÜNCHEN / Radio München find the same station{c}", Text, null,
            ("radio münchen", ["Radio München Eins"]), ("RADIO MÜNCHEN", ["Radio München Eins"]), ("Radio München", ["Radio München Eins"]));
        CheckQueries($"CAT-06 diacritic-insensitive both ways: munchen finds München; cherie / CHÉRIE / chérie / cote find Chérie Côte{c}", Text, null,
            ("munchen", ["Radio München Eins"]), ("cherie", ["Chérie Côte"]), ("CHÉRIE", ["Chérie Côte"]), ("chérie", ["Chérie Côte"]),
            ("cote", ["Chérie Côte"]));
        CheckQueries($"CAT-06 German: grosse / GROSSE / große / GROẞE find Große Freiheit; koln / KÖLN find the city Köln{c}", Text, null,
            ("grosse", ["Große Freiheit"]), ("GROSSE", ["Große Freiheit"]), ("große", ["Große Freiheit"]), ("GROẞE", ["Große Freiheit"]),
            ("koln", ["Kölsch Welle"]), ("KÖLN", ["Kölsch Welle"]));
        CheckQueries($"CAT-06 German: koeln does not find Köln (ö folds to o, not oe; §3.3){c}", Text, null, ("koeln", []));
        CheckQueries($"CAT-06 Greek with tonos: αθηνα / ΑΘΗΝΑ / Αθήνα find Αθήνα Ράδιο (name and city); ραδιο finds it as a substring{c}", Text, null,
            ("αθηνα", ["Αθήνα Ράδιο"]), ("ΑΘΗΝΑ", ["Αθήνα Ράδιο"]), ("Αθήνα", ["Αθήνα Ράδιο"]), ("ραδιο", ["Αθήνα Ράδιο"]));
        CheckQueries($"CAT-06 Greek final sigma: ΚΟΣΜΟΣ / κοσμος / κόσμοσ / Κόσμος find Κόσμος FM{c}", Text, null,
            ("ΚΟΣΜΟΣ", ["Κόσμος FM"]), ("κοσμος", ["Κόσμος FM"]), ("κόσμοσ", ["Κόσμος FM"]), ("Κόσμος", ["Κόσμος FM"]));
        CheckQueries($"CAT-06 Turkish I: istanbul / İSTANBUL / ISTANBUL / ıstanbul find the city İstanbul{c}", Text, null,
            ("istanbul", ["Bosporus Hits"]), ("İSTANBUL", ["Bosporus Hits"]), ("ISTANBUL", ["Bosporus Hits"]), ("ıstanbul", ["Bosporus Hits"]));
        CheckQueries($"CAT-06 name_local is searched: βραδινη / ΒΡΑΔΙΝΉ / κουβεντα find Evening Talk{c}", Text, null,
            ("βραδινη", ["Evening Talk"]), ("ΒΡΑΔΙΝΉ", ["Evening Talk"]), ("κουβεντα", ["Evening Talk"]));
        CheckQueries($"CAT-06 city is searched: ismaning / θεσσαλονικη / ΘΕΣΣΑΛΟΝΊΚΗ find their stations{c}", Text, null,
            ("ismaning", ["Radio München Eins"]), ("θεσσαλονικη", ["Evening Talk"]), ("ΘΕΣΣΑΛΟΝΊΚΗ", ["Evening Talk"]));
        CheckQueries($"CAT-06 substring anywhere in the name: freiheit, adio{c}", Text, null,
            ("freiheit", ["Große Freiheit"]), ("adio", ["Radio München Eins", "Radio   Spaced   Out"]));
        CheckQueries($"CAT-06 whitespace collapses on both sides: radio spaced out / '  RADIO    spaced  ' find 'Radio   Spaced   Out'{c}", Text, null,
            ("radio spaced out", ["Radio   Spaced   Out"]), ("  RADIO    spaced  ", ["Radio   Spaced   Out"]));
        CheckQueries($"CAT-06 genre, notes, tag, type, region, country label, language and stream URL are not searched{c}", Text, null,
            ("polka", []), ("Polkaland", []), ("polkania", []), ("polkish", []), ("example.org", []), ("night", ["Nightline"]));
        var longQuery = LongName.Substring(7, 300).ToUpperInvariant();
        CheckQueries($"CAT-06 a 300-character upper-case query with umlauts, and a folded one, find the 402-character name{c}", Text, null,
            (longQuery, [LongName]), ("ubertragung ubertragung", [LongName]));
        var all = Text.Entries.Count;
        Check($"CAT-06 null, \"\", whitespace-only and marks-only text fold to the empty query: every entry matches{c}",
            new[] { null, "", "   ", "\t\u00A0", "\u0301\u0308" }.All(q => Search(Text, q).TotalCount == all));
    }

    // ─── CAT-06: unpaired surrogates (D77) ───

    private static void LoneSurrogates(string c)
    {
        StationCatalogEntry[] damaged = [E("Broken\uD800Name", votes: 1), E("Tail\uDC00", votes: 2), E("Radio 😀", votes: 3), E("Plain", votes: 4)];
        var queries = new[] { "\uD800", "ab\uDC00", "\uDC00\uD800", "broken", "tail", "😀", "\uD83D", new string('\uDFFF', 300) };
        StationCatalogIndex? index = null;
        CatalogSearchResult[]? first = null, second = null;
        Check($"CAT-06 D77 a lone surrogate in a query, an entry name or a filter value never throws (index build, Search, AvailableValues, 300 in a row){c}",
            NoThrow(() =>
            {
                index = new StationCatalogIndex(damaged);
                first = queries.Select(q => Search(index, q)).ToArray();
                second = queries.Select(q => Search(new StationCatalogIndex(damaged), q)).ToArray();
                _ = Search(Index(E("x\uD800"), E("\uDFFFy"), E(new string('\uD800', 300))), "\uD800");
                _ = StationCatalogQuery.AvailableValues([E("x", city: "\uD800"), E("y", city: "Köln")], CatalogField.City);
            }));
        Check($"CAT-06 D77 each unpaired surrogate folds to U+FFFD (the §3.3 examples \\uD800 and a\\uDC00b; ab\\uDC00, a reversed pair, 300 in a row){c}",
            F("\uD800") == "\uFFFD" && F("a\uDC00b") == "a\uFFFDb" && F("ab\uDC00") == "ab\uFFFD" && F("\uDC00\uD800") == "\uFFFD\uFFFD"
            && F(new string('\uD800', 300)) == new string('\uFFFD', 300));
        Check($"CAT-06 D77 a valid pair folds exactly as before (the §3.3 example \\uD83D\\uDCFB unchanged; Radio 😀 → radio 😀){c}",
            F("\uD83D\uDCFB") == "\uD83D\uDCFB" && F("Radio 😀") == "radio 😀" && F("😀") == "😀" && F("😀").Length == 2);
        var onEmpty = Search(StationCatalogIndex.Empty, "\uD800");
        Check($"CAT-06 D77 Search(StationCatalogIndex.Empty, \"\\uD800\", CatalogFilters.None) returns an empty result instead of throwing{c}",
            onEmpty.Items.Count == 0 && onEmpty.TotalCount == 0);
        Check($"CAT-06 D77 lone-surrogate searches are deterministic: the same query on two indexes of the same entries gives the same items and total{c}",
            first is not null && second is not null
            && first.Zip(second).All(p => p.First.TotalCount == p.Second.TotalCount && p.First.Items.SequenceEqual(p.Second.Items)));
        CheckQueries($"CAT-06 D77 a lone surrogate matches as U+FFFD: \\uD800, \\uDC00 and half of 😀 find both damaged names, ab\\uDC00 none, 😀 only the valid pair{c}",
            index!, null,
            ("\uD800", ["Tail\uDC00", "Broken\uD800Name"]), ("\uDC00", ["Tail\uDC00", "Broken\uD800Name"]), ("\uD83D", ["Tail\uDC00", "Broken\uD800Name"]),
            ("ab\uDC00", []), ("broken", ["Broken\uD800Name"]), ("😀", ["Radio 😀"]));
    }

    // ─── CAT-07: frequency queries ───

    private static StationCatalogIndex Frequencies => Index(
        E("Alpha", "DE", frequency: "101.5", votes: 1),
        E("Bravo", "DE", frequency: "101.7", votes: 2),
        E("Charlie", "FR", frequency: "1017", votes: 3),
        E("Delta", "DE", frequency: "", votes: 4),
        E("Echo", "GR", frequency: "1593", votes: 5),
        E("Foxtrot", "GR", frequency: "98.4", votes: 6));

    private static void FrequencyQueries(string c)
    {
        CheckQueries($"CAT-07 1015, 101.5, 101,5, 101.5 FM (and fm/MHz/spacing variants) match FM 101.5 and not 101.7{c}", Frequencies, null,
            ("1015", ["Alpha"]), ("101.5", ["Alpha"]), ("101,5", ["Alpha"]), ("101.5 FM", ["Alpha"]), ("101.5fm", ["Alpha"]),
            ("101.5 MHz", ["Alpha"]), (" 101.5 ", ["Alpha"]), ("101,5 fm", ["Alpha"]), ("101.5\u00A0FM", ["Alpha"]));
        CheckQueries($"CAT-07 101 and 101. are prefixes: FM 101.5, FM 101.7 and AM 1017, by votes; empty FrequencyFm never matches{c}", Frequencies, null,
            ("101", ["Charlie", "Bravo", "Alpha"]), ("101.", ["Charlie", "Bravo", "Alpha"]), ("10", ["Charlie", "Bravo", "Alpha"]));
        CheckQueries($"CAT-07 101.7 matches FM 101.7 and AM 1017 (same digits, §3.3 prefix rule){c}", Frequencies, null,
            ("101.7", ["Charlie", "Bravo"]), ("1017", ["Charlie", "Bravo"]));
        CheckQueries($"CAT-07 AM 1593: 1593, 1593 kHz, 159 match; 98.4, 984, 98,4 FM, 98 match FM 98.4{c}", Frequencies, null,
            ("1593", ["Echo"]), ("1593 kHz", ["Echo"]), ("159", ["Echo"]), ("98.4", ["Foxtrot"]), ("984", ["Foxtrot"]),
            ("98,4 FM", ["Foxtrot"]), ("98", ["Foxtrot"]));
        CheckQueries($"CAT-07 a frequency query ANDs with a filter: 101 with Country FR finds only AM 1017{c}", Frequencies, new CatalogFilters(Country: "FR"),
            ("101", ["Charlie"]));

        var notFrequency = Index(E("Radio 1"), E("Golf", frequency: "101.555"), E("Hotel", frequency: "1234.5"), E("India", frequency: "101.5"));
        CheckQueries($"CAT-07 1, 12345 and 101.555 are not frequency queries (they only text-match){c}", notFrequency, null,
            ("1", ["Radio 1"]), ("12345", []), ("101.555", []));
        CheckQueries($"CAT-07 not frequency queries: FM 101.5 (unit first), 101.5 FMX, 101.50 (3 decimals of digits), Arabic-Indic and full-width digits{c}",
            notFrequency, null, ("FM 101.5", []), ("101.5 FMX", []), ("101.50", []), ("١٠١٫٥", []), ("１０１.５", []));

        var ranked = Index(E("Kilo", frequency: "101.5", votes: 1000), E("Hit 101.5", votes: 0), E("101.5 Classics", votes: 0));
        CheckQueries($"CAT-07 tier 3 ranks below name matches: 101.5 lists the names containing it before the 1000-vote frequency match{c}", ranked, null,
            ("101.5", ["101.5 Classics", "Hit 101.5", "Kilo"]));
    }

    // ─── CAT-08: filters ───

    private static readonly StationCatalogEntry[] FilterEntries =
    [
        E("Antenne Nord", "DE", countryLabel: "Germany", city: "Hamburg", type: "Music", genre: "Pop", language: "German", votes: 10),
        E("Antenne Süd", "DE", countryLabel: "Germany", city: "München", type: "Music", genre: "Schlager", language: "German", votes: 20),
        E("Info Kanal", "DE", city: "München", type: "News", genre: "News", language: "German", votes: 5),
        E("Antenne Athen", "GR", countryLabel: "Greece", city: "Αθήνα", type: "Music", genre: "Pop", language: "Greek", votes: 7),
        E("Ράδιο Αθήνα", "GR", countryLabel: "Greece", city: "Αθήνα", type: "News", genre: "Talk", language: "Greek", votes: 3),
        E("Antenne Paris", "FR", countryLabel: "France", city: "Paris", type: "Music", genre: "Pop", language: "French", votes: 9),
        E("Web Antenne", "Internet", countryLabel: "Internet (collections)", type: "Music", genre: "Electronic", language: "English", votes: 1),
        E("Stille", "DE", countryLabel: "Germany", votes: 0),
    ];

    private static StationCatalogIndex FilterIndex => new(FilterEntries);

    private static void Filters(string c)
    {
        var everything = new[] { "Antenne Süd", "Antenne Nord", "Antenne Paris", "Antenne Athen", "Info Kanal", "Ράδιο Αθήνα", "Web Antenne", "Stille" };
        CheckQueries($"CAT-08 null = All: CatalogFilters.None and all-null filters return every entry (by votes){c}", FilterIndex, CatalogFilters.None, (null, everything));
        CheckQueries($"CAT-08 null = All (explicit nulls){c}", FilterIndex, new CatalogFilters(null, null, null, null, null), (null, everything));
        Check($"CAT-08 CatalogFilters.None equals new CatalogFilters(){c}", CatalogFilters.None == new CatalogFilters());

        CheckQueries($"CAT-08 Country DE alone (the code, by votes){c}", FilterIndex, new CatalogFilters(Country: "DE"),
            (null, ["Antenne Süd", "Antenne Nord", "Info Kanal", "Stille"]));
        CheckQueries($"CAT-08 City München alone{c}", FilterIndex, new CatalogFilters(City: "München"), (null, ["Antenne Süd", "Info Kanal"]));
        CheckQueries($"CAT-08 Type News alone{c}", FilterIndex, new CatalogFilters(Type: "News"), (null, ["Info Kanal", "Ράδιο Αθήνα"]));
        CheckQueries($"CAT-08 Genre Pop alone{c}", FilterIndex, new CatalogFilters(Genre: "Pop"), (null, ["Antenne Nord", "Antenne Paris", "Antenne Athen"]));
        CheckQueries($"CAT-08 Language Greek alone{c}", FilterIndex, new CatalogFilters(Language: "Greek"), (null, ["Antenne Athen", "Ράδιο Αθήνα"]));

        // Every value of every field, alone: the result is exactly the entries with that value (reference order).
        var ok = true;
        foreach (var field in Enum.GetValues<CatalogField>())
        {
            foreach (var value in FilterEntries.Select(e => Field(e, field)).Distinct().Append("absent"))
            {
                var filters = field switch
                {
                    CatalogField.Country => new CatalogFilters(Country: value),
                    CatalogField.City => new CatalogFilters(City: value),
                    CatalogField.Type => new CatalogFilters(Type: value),
                    CatalogField.Genre => new CatalogFilters(Genre: value),
                    _ => new CatalogFilters(Language: value),
                };
                var result = Search(FilterIndex, null, filters);
                var expected = FilterEntries.Where(e => Field(e, field) == value).OrderByDescending(e => e.Votes ?? 0).ToList();
                if (result.Items.SequenceEqual(expected) && result.TotalCount == expected.Count) continue;
                ok = false;
                Console.WriteLine($"  {field} = {Show(value)}: expected [{string.Join(" | ", expected.Select(e => e.Name))}]; actual [{string.Join(" | ", Names(result))}] total {result.TotalCount}");
            }
        }
        Check($"CAT-08 every value of every field, alone, returns exactly the entries with that value (and \"absent\" none){c}", ok);

        var ordinal = new[]
        {
            new CatalogFilters(Country: "de"), new CatalogFilters(Country: "Germany"), new CatalogFilters(City: "Munchen"),
            new CatalogFilters(City: "münchen"), new CatalogFilters(City: "München "), new CatalogFilters(Genre: "pop"), new CatalogFilters(Language: "greek"),
        };
        Check($"CAT-08 filters compare ordinally (not folded, not by label): de, Germany, Munchen, münchen, 'München ', pop, greek → 0 matches{c}",
            ordinal.All(f => Search(FilterIndex, null, f).TotalCount == 0));
        CheckQueries($"CAT-08 an empty-string filter is not All: City \"\" matches the entries without a city (the UI never offers it){c}", FilterIndex,
            new CatalogFilters(City: ""), (null, ["Web Antenne", "Stille"]));

        var five = new CatalogFilters("DE", "München", "Music", "Schlager", "German");
        CheckQueries($"CAT-08 all five filters AND with each other and the text: antenne + DE/München/Music/Schlager/German → Antenne Süd{c}", FilterIndex, five,
            ("antenne", ["Antenne Süd"]), (null, ["Antenne Süd"]), ("nord", []));
        CheckQueries($"CAT-08 one mismatching filter of five empties the result{c}", FilterIndex, five with { Genre = "Pop" }, ("antenne", []), (null, []));
        CheckQueries($"CAT-08 text AND a filter: antenne + GR → Antenne Athen; nord + GR → none{c}", FilterIndex,
            new CatalogFilters(Country: "GR"), ("antenne", ["Antenne Athen"]), ("nord", []));
        CheckQueries($"CAT-08 text AND Type News: αθηνα → Ράδιο Αθήνα (the Music station in Αθήνα is filtered out){c}", FilterIndex,
            new CatalogFilters(Type: "News"), ("αθηνα", ["Ράδιο Αθήνα"]));
    }

    private static string Field(StationCatalogEntry e, CatalogField field) => field switch
    {
        CatalogField.Country => e.Country,
        CatalogField.City => e.City,
        CatalogField.Type => e.Type,
        CatalogField.Genre => e.Genre,
        _ => e.Language,
    };

    private static void AvailableValues(string c)
    {
        IReadOnlyList<CatalogFilterValue> Values(IReadOnlyList<StationCatalogEntry> entries, CatalogField field) => StationCatalogQuery.AvailableValues(entries, field);
        static CatalogFilterValue V(string value, string? label = null) => new(value, label ?? value);

        CheckSequence($"CAT-08 AvailableValues Country: codes with their labels, ordered by the folded label{c}", Values(FilterEntries, CatalogField.Country),
            [V("FR", "France"), V("DE", "Germany"), V("GR", "Greece"), V("Internet", "Internet (collections)")]);
        CheckSequence($"CAT-08 AvailableValues City: distinct, no empty value, folded order (Hamburg, München, Paris, Αθήνα){c}", Values(FilterEntries, CatalogField.City),
            [V("Hamburg"), V("München"), V("Paris"), V("Αθήνα")]);
        CheckSequence($"CAT-08 AvailableValues Type: Music, News (the empty type left out){c}", Values(FilterEntries, CatalogField.Type), [V("Music"), V("News")]);
        CheckSequence($"CAT-08 AvailableValues Genre{c}", Values(FilterEntries, CatalogField.Genre),
            [V("Electronic"), V("News"), V("Pop"), V("Schlager"), V("Talk")]);
        CheckSequence($"CAT-08 AvailableValues Language{c}", Values(FilterEntries, CatalogField.Language),
            [V("English"), V("French"), V("German"), V("Greek")]);

        var labels = new[]
        {
            E("a", "DE"), E("b", "DE", countryLabel: "Germany"), E("c", "DE", countryLabel: "Deutschland"), E("d", "XX"),
            E("e", "FR", countryLabel: "france"), E("f", "AT", countryLabel: "Österreich"), E("g", "D", countryLabel: "Germany"),
            E("h", "Internet", countryLabel: "Internet (collections)"),
        };
        CheckSequence($"CAT-08 AvailableValues Country labels: first non-empty label in list order, else the code; folded-label order (france before Germany), then value (D before DE){c}",
            Values(labels, CatalogField.Country),
            [V("FR", "france"), V("D", "Germany"), V("DE", "Germany"), V("Internet", "Internet (collections)"), V("AT", "Österreich"), V("XX")]);
        CheckSequence($"CAT-08 AvailableValues ties on the folded label order by label ordinal: Munchen, München, munchen{c}",
            Values([E("a", city: "munchen"), E("b", city: "München"), E("c", city: "Munchen"), E("d", city: "München")], CatalogField.City),
            [V("Munchen"), V("München"), V("munchen")]);
        CheckSequence($"CAT-08 AvailableValues is ordinally distinct: Pop, 'Pop ', pop are three values{c}",
            Values([E("a", type: "pop"), E("b", type: "Pop "), E("c", type: "Pop"), E("d", type: "Pop")], CatalogField.Type),
            [V("Pop"), V("Pop "), V("pop")]);
        Check($"CAT-08 AvailableValues: every non-country Label equals its Value; an empty list gives no values{c}",
            new[] { CatalogField.City, CatalogField.Type, CatalogField.Genre, CatalogField.Language }.All(f => Values(FilterEntries, f).All(v => v.Label == v.Value))
            && Enum.GetValues<CatalogField>().All(f => Values([], f).Count == 0));
        Check($"CAT-08 AvailableValues is flat and order-independent for consistent labels (reversed input, same values){c}",
            Enum.GetValues<CatalogField>().All(f => Values(FilterEntries, f).SequenceEqual(Values(FilterEntries.Reverse().ToArray(), f))));
        Check($"CAT-08 AvailableValues: a null list or entry throws ArgumentNullException; an undefined field ArgumentOutOfRangeException{c}",
            Throws<ArgumentNullException>(() => StationCatalogQuery.AvailableValues(null!, CatalogField.City))
            && Throws<ArgumentNullException>(() => StationCatalogQuery.AvailableValues([E("a"), null!], CatalogField.City))
            && Throws<ArgumentOutOfRangeException>(() => StationCatalogQuery.AvailableValues(FilterEntries, (CatalogField)99)));
    }

    // ─── CAT-09: ranking ───

    private static void Tiers(string c)
    {
        var index = Index(
            E("Mike", votes: 9999),
            E("Kappa", nameLocal: "Νέα Nova", votes: 1000),
            E("Lima", city: "Novara", votes: 5000),
            E("Radio Nova", votes: 100),
            E("Nova Radio", votes: 1));
        CheckQueries($"CAT-09 tiers: name prefix, then name substring, then name_local or city (by votes); non-matches left out{c}", index, null,
            ("nova", ["Nova Radio", "Radio Nova", "Lima", "Kappa"]));
        var four = Index(
            E("Papa", frequency: "101.5", votes: 9999),
            E("Oscar", nameLocal: "Ράδιο 101.5", votes: 10),
            E("Hit 101.5", votes: 5),
            E("101.5 Classics", votes: 0));
        CheckQueries($"CAT-09 all four tiers in order whatever the votes: 101.5 → name prefix, name substring, name_local, frequency{c}", four, null,
            ("101.5", ["101.5 Classics", "Hit 101.5", "Oscar", "Papa"]));
        var lowest = Index(E("Tango", city: "Nova", votes: 50), E("Nova Sierra", city: "Nova", votes: 0), E("Uniform", city: "Nova", frequency: "101.5", votes: 60));
        CheckQueries($"CAT-09 an entry takes the lowest tier it satisfies (name prefix beats a higher-voted city match){c}", lowest, null,
            ("nova", ["Nova Sierra", "Uniform", "Tango"]));
        Check($"CAT-09 the empty query puts every entry in tier 0: the most-voted stations first{c}",
            Names(Search(index, "")).SequenceEqual(["Mike", "Lima", "Kappa", "Radio Nova", "Nova Radio"]));
    }

    private static void TieBreaks(string c)
    {
        CheckQueries($"CAT-09 votes descending, null ranked as 0 (A-null before Y-zero, B-zero before Z-null by folded name){c}",
            Index(E("Z-null"), E("A1", votes: 5), E("Y-zero", votes: 0), E("A3", votes: 10), E("B-zero", votes: 0), E("A-null")), null,
            (null, ["A3", "A1", "A-null", "B-zero", "Y-zero", "Z-null"]));
        CheckQueries($"CAT-09 equal votes: folded name ordinal (alpha before Beta, although 'B' < 'a' ordinally){c}",
            Index(E("Beta", votes: 5), E("alpha", votes: 5)), null, (null, ["alpha", "Beta"]));
        CheckQueries($"CAT-09 equal folded name: Name ordinal (Eclair, eclair, Éclair){c}",
            Index(E("Éclair", votes: 5), E("eclair", votes: 5), E("Eclair", votes: 5)), null, (null, ["Eclair", "eclair", "Éclair"]));

        var countries = Search(Index(E("Same", "FR"), E("Same", "Internet"), E("Same", "DE")), null);
        CheckSequence($"CAT-09 equal name: Country ordinal (DE, FR, Internet){c}", countries.Items.Select(e => e.Country), ["DE", "FR", "Internet"]);
        var urls = Search(Index(E("Same", url: "https://x.example.org/b"), E("Same", url: "https://x.example.org/a"), E("Same", url: "https://x.example.org/B")), null);
        CheckSequence($"CAT-09 equal country: StreamUrl ordinal (/B, /a, /b){c}", urls.Items.Select(e => e.StreamUrl),
            ["https://x.example.org/B", "https://x.example.org/a", "https://x.example.org/b"]);
        var twins = new[] { E("Twin", tag: "first"), E("Twin", tag: "second"), E("Twin", tag: "third") };
        CheckSequence($"CAT-09 equal on every key: catalog position decides (and reversing the catalog reverses them){c}",
            Search(new StationCatalogIndex(twins), "twin").Items.Select(e => e.Tag).Concat(Search(new StationCatalogIndex(twins.Reverse().ToArray()), "twin").Items.Select(e => e.Tag)),
            ["first", "second", "third", "third", "second", "first"]);
        CheckQueries($"CAT-09 tie-breaks apply inside a text tier too (tier 1 of 'o': votes, then folded name){c}",
            Index(E("Foo", votes: 1), E("Boo", votes: 1), E("Zoo", votes: 7), E("Oslo", votes: 0)), null, ("o", ["Oslo", "Zoo", "Boo", "Foo"]));
    }

    private static void CapAndTotal(string c)
    {
        var entries = Enumerable.Range(0, 120).Select(i => E($"Station {i:D3}", votes: i % 7, url: $"https://s{i:D3}.example.org/")).ToList();
        var index = new StationCatalogIndex(entries);
        var reference = Reference(entries, "station", CatalogFilters.None);
        var capped = Search(index, "station");
        Check($"CAT-09 cap 50 by default (DefaultCap): 50 items and the true total 120{c}",
            StationCatalogQuery.DefaultCap == 50 && capped.Items.Count == 50 && capped.TotalCount == 120);
        CheckSequence($"CAT-09 the capped items are the first 50 of the full order{c}", capped.Items.Select(e => e.Name), reference.Take(50).Select(e => e.Name));
        var ok = true;
        foreach (var cap in new[] { 1, 2, 49, 50, 51, 119, 120, 121, 1000, int.MaxValue })
        {
            var result = Search(index, "station", cap: cap);
            if (result.TotalCount == 120 && result.Items.SequenceEqual(reference.Take(cap))) continue;
            ok = false;
            Console.WriteLine($"  cap {cap}: {result.Items.Count} items, total {result.TotalCount}");
        }
        Check($"CAT-09 caps 1, 2, 49..51, 119..121, 1000, int.MaxValue: min(cap, total) items in order, total always 120{c}", ok);
        var filtered = Search(index, "station", new CatalogFilters(Country: "FR"));
        Check($"CAT-09 no match: empty items and total 0{c}", filtered.Items.Count == 0 && filtered.TotalCount == 0 && Search(index, "zzz").TotalCount == 0);
        Check($"CAT-09 cap < 1 throws ArgumentOutOfRangeException (0, -1, int.MinValue){c}",
            Throws<ArgumentOutOfRangeException>(() => Search(index, "x", cap: 0)) && Throws<ArgumentOutOfRangeException>(() => Search(index, "x", cap: -1))
            && Throws<ArgumentOutOfRangeException>(() => Search(index, "x", cap: int.MinValue)));
        Check($"CAT-09 a null catalog or null filters throws ArgumentNullException{c}",
            Throws<ArgumentNullException>(() => StationCatalogQuery.Search(null!, "x", CatalogFilters.None))
            && Throws<ArgumentNullException>(() => StationCatalogQuery.Search(index, "x", null!)));
    }

    /// <summary>The heap selection equals the reference order, and a shuffled catalog gives the same output.</summary>
    private static void ReferenceAndDeterminism(string c)
    {
        var entries = Generated(400, seed: 20260925);
        var index = new StationCatalogIndex(entries);
        var mismatches = 0;
        var compared = 0;
        foreach (var query in Queries)
        foreach (var filters in FilterSets)
        {
            var reference = Reference(entries, query, filters);
            foreach (var cap in new[] { 1, 7, 50, entries.Count })
            {
                compared++;
                var result = Search(index, query, filters, cap);
                if (result.TotalCount == reference.Count && result.Items.SequenceEqual(reference.Take(cap))) continue;
                if (++mismatches <= 5)
                    Console.WriteLine($"  query {Show(query)} {filters} cap {cap}: total {result.TotalCount} vs {reference.Count}; first actual [{string.Join(" | ", Names(result).Take(5))}] vs expected [{string.Join(" | ", reference.Take(5).Select(e => e.Name))}]");
            }
        }
        Check($"CAT-09 Search equals the §3.3 reference order and total on a generated 400-entry catalog ({compared} query × filters × cap cases){c}", mismatches == 0);
        static bool NameHit(StationCatalogEntry e, string q) => F(e.Name).Contains(q, StringComparison.Ordinal);
        static bool TextHit(StationCatalogEntry e, string q) => NameHit(e, q) || F(e.NameLocal).Contains(q, StringComparison.Ordinal) || F(e.City).Contains(q, StringComparison.Ordinal);
        var nova = Reference(entries, "nova", CatalogFilters.None);
        Check($"CAT-09 the generated catalog is not a vacuous comparison: the cap bites, and tiers 1, 2 (name_local and city) and 3 all occur{c}",
            nova.Count > 50 && nova.Any(e => NameHit(e, "nova") && !F(e.Name).StartsWith("nova", StringComparison.Ordinal))
            && Reference(entries, "local", CatalogFilters.None).Any(e => !NameHit(e, "local"))
            && Reference(entries, "novara", CatalogFilters.None).Any(e => !NameHit(e, "novara"))
            && Reference(entries, "101.5", CatalogFilters.None).Any(e => !TextHit(e, "101.5")) && Search(index, null).TotalCount == 400);

        var baseline = Queries.SelectMany(q => FilterSets.Select(f => Search(index, q, f))).ToList();
        var differing = 0;
        for (var seed = 1; seed <= 10; seed++)
        {
            var shuffled = new StationCatalogIndex(Shuffled(entries, seed));
            var results = Queries.SelectMany(q => FilterSets.Select(f => Search(shuffled, q, f))).ToList();
            differing += results.Zip(baseline).Count(p => p.First.TotalCount != p.Second.TotalCount || !p.First.Items.SequenceEqual(p.Second.Items));
        }
        Check($"CAT-09 determinism: 10 shuffles of the catalog give the same items and totals for every query and filter ({baseline.Count} cases each){c}", differing == 0);
    }

    private static void IndexContract(string c)
    {
        var source = new List<StationCatalogEntry> { E("One", votes: 1), E("Two", votes: 2) };
        var index = new StationCatalogIndex(source);
        source.Add(E("Three", votes: 3));
        source[0] = E("Replaced", votes: 9);
        Check($"CAT-09 the index copies its list: later changes to the source list change neither Entries nor results{c}",
            index.Entries.Select(e => e.Name).SequenceEqual(["One", "Two"]) && Names(Search(index, null)).SequenceEqual(["Two", "One"]));
        Check($"CAT-09 the index keeps the list order in Entries{c}",
            new StationCatalogIndex(FilterEntries).Entries.SequenceEqual(FilterEntries));
        Check($"CAT-09 a null list or a null entry throws ArgumentNullException{c}",
            Throws<ArgumentNullException>(() => _ = new StationCatalogIndex(null!)) && Throws<ArgumentNullException>(() => _ = new StationCatalogIndex([E("a"), null!])));
        var empty = Search(StationCatalogIndex.Empty, null);
        Check($"CAT-09 StationCatalogIndex.Empty has no entries and every search on it is empty with total 0{c}",
            StationCatalogIndex.Empty.Entries.Count == 0 && empty.Items.Count == 0 && empty.TotalCount == 0 && Search(StationCatalogIndex.Empty, "101.5").TotalCount == 0);
    }
}

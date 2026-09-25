using System.Globalization;
using DialShift.Core.Catalog;
using static DialShift.Tests.Catalog.CatalogFixtures;
using static DialShift.Tests.TestHarness;

namespace DialShift.Tests.Catalog;

/// <summary>
/// The <c>Catalog</c> suite (brief 3; docs/catalog-contracts.md §8). Check names start with their CAT row id, so the
/// acceptance matrix §11 can cite them. Covered here: the Core half of CAT-06..09 (<see cref="CatalogQueryTests"/>).
/// </summary>
/// <remarks>
/// <b>Machine-independent by construction:</b> every catalog is an inline fixture (never <c>data/output/app-catalog.json</c>),
/// nothing is timed (CAT-16 is the perf lane's <c>CatalogPerf</c>), and the query checks run twice: under the harness's
/// invariant culture and under tr-TR, whose dotted/dotless I casing and decimal comma are what a culture-sensitive
/// matcher would get wrong (D70). A cross-culture check then compares whole result sets across several cultures.
/// </remarks>
public static class CatalogTests
{
    /// <summary>Cultures whose casing or number format differ from the invariant culture in ways that matter to matching.</summary>
    private static readonly string[] OtherCultures = ["tr-TR", "az-Latn-AZ", "lt-LT", "el-GR", "de-DE"];

    public static void Run()
    {
        CatalogQueryTests.Run("");
        RunUnderTurkish();
        CrossCultureResults();
    }

    private static void RunUnderTurkish()
    {
        const string name = "CAT-06..09 query checks under tr-TR";
        UnderCulture("tr-TR", available =>
        {
            // Without real culture data (globalization-invariant mode) tr-TR would case like the invariant culture, and a
            // green run would prove nothing, so it is a SKIP rather than a pass.
            var turkish = available && "i".ToUpper(CultureInfo.CurrentCulture) == "İ" && "I".ToLower(CultureInfo.CurrentCulture) == "ı"
                          && CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator == ",";
            if (!turkish)
            {
                Skip(name, "tr-TR culture data is not available on this host (globalization-invariant mode or no ICU)");
                return;
            }
            Check("CAT-06 tr-TR is really Turkish here: \"i\" upper-cases to İ, \"I\" lower-cases to ı, the decimal separator is ','", turkish);
            CatalogQueryTests.Run(" [tr-TR]");
        });
        Check("CAT-06 the tr-TR run restores the invariant culture (CurrentCulture and CurrentUICulture)",
            CultureInfo.CurrentCulture.Equals(CultureInfo.InvariantCulture) && CultureInfo.CurrentUICulture.Equals(CultureInfo.InvariantCulture));
    }

    /// <summary>Every query and filter set of the generated catalog, plus <c>AvailableValues</c>, gives identical output
    /// under each of <see cref="OtherCultures"/> as under the invariant culture; the index is rebuilt under each culture.</summary>
    private static void CrossCultureResults()
    {
        var entries = Generated(300, seed: 7);
        (List<CatalogSearchResult> Results, List<IReadOnlyList<CatalogFilterValue>> Values) Snapshot()
        {
            var index = new StationCatalogIndex(entries);
            var results = Queries.SelectMany(q => FilterSets.Select(f => Search(index, q, f))).ToList();
            var values = Enum.GetValues<CatalogField>().Select(f => StationCatalogQuery.AvailableValues(entries, f)).ToList();
            return (results, values);
        }

        var invariant = Snapshot();
        var compared = new List<string>();
        var differing = new List<string>();
        foreach (var culture in OtherCultures)
        {
            UnderCulture(culture, available =>
            {
                if (!available) return;
                compared.Add(culture);
                var other = Snapshot();
                var same = other.Results.Zip(invariant.Results).All(p => p.First.TotalCount == p.Second.TotalCount && p.First.Items.SequenceEqual(p.Second.Items))
                           && other.Values.Zip(invariant.Values).All(p => p.First.SequenceEqual(p.Second));
                if (!same) differing.Add(culture);
            });
        }
        if (compared.Count == 0)
        {
            Skip("CAT-09 results identical across cultures", "none of " + string.Join(", ", OtherCultures) + " is available on this host");
            return;
        }
        if (differing.Count > 0) Console.WriteLine("  differing cultures: " + string.Join(", ", differing));
        Check($"CAT-09 Search and AvailableValues give identical output under the invariant culture and {string.Join(", ", compared)} " +
              $"({invariant.Results.Count} query × filter cases, 5 fields)", differing.Count == 0);
    }

    /// <summary>
    /// Runs <paramref name="run"/> with CurrentCulture and CurrentUICulture set to <paramref name="name"/>, restoring both
    /// afterwards even when a check throws. <paramref name="run"/> gets false, with the culture unchanged, when the
    /// culture cannot be created (globalization-invariant mode with predefined cultures only).
    /// </summary>
    private static void UnderCulture(string name, Action<bool> run)
    {
        var (culture, uiCulture) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo target;
        try { target = CultureInfo.GetCultureInfo(name); }
        catch (CultureNotFoundException)
        {
            run(false);
            return;
        }
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = target;
            run(true);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }
}

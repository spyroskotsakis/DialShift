using System.Globalization;

namespace DialShift.Tests;

/// <summary>A named group of checks. Sync suites are wrapped so the runner treats every suite as async.</summary>
public sealed record TestSuite(string Name, Func<Task> Run)
{
    public TestSuite(string name, Action run) : this(name, () => { run(); return Task.CompletedTask; }) { }
}

/// <summary>Thrown by <see cref="TestHarness.Check"/>; its stack trace points at the failing check's file and line.</summary>
public sealed class CheckFailedException(string name) : Exception("FAIL: " + name);

/// <summary>
/// Framework-free console runner. <see cref="Check"/> prints <c>PASS: name</c> or throws. A failing check stops its
/// suite; the remaining suites still run and the process exits non-zero. <see cref="Skip"/> prints
/// <c>SKIP: name (reason)</c> and is counted in the totals, so no check is ever skipped silently (OS-specific checks
/// skip on the other OS). <c>--filter text</c> runs only suites whose name contains <c>text</c> (case-insensitive).
/// </summary>
public static class TestHarness
{
    private static int passed;
    private static int skipped;

    public static void Check(string name, bool condition)
    {
        if (!condition) throw new CheckFailedException(name);
        Console.WriteLine("PASS: " + name);
        Interlocked.Increment(ref passed);
    }

    /// <summary>Records a check that cannot run here. <paramref name="reason"/> is mandatory and printed.</summary>
    public static void Skip(string name, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Console.WriteLine($"SKIP: {name} ({reason})");
        Interlocked.Increment(ref skipped);
    }

    /// <summary>True when <paramref name="action"/> throws <typeparamref name="T"/> (or a subtype). Any other exception propagates, so the suite reports it in full.</summary>
    public static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    /// <summary>Async <see cref="Throws{T}"/>: the exception may be thrown synchronously or from the returned task.</summary>
    public static async Task<bool> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); return false; }
        catch (T) { return true; }
    }

    /// <summary>True when <paramref name="action"/> completes; an exception is printed and gives false.</summary>
    public static bool NoThrow(Action action)
    {
        try { action(); return true; }
        catch (Exception ex) { Console.WriteLine("  unexpected exception: " + ex); return false; }
    }

    /// <summary>Async <see cref="NoThrow"/>.</summary>
    public static async Task<bool> NoThrowAsync(Func<Task> action)
    {
        try { await action(); return true; }
        catch (Exception ex) { Console.WriteLine("  unexpected exception: " + ex); return false; }
    }

    public static async Task<int> RunAsync(string[] args, params TestSuite[] suites)
    {
        // Machine independence: formatting and parsing never depend on the host culture.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

        if (!TryParseFilter(args, out var filter))
        {
            Console.Error.WriteLine("Usage: DialShift.Tests [--filter <suite-name-substring>]");
            return 2;
        }
        var selected = suites.Where(s => filter is null || s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (selected.Count == 0)
        {
            Console.Error.WriteLine($"No suite matches '{filter}'. Suites: {string.Join(", ", suites.Select(s => s.Name))}");
            return 2;
        }

        var failed = new List<string>();
        foreach (var suite in selected)
        {
            Console.WriteLine($"\n== {suite.Name} ==");
            try { await suite.Run(); }
            catch (Exception ex)
            {
                failed.Add(suite.Name);
                Console.Error.WriteLine(ex is CheckFailedException ? $"{ex.Message}\n{ex.StackTrace}" : $"ERROR in {suite.Name}: {ex}");
            }
        }

        Console.WriteLine($"\n{passed} passed, {skipped} skipped; {selected.Count - failed.Count}/{selected.Count} suites green{(filter is null ? "" : $" (filter '{filter}')")}.");
        if (failed.Count == 0) return 0;
        Console.Error.WriteLine("FAILED suites: " + string.Join(", ", failed));
        return 1;
    }

    private static bool TryParseFilter(string[] args, out string? filter)
    {
        filter = null;
        if (args.Length == 0) return true;
        if (args.Length == 2 && args[0] == "--filter" && !string.IsNullOrWhiteSpace(args[1])) { filter = args[1]; return true; }
        if (args.Length == 1 && args[0].StartsWith("--filter=", StringComparison.Ordinal) && args[0].Length > "--filter=".Length) { filter = args[0]["--filter=".Length..]; return true; }
        return false;
    }
}

using System.Text.RegularExpressions;

namespace DialShift.App.Services;

/// <summary>
/// Log redaction (acceptance matrix §8.2.7). A URL is reduced to <c>scheme://host[:port]/…</c>: user-info, path,
/// query and fragment are dropped. Free text has every URL-looking substring redacted that way, and the user's home
/// directory prefix replaced by <c>~</c>. Never throws.
/// </summary>
public static partial class StreamUrlRedactor
{
    private const string Ellipsis = "…";

    /// <summary>Any <c>scheme://…</c> run up to whitespace or a quote/angle bracket (rule 2, broader than http/https on purpose).</summary>
    [GeneratedRegex("""\b[A-Za-z][A-Za-z0-9+.\-]*://[^\s"'<>]+""", RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    private static readonly string? HomePrefix = ResolveHomePrefix();

    /// <summary>Returns <c>scheme://host[:port]/…</c>. The port appears only when it is not the scheme default.</summary>
    public static string RedactUrl(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri) return "[relative-url]";
        // Uri.Authority is host plus non-default port; it never contains user-info.
        return url.Scheme + "://" + url.Authority + "/" + Ellipsis;
    }

    /// <summary>Redacts every URL in <paramref name="text"/> and replaces the home directory prefix with <c>~</c>.</summary>
    public static string RedactText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        try
        {
            var redacted = UrlPattern().Replace(text, static m => RedactUrlText(m.Value));
            if (HomePrefix != null)
                redacted = redacted.Replace(HomePrefix, "~",
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            return redacted;
        }
        catch (Exception)
        {
            // Redaction must never let the original text through when something unexpected happens.
            return "[redaction-failed]";
        }
    }

    private static string RedactUrlText(string candidate)
    {
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var url) && !string.IsNullOrEmpty(url.Authority))
            return RedactUrl(url);
        var scheme = candidate[..candidate.IndexOf("://", StringComparison.Ordinal)];
        return scheme + "://[redacted]/" + Ellipsis;
    }

    private static string? ResolveHomePrefix()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            home = Path.TrimEndingDirectorySeparator(home);
            // A root or empty home would turn every path into "~…"; skip replacement then.
            return home.Length > 3 ? home : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

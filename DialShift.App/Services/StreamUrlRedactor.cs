using System.Text;
using System.Text.RegularExpressions;

namespace DialShift.App.Services;

/// <summary>
/// The one redactor for the log file and for engine diagnostics (acceptance matrix §8.2.7). It prefers over-redaction to
/// a leak, never throws and runs in linear time (it is on the log hot path). Free text goes through these passes, in order:
/// <list type="number">
/// <item><b>URLs.</b> A scanner finds every <c>scheme://</c> (any scheme, any case, also glued to preceding text such as
/// <c>url_http://</c>), every JSON-escaped <c>scheme:\/\/</c>, and every scheme-less <c>//host</c> that starts a token. Each
/// becomes <c>scheme://host[:port]/…</c>: user-info, path, query and fragment are dropped. IPv6 literals keep their
/// brackets. When the host cannot be trusted (no host, unparseable authority, an <c>@</c> after the authority) it becomes
/// <c>[redacted]</c>. When whitespace splits a URL, the following tokens that look like its continuation (they contain
/// <c>/ \ ? # &amp; @</c>) are dropped too.</item>
/// <item><b>User-info anywhere.</b> Any <c>user:password@</c> run (the colon may be <c>%3A</c>) is removed, with or
/// without a scheme.</item>
/// <item><b>Detached queries.</b> A <c>?</c> glued to a word and followed by a <c>key=value</c> run becomes <c>?…</c>.</item>
/// <item><b>Secrets by name.</b> The value of any <c>…token=</c>, <c>…key=</c>, <c>…sig=</c>, <c>…secret=</c>,
/// <c>…auth=</c>, <c>…password=</c> (and similar) pair becomes <c>…</c>, as do <c>Authorization:</c> header values and
/// <c>Bearer</c> tokens.</item>
/// <item><b>Home directory.</b> The user's home prefix becomes <c>~</c> (only at a path boundary).</item>
/// </list>
/// </summary>
public static partial class StreamUrlRedactor
{
    /// <summary>Marks what was removed: the path of a redacted URL (<c>scheme://host/…</c>) or a redacted value.</summary>
    public const string Ellipsis = "…";

    private const string RedactedHost = "[redacted]";

    /// <summary>Characters that end a URL wherever they appear: they are never part of a URL in prose, JSON or markup.</summary>
    private const string HardStops = "\"<>`";

    /// <summary>Closing punctuation after a URL that belongs to the surrounding text; kept after the redacted URL.</summary>
    private const string TrailingPunctuation = ")]}'.,;:!";

    /// <summary>A token after a whitespace-broken URL containing one of these is treated as the URL's continuation.</summary>
    private const string ContinuationMarks = "/\\?#&@";

    private static readonly string? HomePrefix = ResolveHomePrefix();

    // NonBacktracking: linear time whatever the input (log text can contain long runs such as base64 blobs).

    /// <summary><c>user:password@</c> (colon literal or <c>%3A</c>) anywhere, with or without a scheme.</summary>
    [GeneratedRegex("""[^\s/@:"<>()\[\]{}]+(?::|%3[Aa])[^\s/@"<>]*@""", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex UserInfoPattern();

    /// <summary>A query glued to a word (<c>stream?token=…</c>) whose URL start was lost; group 1 is the preceding character.</summary>
    [GeneratedRegex("""([^\s?])\?[^\s"<>?]*=[^\s"<>]*""", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex DetachedQueryPattern();

    /// <summary>A secret-named key/value pair; group 1 is the key. Keys only need to end in one of the names (<c>access_token</c>, <c>x-api-key</c>).</summary>
    [GeneratedRegex("""([A-Za-z0-9_.\-]*(?:token|key|sig|signature|secret|password|passwd|pwd|pass|auth|authorization|credential|credentials|jwt))=[^\s&"<>]+""",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex SecretPairPattern();

    /// <summary>
    /// An <c>Authorization:</c>/<c>Proxy-Authorization:</c> header value (scheme and credentials: two tokens) or a
    /// <c>Bearer</c> token (one); group 1 is the label. A value that is already <c>…</c> does not match, so the pass is idempotent.
    /// </summary>
    [GeneratedRegex("""(authorization\s*:)[ \t]+[^\s"<>…]+(?:[ \t]+[^\s"<>]+)?|(bearer)[ \t]+[^\s"<>…]+""",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex AuthorizationPattern();

    /// <summary>
    /// Returns <c>scheme://host[:port]/…</c>. The port appears only when it is not the scheme default. A relative URI
    /// becomes <c>[relative-url]</c> and null becomes <c>[no-url]</c>.
    /// </summary>
    public static string RedactUrl(Uri? url)
    {
        if (url is null) return "[no-url]";
        if (!url.IsAbsoluteUri) return "[relative-url]";
        // Uri.Authority is host plus non-default port (IPv6 in brackets); it never contains user-info.
        var authority = url.Authority;
        return url.Scheme + "://" + (authority.Length == 0 ? RedactedHost : authority) + "/" + Ellipsis;
    }

    /// <summary>Redacts <paramref name="text"/> for the log file: every pass in the class summary. Never throws.</summary>
    public static string RedactText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        try
        {
            var redacted = RedactUrls(text);
            redacted = UserInfoPattern().Replace(redacted, "");
            redacted = DetachedQueryPattern().Replace(redacted, "$1?" + Ellipsis);
            redacted = SecretPairPattern().Replace(redacted, "$1=" + Ellipsis);
            redacted = AuthorizationPattern().Replace(redacted, "$1$2 " + Ellipsis);
            return ReplaceHomePrefix(redacted);
        }
        catch (Exception)
        {
            // Redaction must never let the original text through when something unexpected happens.
            return "[redaction-failed]";
        }
    }

    /// <summary>
    /// Redacts native diagnostic text (NSError descriptions, LibVLC log lines) exactly like <see cref="RedactText"/>, then
    /// folds it onto one line and caps it at <paramref name="maxLength"/> characters (plus <c>…</c>). Never throws.
    /// </summary>
    public static string RedactDiagnostic(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var redacted = RedactText(text).ReplaceLineEndings(" ").Trim();
        return redacted.Length <= maxLength ? redacted : redacted[..maxLength] + Ellipsis;
    }

    // ─── URL scanner ───

    private static string RedactUrls(string text)
    {
        StringBuilder? output = null;
        var copied = 0; // text[..copied] has been handled
        var slash = text.IndexOf('/');
        while (slash >= 0)
        {
            var next = slash + 1;
            if (TryFindSeparator(text, slash, out var separatorStart, out var authorityStart)
                && TryFindUrlStart(text, separatorStart, authorityStart, out var urlStart, out var scheme))
            {
                var end = ScanUrl(text, authorityStart, scheme, out var replacement);
                output ??= new StringBuilder(text.Length);
                output.Append(text, copied, urlStart - copied).Append(replacement);
                copied = next = end;
            }
            slash = next < text.Length ? text.IndexOf('/', next) : -1;
        }
        if (output is null) return text;
        return output.Append(text, copied, text.Length - copied).ToString();
    }

    /// <summary>A <c>//</c> or JSON-escaped <c>\/\/</c> at <paramref name="slash"/>.</summary>
    private static bool TryFindSeparator(string text, int slash, out int separatorStart, out int authorityStart)
    {
        var n = text.Length;
        if (slash + 1 < n && text[slash + 1] == '/')
        {
            separatorStart = slash;
            authorityStart = slash + 2;
            return true;
        }
        if (slash >= 1 && text[slash - 1] == '\\' && slash + 2 < n && text[slash + 1] == '\\' && text[slash + 2] == '/')
        {
            separatorStart = slash - 1;
            authorityStart = slash + 3;
            return true;
        }
        separatorStart = authorityStart = 0;
        return false;
    }

    /// <summary>
    /// <c>scheme:</c> before the separator (the scheme starts at the first letter of the run of scheme characters, so
    /// <c>url_http://</c> yields <c>http</c>), or a scheme-less <c>//host</c> at the start of a token.
    /// </summary>
    private static bool TryFindUrlStart(string text, int separatorStart, int authorityStart, out int urlStart, out string? scheme)
    {
        urlStart = separatorStart;
        scheme = null;
        if (separatorStart >= 1 && text[separatorStart - 1] == ':')
        {
            var colon = separatorStart - 1;
            var runStart = colon;
            while (runStart > 0 && IsSchemeChar(text[runStart - 1])) runStart--;
            for (var i = runStart; i < colon; i++)
            {
                if (!char.IsAsciiLetter(text[i])) continue;
                urlStart = i;
                scheme = text[i..colon];
                return true;
            }
            return false; // ":" with no scheme ("1://") is not a URL
        }
        // Scheme-less "//host": only where a token starts, and only when a host (or user-info) follows.
        var tokenStart = separatorStart == 0 || char.IsWhiteSpace(text[separatorStart - 1]) || "\"'(<[=,;".Contains(text[separatorStart - 1]);
        return tokenStart && authorityStart < text.Length && (char.IsLetterOrDigit(text[authorityStart]) || text[authorityStart] is '[' or '%' or '_');
    }

    /// <summary>Scans the URL whose authority starts at <paramref name="start"/>; returns the index just after everything it consumed.</summary>
    private static int ScanUrl(string text, int start, string? scheme, out string replacement)
    {
        var n = text.Length;

        // Authority: everything up to the path, query or fragment. Apostrophes, brackets and parentheses stay inside it
        // (user-info may contain them; IPv6 needs brackets), so the last '@' reliably ends the user-info.
        var authorityEnd = start;
        while (authorityEnd < n && !IsAuthorityStop(text[authorityEnd])) authorityEnd++;

        // Path, query and fragment: everything up to whitespace or a hard stop; closing punctuation at the very end goes back to the text.
        var end = authorityEnd;
        var suffix = "";
        var atAfterAuthority = false;
        if (authorityEnd < n && text[authorityEnd] is '/' or '\\' or '?' or '#')
        {
            var inPath = text[authorityEnd] is '/' or '\\';
            while (end < n && !char.IsWhiteSpace(text[end]) && !HardStops.Contains(text[end]))
            {
                if (text[end] is '?' or '#') inPath = false;
                else if (text[end] == '@' && inPath) atAfterAuthority = true; // user-info with a '/' in the password
                end++;
            }
            var kept = end;
            while (kept > authorityEnd + 1 && TrailingPunctuation.Contains(text[kept - 1])) kept--;
            suffix = text[kept..end];
        }

        var host = ParseHost(text.AsSpan(start, authorityEnd - start), out var authoritySuffix);
        if (atAfterAuthority) host = null;
        if (end == authorityEnd) suffix = authoritySuffix; // no path: punctuation after host[:port] belongs to the text
        replacement = (scheme is null ? "//" : scheme + "://") + (host ?? RedactedHost) + "/" + Ellipsis + suffix;

        // A URL split by whitespace (a space in the path, a wrapped line): drop the tokens that look like its tail.
        if (suffix.Length == 0)
        {
            while (end < n && char.IsWhiteSpace(text[end]))
            {
                var tokenStart = end;
                while (tokenStart < n && char.IsWhiteSpace(text[tokenStart])) tokenStart++;
                var tokenEnd = tokenStart;
                var continuation = false;
                while (tokenEnd < n && !char.IsWhiteSpace(text[tokenEnd]) && !HardStops.Contains(text[tokenEnd]))
                {
                    if (ContinuationMarks.Contains(text[tokenEnd])) continuation = true;
                    tokenEnd++;
                }
                var token = text.AsSpan(tokenStart, tokenEnd - tokenStart);
                // Another URL is not a continuation: the scanner redacts it on its own.
                if (!continuation || token.Contains("//", StringComparison.Ordinal) || token.Contains(@"\/\/", StringComparison.Ordinal)) break;
                end = tokenEnd;
            }
        }
        return end;
    }

    /// <summary>
    /// <c>host[:port]</c> after the last <c>@</c> of <paramref name="authority"/>, or null when it cannot be trusted.
    /// <paramref name="suffix"/> receives trailing closing punctuation that belongs to the surrounding text.
    /// </summary>
    private static string? ParseHost(ReadOnlySpan<char> authority, out string suffix)
    {
        suffix = "";
        var at = authority.LastIndexOf('@');
        var hostPart = at >= 0 ? authority[(at + 1)..] : authority;
        int hostEnd;
        if (hostPart.StartsWith('['))
        {
            var close = hostPart.IndexOf(']');
            if (close < 2 || !IsIpLiteral(hostPart[1..close])) return null;
            hostEnd = close + 1;
        }
        else
        {
            hostEnd = 0;
            while (hostEnd < hostPart.Length && IsHostChar(hostPart[hostEnd])) hostEnd++;
            while (hostEnd > 0 && hostPart[hostEnd - 1] == '.') hostEnd--; // "see http://host." ends a sentence
            if (hostEnd == 0) return null;
        }
        var portEnd = hostEnd;
        if (portEnd + 1 < hostPart.Length && hostPart[portEnd] == ':' && char.IsAsciiDigit(hostPart[portEnd + 1]))
        {
            portEnd++;
            while (portEnd < hostPart.Length && char.IsAsciiDigit(hostPart[portEnd])) portEnd++;
        }
        var rest = hostPart[portEnd..];
        foreach (var c in rest)
        {
            // Anything but closing punctuation ("host:pa" of a password cut by a space, "host'x") makes the host
            // itself suspect: it may be the user name.
            if (!TrailingPunctuation.Contains(c)) return null;
        }
        suffix = rest.ToString();
        return hostPart[..portEnd].ToString();
    }

    private static bool IsSchemeChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '+' or '.' or '-';

    private static bool IsAuthorityStop(char c) => c is '/' or '\\' or '?' or '#' || char.IsWhiteSpace(c) || HardStops.Contains(c);

    private static bool IsHostChar(char c) => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or '~' or '%';

    /// <summary>IPv6 (optionally with a <c>%zone</c>) or IPvFuture text between the brackets.</summary>
    private static bool IsIpLiteral(ReadOnlySpan<char> literal)
    {
        var zone = literal.IndexOf('%');
        var address = zone >= 0 ? literal[..zone] : literal;
        if (!address.Contains(':') && !(address.Length > 0 && address[0] is 'v' or 'V')) return false;
        foreach (var c in address)
            if (!char.IsAsciiHexDigit(c) && c is not ':' and not '.' and not 'v' and not 'V') return false;
        if (zone < 0) return true;
        foreach (var c in literal[(zone + 1)..])
            if (!char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-' and not '_' and not '~' and not '%') return false;
        return true;
    }

    // ─── Home directory ───

    /// <summary>Replaces the home prefix with <c>~</c> where it is a whole path prefix (<c>/Users/bob</c> is not replaced inside <c>/Users/bobby</c>).</summary>
    private static string ReplaceHomePrefix(string text)
    {
        if (HomePrefix is null) return text;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var index = text.IndexOf(HomePrefix, comparison);
        if (index < 0) return text;
        var output = new StringBuilder(text.Length);
        var copied = 0;
        while (index >= 0)
        {
            var after = index + HomePrefix.Length;
            if (after >= text.Length || !(char.IsLetterOrDigit(text[after]) || text[after] is '.' or '_' or '-'))
            {
                output.Append(text, copied, index - copied).Append('~');
                copied = after;
            }
            index = after < text.Length ? text.IndexOf(HomePrefix, after, comparison) : -1;
        }
        return output.Append(text, copied, text.Length - copied).ToString();
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

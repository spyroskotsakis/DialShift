using System.Diagnostics;
using DialShift.App.Services;
using static DialShift.Tests.TestHarness;

namespace DialShift.Tests.App;

/// <summary>
/// <see cref="StreamUrlRedactor"/>, the one redactor behind the log file and both engines' diagnostics (acceptance matrix
/// §8.2.7; review F4). A table of every known leak shape, each with its exact expected output and the secrets that must
/// not survive, then the same inputs written through a real <see cref="FileAppLog"/> (as messages and as exception
/// chains) to hold README's "The log never contains stream credentials or full private stream URLs."
/// </summary>
public static class RedactionTests
{
    /// <summary>One input, the exact redacted text, and the substrings that must not survive.</summary>
    private sealed record Case(string Name, string Input, string Expected, params string[] Secrets);

    private static readonly Case[] Table =
    [
        // Baseline (CT-LOG-01/02 shapes).
        new("user-info, path, query and fragment", "open https://user:pa55@radio.example.com:8443/live.mp3?token=SECRET#frag failed",
            "open https://radio.example.com:8443/… failed", "user", "pa55", "live.mp3", "SECRET", "frag"),
        new("several URLs in one message", "a http://u1:p1@a.example/x b https://b.example:444/y?z=1 c",
            "a http://a.example/… b https://b.example:444/… c", "u1", "p1", "/x", "/y", "z=1"),
        new("other schemes (rtsp, mms, icyx)", "rtsp://user:pw@cam.example/stream?token=SECRET mms://m.example/mpath?k=1 icyx://i.example:8000/ipath",
            "rtsp://cam.example/… mms://m.example/… icyx://i.example:8000/…", "user", "pw@", "stream", "SECRET", "/mpath", "k=1", "/ipath"),
        // F4a: a URL glued to preceding word characters.
        new("F4a glued to a word (url_http://)", "url_http://user:pa55@radio.example.com/live?token=SECRET",
            "url_http://radio.example.com/…", "user:", "pa55", "live", "SECRET"),
        new("F4a glued to letters and digits (x1http://)", "x1http://user:pa55@h.example/p?token=SECRET", "x1http://h.example/…", "pa55", "/p", "SECRET"),
        new("F4a glued after a colon and non-ASCII", "étiquette:http://user:pa55@h.example/p?token=SECRET", "étiquette:http://h.example/…", "pa55", "/p", "SECRET"),
        // F4b: an apostrophe in the user-info.
        new("F4b apostrophe in the password", "http://user:pa'ss@h.example/p?token=SECRET", "http://h.example/…", "user", "pa'", "'ss", "/p", "SECRET"),
        // F4c: a URL broken by whitespace.
        new("F4c space in the path", "http://h.example/my stream?token=SECRET", "http://h.example/…", "/my", "stream", "SECRET"),
        new("F4c newline in the path", "http://h.example/long/pa\nth/more?token=SECRET", "http://h.example/…", "long", "th/more", "SECRET"),
        new("F4c space in the password", "http://user:pa ss@h.example/p?token=SECRET", "http://[redacted]/…", "user", "ss@", "/p", "SECRET"),
        new("F4c ... the next token that is not a continuation is kept", "http://h.example/my stream?token=SECRET failed (404)",
            "http://h.example/… failed (404)", "stream", "SECRET"),
        // F4d: IPv6 hosts.
        new("F4d IPv6 literal", "http://[::1]:8000/secret/path?token=SECRET", "http://[::1]:8000/…", "secret/path", "SECRET"),
        new("F4d IPv6 literal with user-info", "http://user:pw@[2001:db8::1]:8000/secret/path?token=SECRET",
            "http://[2001:db8::1]:8000/…", "user", "pw@", "secret/path", "SECRET"),
        new("F4d unterminated IPv6 literal", "bad http://[::1/secret?token=abc end", "bad http://[redacted]/… end", "secret", "abc"),
        // F4e: scheme-less and JSON-escaped URLs.
        new("F4e scheme-less //user:pw@host", "//user:pw@h.example/p?token=SECRET", "//h.example/…", "user", "pw@", "/p", "SECRET"),
        new("F4e scheme-less after a space", "fetch //user:pw@h.example/p?token=SECRET", "fetch //h.example/…", "user", "pw@", "/p", "SECRET"),
        new("F4e JSON-escaped https:\\/\\/", @"{""u"":""https:\/\/user:pw@h.example\/p?token=SECRET""}", @"{""u"":""https://h.example/…""}",
            "user", "pw@", @"\/p", "SECRET"),
        // Encoding and case.
        new("percent-encoded user-info", "http://us%40er:p%3Ass@h.example/p", "http://h.example/…", "us%40er", "p%3Ass", "/p"),
        new("uppercase scheme and query", "HTTP://USER:PW@H.EXAMPLE/P?TOKEN=SECRET", "HTTP://H.EXAMPLE/…", "USER", "PW@", "/P", "SECRET"),
        new("'/' in the password (the user name is not taken for the host)", "http://user:pa/ss@h.example/p", "http://[redacted]/…", "user", "pa/ss", "/p"),
        // User-info without any URL.
        new("user:password@ anywhere, no scheme", "login bob:hunter2@x.example ok", "login x.example ok", "bob", "hunter2"),
        new("... with a %3A colon", "user%3Apass@h.example", "h.example", "user%3A", "3Apass"),
        // Tokens separated from their URL.
        new("detached query glued to a word", "request /radio?token=SECRET&x=1 failed", "request /radio?… failed", "SECRET", "x=1"),
        new("secret-named pairs anywhere", "token=A1 key=B2 x-api-key=C3 access_token=D4 sig=E5 auth=F6 password=G7",
            "token=… key=… x-api-key=… access_token=… sig=… auth=… password=…", "A1", "B2", "C3", "D4", "E5", "F6", "G7"),
        new("Authorization header and Bearer token", "Authorization: Basic dXNlcjpwYXNz next Bearer eyJhbGciOi.xyz done",
            "Authorization: … next Bearer … done", "dXNlcjpwYXNz", "eyJhbGciOi"),
        // Punctuation around URLs stays with the text.
        new("closing punctuation after a URL is kept", "see http://h.example. Then (http://h.example/a)b/secret?token=SECRET) and 'http://h.example'",
            "see http://h.example/…. Then (http://h.example/…) and 'http://h.example/…'", "/a)", "secret", "SECRET"),
        // Must not change.
        new("engine diagnostic fields are kept", "libvlc: EncounteredError; source=http://127.0.0.1:1/… kind=Unknown passed=3 session=4",
            "libvlc: EncounteredError; source=http://127.0.0.1:1/… kind=Unknown passed=3 session=4"),
        new("prose, station names and comments are unchanged", "Groove Salad · 128k // comment and a//b; why? because",
            "Groove Salad · 128k // comment and a//b; why? because"),
    ];

    public static void Run()
    {
        TableCases();
        ExceptionChains();
        Diagnostics();
        EngineSourceFormat();
        LogFileNeverContainsCredentials();
        LinearTime();
    }

    private static void TableCases()
    {
        foreach (var c in Table)
        {
            var actual = StreamUrlRedactor.RedactText(c.Input);
            Check($"F4 {c.Name}: → {actual.Replace("\n", "\\n")}", actual == c.Expected);
            var leaked = c.Secrets.Where(s => actual.Contains(s, StringComparison.Ordinal)).ToList();
            Check($"F4 {c.Name}: no secret survives{(leaked.Count == 0 ? "" : $" (leaked: {string.Join(", ", leaked)})")}", leaked.Count == 0);
            Check($"F4 {c.Name}: redacting twice changes nothing", StreamUrlRedactor.RedactText(actual) == actual);
        }
    }

    // The exception text below includes the stack trace, whose source paths are wherever this checkout was built (for
    // example /private/var/folders/x5/… under macOS's $TMPDIR). Every credential, path and token is therefore a
    // sentinel that cannot occur in a filesystem path or a stack frame, as in CT-LOG (core) HZ-07, and the check scans
    // the whole text for exactly those sentinels.
    private static readonly string[] ChainSentinels =
    [
        "ds-outer-user-r4", "ds-outer-pass-r4", "/ds-outer-path-r4", "DS-OUTER-TOKEN-R4", "ds-outer-frag-r4",
        "ds-inner-user-r4", "ds-inner-pass-r4", "/ds-inner-path-r4", "DS-INNER-SIG-R4",
    ];

    private static void ExceptionChains([System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
    {
        var inner = new HttpRequestException("inner https://ds-inner-user-r4:ds-inner-pass-r4@h2.example/ds-inner-path-r4/live.aac?sig=DS-INNER-SIG-R4");
        var outer = new InvalidOperationException(
            "outer http://ds-outer-user-r4:ds-outer-pass-r4@h1.example/ds-outer-path-r4/live.mp3?token=DS-OUTER-TOKEN-R4#ds-outer-frag-r4", inner);
        string text;
        try { throw outer; }
        catch (InvalidOperationException ex) { text = ex.ToString(); }
        var redacted = StreamUrlRedactor.RedactText(text);
        Check("F4 exception chain: the sentinels cannot occur in this checkout's paths (source, binaries, temp)",
            ChainSentinels.All(s => !sourceFile.Contains(s, StringComparison.OrdinalIgnoreCase)
                && !AppContext.BaseDirectory.Contains(s, StringComparison.OrdinalIgnoreCase)
                && !Path.GetTempPath().Contains(s, StringComparison.OrdinalIgnoreCase)));
        Check("F4 ... every sentinel is in the unredacted exception text", ChainSentinels.All(s => text.Contains(s, StringComparison.Ordinal)));
        Check("F4 exception with an inner exception: both URLs are redacted",
            redacted.Contains("outer http://h1.example/…", StringComparison.Ordinal) && redacted.Contains("inner https://h2.example/…", StringComparison.Ordinal));
        var leaked = ChainSentinels.Where(s => redacted.Contains(s, StringComparison.OrdinalIgnoreCase)).ToList();
        Check($"F4 ... no credential, path or token of either survives{(leaked.Count == 0 ? "" : $" (leaked: {string.Join(", ", leaked)})")}",
            leaked.Count == 0);
        Check("F4 ... the stack trace is kept", redacted.Contains(nameof(ExceptionChains), StringComparison.Ordinal));
    }

    private static void Diagnostics()
    {
        Check("F4 RedactDiagnostic applies the same redaction and folds lines",
            StreamUrlRedactor.RedactDiagnostic("NSURLErrorDomain -1004 \"http://u:p@h.example/p\nath?token=T\"\r\nnext", maxLength: 400)
            == "NSURLErrorDomain -1004 \"http://h.example/…\" next");
        Check("F4 RedactDiagnostic caps the length after redacting",
            StreamUrlRedactor.RedactDiagnostic("x " + new string('y', 50) + " http://u:p@h.example/secret", maxLength: 10) == "x yyyyyyyy…");
        Check("F4 RedactDiagnostic: null and blank text become empty",
            StreamUrlRedactor.RedactDiagnostic(null, 10) == "" && StreamUrlRedactor.RedactDiagnostic(" \n ", 10) == "");
    }

    private static void EngineSourceFormat()
    {
        Check("F4 engines describe a source as scheme://host[:port]/… (IPv6 in brackets)",
            StreamUrlRedactor.RedactUrl(new Uri("http://user:pw@[2001:db8::1]:8000/secret/path?token=S")) == "http://[2001:db8::1]:8000/…");
        Check("F4 ... a null source is described without throwing", StreamUrlRedactor.RedactUrl(null) == "[no-url]");
        Check("F4 ... a host-less URI is not described by its path", StreamUrlRedactor.RedactUrl(new Uri("file:///secret/x.mp3")) == "file://[redacted]/…");
        var diagnostic = "avplayer item: NSURLErrorDomain -1100 \"not found\"; source=" + StreamUrlRedactor.RedactUrl(new Uri("https://u:p@radio.example.com/live?token=T"));
        Check("F4 ... and that form survives the log's second pass unchanged",
            StreamUrlRedactor.RedactText(StreamUrlRedactor.RedactDiagnostic(diagnostic, 400)) == "avplayer item: NSURLErrorDomain -1100 \"not found\"; source=https://radio.example.com/…");
    }

    /// <summary>README:55: every table input written through a real FileAppLog, as a message and as an exception chain.</summary>
    private static void LogFileNeverContainsCredentials()
    {
        using var temp = new TempDirectory("redaction");
        var log = new FileAppLog(Path.Combine(temp.Path, "dialshift.log"));
        foreach (var c in Table)
        {
            log.Info("x.redaction", c.Input);
            log.Warn("x.redaction", "failed", new InvalidOperationException(c.Input, new IOException("inner " + c.Input)));
        }
        var text = File.ReadAllText(log.LogFile);
        // The file is JSON: undo the escaping of '\' and '"' so JSON-escaped inputs are searched as written.
        var unescaped = text.Replace("\\\\", "\\", StringComparison.Ordinal).Replace("\\\"", "\"", StringComparison.Ordinal);
        var leaked = Table.SelectMany(c => c.Secrets).Where(s => unescaped.Contains(s, StringComparison.Ordinal)).Distinct().ToList();
        Check($"README:55 the log file never contains stream credentials or private URL parts ({2 * Table.Length} entries; leaked: [{string.Join(", ", leaked)}])",
            leaked.Count == 0);
    }

    private static void LinearTime()
    {
        string[] inputs =
        [
            new string('a', 200_000) + ":" + new string('b', 200_000),
            string.Concat(Enumerable.Repeat("a:", 100_000)),
            string.Concat(Enumerable.Repeat("http://", 50_000)),
            string.Concat(Enumerable.Repeat("x?", 100_000)) + "=",
            string.Concat(Enumerable.Repeat("token", 40_000)),
            "http://h.example/x" + string.Concat(Enumerable.Repeat(" a/", 100_000)),
        ];
        var watch = Stopwatch.StartNew();
        foreach (var input in inputs) StreamUrlRedactor.RedactText(input);
        watch.Stop();
        Check($"F4 pathological 200–400 KB inputs redact in linear time ({watch.ElapsedMilliseconds} ms, bound 3000 ms)", watch.ElapsedMilliseconds < 3000);
    }
}

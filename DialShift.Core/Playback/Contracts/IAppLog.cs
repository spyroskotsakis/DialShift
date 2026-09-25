namespace DialShift.Core.Playback;

/// <summary>
/// Structured diagnostic log port. Core never touches the filesystem; the App layer implements this as a
/// redacted JSON-lines file log in the canonical per-user data directory (see docs/acceptance-matrix.md, Contracts).
/// </summary>
/// <remarks>
/// <para><c>eventName</c> conventions: lower-case dotted identifiers such as
/// <c>playback.state</c>, <c>playback.failed</c>, <c>wake.recovery</c>, <c>schedule.fired</c>.</para>
/// <para><b>Redaction rule (callers and implementations):</b> never log credentials, URL user-info, query
/// strings, fragments, or full private stream URLs — at most scheme + host (+ port). Station names are allowed.
/// Settings file contents and single-instance pipe payloads are never logged. Implementations re-apply
/// URL redaction to messages and exception text as a second line of defense, and never throw.</para>
/// </remarks>
public interface IAppLog
{
    void Info(string eventName, string message);

    void Warn(string eventName, string message, Exception? ex = null);

    void Error(string eventName, string message, Exception? ex = null);
}

/// <summary>Discards everything. Default for tests and for components constructed without a log.</summary>
public sealed class NullAppLog : IAppLog
{
    public static NullAppLog Instance { get; } = new();

    private NullAppLog() { }

    public void Info(string eventName, string message) { }

    public void Warn(string eventName, string message, Exception? ex = null) { }

    public void Error(string eventName, string message, Exception? ex = null) { }
}

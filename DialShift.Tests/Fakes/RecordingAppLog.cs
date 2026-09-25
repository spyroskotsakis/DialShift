using DialShift.Core.Playback;

namespace DialShift.Tests.Fakes;

public enum AppLogLevel { Info, Warn, Error }

public sealed record AppLogEntry(AppLogLevel Level, string EventName, string Message, Exception? Exception)
{
    /// <summary>Everything this entry would put on disk: event, message and full exception text.</summary>
    public string Text => $"{EventName} {Message} {Exception}";
}

/// <summary>Thread-safe <see cref="IAppLog"/> that keeps every entry for assertions (redaction checks, required events).</summary>
public sealed class RecordingAppLog : IAppLog
{
    private readonly Lock gate = new();
    private readonly List<AppLogEntry> entries = [];

    public IReadOnlyList<AppLogEntry> Entries { get { lock (gate) return [.. entries]; } }

    public void Info(string eventName, string message) => Add(new(AppLogLevel.Info, eventName, message, null));

    public void Warn(string eventName, string message, Exception? ex = null) => Add(new(AppLogLevel.Warn, eventName, message, ex));

    public void Error(string eventName, string message, Exception? ex = null) => Add(new(AppLogLevel.Error, eventName, message, ex));

    public bool HasEvent(string eventName) => Entries.Any(e => e.EventName == eventName);

    /// <summary>
    /// True when no entry (event, message or exception text) contains any of <paramref name="secrets"/>, compared
    /// case-insensitively. Use for credentials, tokens and private URL paths.
    /// </summary>
    public bool NoEntryContains(params string[] secrets) =>
        !Entries.Any(e => secrets.Any(s => e.Text.Contains(s, StringComparison.OrdinalIgnoreCase)));

    private void Add(AppLogEntry entry)
    {
        lock (gate) entries.Add(entry);
    }
}

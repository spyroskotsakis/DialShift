namespace DialShift.Core.Playback;

/// <summary>
/// Optional capability an <see cref="IPlaybackEngine"/> may also implement (e.g. LibVLC "now playing").
/// Callers discover it with a type test; absence is normal (AVPlayer may expose no title) and the
/// coordinator then shows the station tag instead.
/// </summary>
public interface ITrackMetadataProvider
{
    /// <summary>Now-playing title of the current session, or null when unknown. Reset to null on every start/stop.</summary>
    string? CurrentTitle { get; }

    /// <summary>Raised on an arbitrary thread when <see cref="CurrentTitle"/> changes.</summary>
    event EventHandler? MetadataChanged;
}

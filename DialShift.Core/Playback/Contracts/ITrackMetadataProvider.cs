namespace DialShift.Core.Playback;

/// <summary>
/// Optional capability an <see cref="IPlaybackEngine"/> may also implement (e.g. LibVLC "now playing").
/// Callers discover it with a type test. Absence is normal, and the coordinator then shows the station tag
/// instead. Per D26, the macOS AVPlayer adapter does not implement it, because AVPlayer delivers ICY titles
/// reliably only for Shoutcast v2 and never for Icecast or HLS. The Windows LibVLC adapter implements it, but
/// titles arrive only for http:// streams, since LibVLC 3 does not request ICY metadata over https.
/// </summary>
public interface ITrackMetadataProvider
{
    /// <summary>Now-playing title of the current session, or null when unknown. Reset to null on every start/stop. Read from arbitrary threads, so it must be thread-safe.</summary>
    string? CurrentTitle { get; }

    /// <summary>Raised on an arbitrary thread when <see cref="CurrentTitle"/> changes.</summary>
    event EventHandler? MetadataChanged;
}

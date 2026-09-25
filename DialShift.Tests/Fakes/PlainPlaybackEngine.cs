using DialShift.Core.Playback;

namespace DialShift.Tests.Fakes;

/// <summary>
/// Wraps a <see cref="FakePlaybackEngine"/> but deliberately does NOT implement <see cref="ITrackMetadataProvider"/>, like an
/// AVPlayer adapter with no now-playing title (CT-PB-37). Every call and event is forwarded unchanged.
/// </summary>
public sealed class PlainPlaybackEngine : IPlaybackEngine
{
    private readonly FakePlaybackEngine inner;

    public PlainPlaybackEngine(FakePlaybackEngine inner)
    {
        this.inner = inner;
        inner.StateChanged += (_, e) => StateChanged?.Invoke(this, e);
        inner.Failed += (_, e) => Failed?.Invoke(this, e);
    }

    public event EventHandler<PlaybackEngineStateChangedEventArgs>? StateChanged;
    public event EventHandler<PlaybackEngineFailedEventArgs>? Failed;

    public Task StartAsync(StreamSource source, double volume, CancellationToken ct) => inner.StartAsync(source, volume, ct);

    public Task StopAsync(CancellationToken ct) => inner.StopAsync(ct);

    public Task SetVolumeAsync(double volume, CancellationToken ct) => inner.SetVolumeAsync(volume, ct);

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}

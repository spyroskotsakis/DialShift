using DialShift.Core.Playback;

namespace DialShift.App.Services;

/// <summary>
/// Owns the coordinator's 1 s heartbeat and its bounded shutdown (brief 1 §4.1a, decision D18, review CR-02).
/// <para><b>UI thread (D18).</b> <see cref="Start"/> must be called on the UI thread. The loop awaits
/// <see cref="PeriodicTimer.WaitForNextTickAsync"/> and <see cref="IPlaybackCoordinator.OnTickAsync"/> without
/// <c>ConfigureAwait(false)</c>, so every continuation resumes on the captured Avalonia synchronization context: each tick,
/// and therefore every coordinator read of <c>Settings</c>, runs on the UI thread, the same thread that mutates settings.
/// This is a <see cref="PeriodicTimer"/> in the orchestration layer, not a <c>DispatcherTimer</c> in coordinator logic.</para>
/// <para><b>Quit never hangs (CR-02).</b> <see cref="StopAsync"/> cancels the loop, then awaits the coordinator's
/// disposal (which stops the engine) for at most <see cref="DisposeTimeout"/>. When that expires it logs
/// <c>app.quit.dispose_timeout</c> and returns, so the caller shuts down anyway.</para>
/// </summary>
public sealed class PlaybackHost(IPlaybackCoordinator coordinator, IAppLog log)
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(3);

    private readonly CancellationTokenSource stopping = new();
    private Task? loop;

    /// <summary>Starts the heartbeat. UI thread only; idempotent.</summary>
    /// <exception cref="InvalidOperationException">Called without a synchronization context (not on the UI thread).</exception>
    public void Start()
    {
        if (loop != null) return;
        if (SynchronizationContext.Current == null)
            throw new InvalidOperationException("PlaybackHost.Start must run on the UI thread so ticks stay on it (D18).");
        loop = RunAsync(stopping.Token);
    }

    /// <summary>Stops the heartbeat, then disposes the coordinator within <paramref name="disposeTimeout"/> (default <see cref="DisposeTimeout"/>). Returns false on timeout.</summary>
    public async Task<bool> StopAsync(TimeSpan? disposeTimeout = null)
    {
        stopping.Cancel();
        if (loop != null) await loop;

        var timeout = disposeTimeout ?? DisposeTimeout;
        var dispose = coordinator.DisposeAsync().AsTask();
        if (await Task.WhenAny(dispose, Task.Delay(timeout)) != dispose)
        {
            log.Warn("app.quit.dispose_timeout", $"Playback did not stop within {timeout.TotalSeconds:0.#} s; quitting anyway.");
            _ = dispose.ContinueWith(t => log.Error("app.quit.dispose_failed", "Playback disposal failed after the timeout.", t.Exception),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            return false;
        }
        try { await dispose; }
        catch (Exception ex) { log.Error("app.quit.dispose_failed", "Playback disposal failed.", ex); }
        return true;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try { await coordinator.OnTickAsync(cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                catch (Exception ex) { log.Error("playback.tick_failed", "A playback tick failed; the heartbeat continues.", ex); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }
}

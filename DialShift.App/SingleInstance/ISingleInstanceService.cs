namespace DialShift.App.SingleInstance;

public enum SingleInstanceStartResult
{
    /// <summary>This process holds the lock and serves the activation pipe.</summary>
    Primary,

    /// <summary>Another process holds the lock (a sharing or lock violation, nothing else).</summary>
    AlreadyRunning,

    /// <summary>The lock was acquired but the activation pipe could not start; the lock was released (exit code 3).</summary>
    Failed,

    /// <summary>The lock file could not be created or opened for another reason (data folder unusable, read-only, disk
    /// full, access denied); nobody is known to be running (exit code 1).</summary>
    LockFailed,
}

public enum SingleInstanceActivationResult { Activated, Rejected, NoResponse }

/// <summary>
/// Per-user single-instance lock plus activation pipe (acceptance matrix §8.2.4). The lock file is
/// <c>&lt;data&gt;/.single-instance.lock</c> held with <c>FileShare.None</c> for the process lifetime; the pipe
/// uses <c>PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous</c> and the v1 wire protocol of
/// <see cref="SingleInstanceMessage"/>.
/// </summary>
public interface ISingleInstanceService : IAsyncDisposable
{
    /// <summary><c>"DialShift-"</c> + the first 16 lower-hex chars of the SHA-256 of the normalized data directory.</summary>
    string PipeName { get; }

    /// <summary>
    /// Raised on a thread-pool thread when a valid <c>activate</c> message arrives. An activation that arrives while
    /// nobody is subscribed is kept (at most one) and raised for the next subscriber, so a second launch during
    /// startup is not lost.
    /// </summary>
    event EventHandler? ActivationRequested;

    /// <summary>
    /// Takes the lock, then starts the server. Returns <see cref="SingleInstanceStartResult.AlreadyRunning"/> only for a
    /// lock held by another process and <see cref="SingleInstanceStartResult.LockFailed"/> for any other lock error. If
    /// the server fails to start, releases the lock and returns <see cref="SingleInstanceStartResult.Failed"/>.
    /// </summary>
    SingleInstanceStartResult TryStartPrimary();

    /// <summary>Asks the running primary to show its window (1 000 ms connect timeout, 2 000 ms reply timeout).</summary>
    Task<SingleInstanceActivationResult> ActivateExistingAsync(CancellationToken cancellationToken = default);
}

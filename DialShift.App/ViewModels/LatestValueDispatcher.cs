using DialShift.App.Services;

namespace DialShift.App.ViewModels;

/// <summary>
/// Coalesces values pushed from any thread (for example <c>SnapshotChanged</c>) into at most one pending UI-thread
/// callback that applies the latest value. A burst of snapshots therefore costs one UI update, and an older value is
/// never applied after a newer one.
/// <para><see cref="Push"/> never blocks: it only <c>Post</c>s. <c>SnapshotChanged</c> handlers run under the coordinator's
/// notify lock, so a synchronous <c>Invoke</c>/<c>InvokeAsync(...).Wait()</c> there could deadlock with a UI-thread command
/// that is entering the coordinator (acceptance matrix §7.10).</para>
/// </summary>
public sealed class LatestValueDispatcher<T>(IUiDispatcher dispatcher, Action<T> apply) where T : class
{
    private T? latest;
    private int pending;

    public void Push(T value)
    {
        Volatile.Write(ref latest, value);
        if (Interlocked.Exchange(ref pending, 1) == 0) dispatcher.Post(Apply);
    }

    private void Apply()
    {
        Volatile.Write(ref pending, 0);
        if (Volatile.Read(ref latest) is { } value) apply(value);
    }
}

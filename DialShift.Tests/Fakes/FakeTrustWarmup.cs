using System.Collections.Concurrent;
using DialShift.App.Services;

namespace DialShift.Tests.Fakes;

/// <summary>
/// Scriptable <see cref="IStreamTrustWarmup"/> for the LibVLC engine checks (HS-17 LV-12, D100): records every URL it is
/// asked to warm, then runs <see cref="Behavior"/>, which may complete, throw, fault, wait on a gate or never finish.
/// </summary>
public sealed class FakeTrustWarmup : IStreamTrustWarmup
{
    private readonly ConcurrentQueue<Uri> calls = new();
    private int disposed;

    /// <summary>What <see cref="WarmAsync"/> does after recording the call; completes at once by default.</summary>
    public Func<Uri, CancellationToken, Task> Behavior { get; set; } = static (_, _) => Task.CompletedTask;

    /// <summary>Every URL passed to <see cref="WarmAsync"/>, in call order.</summary>
    public IReadOnlyList<Uri> Calls => [.. calls];

    /// <summary>True once <see cref="Dispose"/> ran (the engine owns the warm-up).</summary>
    public bool Disposed => Volatile.Read(ref disposed) != 0;

    public Task WarmAsync(Uri url, CancellationToken ct)
    {
        calls.Enqueue(url);
        return Behavior(url, ct);
    }

    public void Dispose() => Volatile.Write(ref disposed, 1);
}

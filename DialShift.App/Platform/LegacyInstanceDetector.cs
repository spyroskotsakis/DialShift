using System.Globalization;
using System.Net.Sockets;
using System.Runtime.Versioning;
using DialShift.App.SingleInstance;
using DialShift.Core.Playback;

namespace DialShift.App.Platform;

/// <summary>One way of seeing a running older DialShift: what it looks for, and the check itself.</summary>
/// <param name="Description">What is probed, for the log (for example <c>mutex Local\DialShift.App</c>).</param>
/// <param name="IsRunning">True when that older app is running. May throw; see <see cref="LegacyInstanceDetector.Detect"/>.</param>
public sealed record LegacyInstanceProbe(string Description, Func<bool> IsRunning);

/// <summary>
/// Finds a running copy of an older DialShift, whose single-instance mechanism this app's lock file and pipe can't see
/// (sweep S2). Running both would play two streams at once, and the older app rewrites <c>settings.json</c> without the
/// time-zone fields when it quits. <c>Program.Main</c> runs <see cref="Detect"/> before becoming the primary instance and,
/// on a hit, shows the startup-failure dialog with <c>Program.LegacyInstanceRunning</c> and exits 1
/// (<see cref="ExitCodes.StartupFailed"/>).
/// </summary>
/// <remarks>
/// <para>The per-OS probes are chosen in <see cref="PlatformServices"/>. The identifiers come from the older apps'
/// sources:</para>
/// <list type="bullet">
/// <item>Windows, the WPF app (upstream v0.1.0 and v0.2.0, the local legacy build): the named mutex
/// <see cref="WindowsMutexName"/>, held while it runs (<see cref="IsMutexHeld"/>, which only opens it).</item>
/// <item>macOS, the retired DialShift.Mac: the activation pipe <see cref="MacPipeName"/>, a Unix socket at
/// <see cref="UnixPipeSocketPath"/> (<see cref="IsPipeListening"/>). It also locked <c>.single-instance.lock</c> in the
/// same data folder as this app, so while it runs this app would otherwise take it for a copy of itself and fail to
/// activate it. Connecting is the only reliable liveness check (the socket file outlives a crash); the older app answers
/// any connection by showing its window, which helps the user find it.</item>
/// <item>macOS, upstream v0.2.0: <see cref="UpstreamMacLockFileName"/> in the default data folder, locked with
/// <c>FileShare.None</c> while it runs (<see cref="IsLockFileHeld"/>, which never creates it and releases it at once).
/// Its Windows build was the WPF app above.</item>
/// </list>
/// <para>A probe that throws is logged as <c>app.legacy_instance_probe_failed</c> and counts as "not running": an
/// uncertain probe must never keep DialShift from starting. A hit is logged as <c>app.legacy_instance_running</c>.</para>
/// </remarks>
public sealed class LegacyInstanceDetector
{
    public const string WindowsMutexName = @"Local\DialShift.App";
    public const string MacPipeName = "DialShift.App.Pipe";
    public const string UpstreamMacLockFileName = "running.lock";

    /// <summary>The longest wait for the older app's pipe to accept a connection (a Unix socket answers at once).</summary>
    public static readonly TimeSpan PipeProbeTimeout = TimeSpan.FromMilliseconds(500);

    private readonly IAppLog log;

    public LegacyInstanceDetector(IAppLog log, IReadOnlyList<LegacyInstanceProbe> probes)
    {
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        Probes = probes ?? throw new ArgumentNullException(nameof(probes));
    }

    public IReadOnlyList<LegacyInstanceProbe> Probes { get; }

    /// <summary>
    /// Runs the probes in order and returns the description of the first that finds an older DialShift running, or null.
    /// Never throws.
    /// </summary>
    public string? Detect()
    {
        foreach (var probe in Probes)
        {
            bool running;
            try { running = probe.IsRunning(); }
            catch (Exception ex)
            {
                log.Warn("app.legacy_instance_probe_failed", $"Couldn't check for an older DialShift ({probe.Description}); assuming it isn't running.", ex);
                continue;
            }
            if (!running) continue;
            log.Warn("app.legacy_instance_running", $"An older DialShift is running ({probe.Description}); not starting alongside it.");
            return probe.Description;
        }
        return null;
    }

    /// <summary>The Windows probes: the WPF app's mutex.</summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<LegacyInstanceProbe> WindowsProbes() =>
        [new($"mutex {WindowsMutexName}", () => IsMutexHeld(WindowsMutexName))];

    /// <summary>The macOS probes: DialShift.Mac's pipe, then upstream v0.2.0's lock file in <paramref name="defaultDataDirectory"/>.</summary>
    /// <param name="defaultDataDirectory"><c>~/Library/Application Support/DialShift</c>, where both older Mac apps kept their
    /// data (not a <c>DIALSHIFT_DATA_DIR</c> override: the older apps never read it).</param>
    [SupportedOSPlatform("macos")]
    public static IReadOnlyList<LegacyInstanceProbe> MacProbes(string defaultDataDirectory)
    {
        var socket = UnixPipeSocketPath(MacPipeName);
        var lockFile = Path.Combine(defaultDataDirectory, UpstreamMacLockFileName);
        return
        [
            new($"pipe {MacPipeName} ({socket})", () => IsPipeListening(socket, PipeProbeTimeout)),
            new($"lock {lockFile}", () => IsLockFileHeld(lockFile)),
        ];
    }

    /// <summary>True when a named mutex exists, i.e. a running process holds a handle to it. Only opens it; never owns it.</summary>
    [SupportedOSPlatform("windows")]
    public static bool IsMutexHeld(string name)
    {
        try
        {
            if (!Mutex.TryOpenExisting(name, out var mutex)) return false;
            mutex.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // It exists, but was created with a security descriptor that denies us (for example by an elevated copy).
            return true;
        }
    }

    /// <summary>
    /// True when another handle holds <paramref name="path"/> with <c>FileShare.None</c> (on macOS a <c>flock</c>). A missing
    /// file is "not held" and is never created; the probe's own handle is closed at once.
    /// </summary>
    public static bool IsLockFileHeld(string path)
    {
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException ex) when (SingleInstanceService.IsHeldByAnotherProcess(ex))
        {
            return true;
        }
    }

    /// <summary>Where .NET on Unix binds a named pipe: <c>$TMPDIR/CoreFxPipe_&lt;name&gt;</c>.</summary>
    public static string UnixPipeSocketPath(string pipeName) => Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + pipeName);

    /// <summary>
    /// True when something accepts connections on the Unix socket at <paramref name="socketPath"/>. A missing path, a
    /// stale socket file (refused) or anything that isn't a socket is false.
    /// </summary>
    /// <exception cref="TimeoutException">No answer within <paramref name="timeout"/>.</exception>
    [UnsupportedOSPlatform("windows")]
    public static bool IsPipeListening(string socketPath, TimeSpan timeout)
    {
        if (!File.Exists(socketPath)) return false;
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), deadline.Token).AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"{socketPath} didn't answer within {timeout.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms.");
        }
    }
}

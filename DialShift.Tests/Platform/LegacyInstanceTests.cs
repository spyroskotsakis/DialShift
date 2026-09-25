using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.Versioning;
using DialShift.App;
using DialShift.App.Platform;
using DialShift.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using static DialShift.Tests.TestHarness;

namespace DialShift.Tests.Platform;

/// <summary>
/// Sweep S2: <see cref="LegacyInstanceDetector"/> finds a running older DialShift so the new app never plays alongside it.
/// The detector runs against fake probes; each real probe runs against a throw-away mutex, lock file or socket of its own,
/// never the older apps' real identifiers (probing a real older DialShift would bring its window to the front).
/// </summary>
public static class LegacyInstanceTests
{
    public static async Task RunAsync()
    {
        Identifiers();
        Detector();
        LockFile();
        if (OperatingSystem.IsWindows()) Mutex();
        else Skip("S2 win: named mutex probe", "Windows only (runs on windows-latest in CI)");
        if (OperatingSystem.IsMacOS()) await PipeAsync();
        else Skip("S2 mac: Unix socket pipe probe", "macOS only");
        await WiringAsync();
    }

    /// <summary>The identifiers are the older apps' own (upstream v0.1.0/v0.2.0 and the legacy-last-known-good sources).</summary>
    private static void Identifiers()
    {
        Check("S2: the WPF app's mutex is Local\\DialShift.App", LegacyInstanceDetector.WindowsMutexName == @"Local\DialShift.App");
        Check("S2: DialShift.Mac's pipe is DialShift.App.Pipe", LegacyInstanceDetector.MacPipeName == "DialShift.App.Pipe");
        Check("S2: upstream v0.2.0's Mac lock file is running.lock", LegacyInstanceDetector.UpstreamMacLockFileName == "running.lock");
        Check("S2: a pipe's Unix socket is $TMPDIR/CoreFxPipe_<name>",
            LegacyInstanceDetector.UnixPipeSocketPath("DialShift.App.Pipe") == Path.Combine(Path.GetTempPath(), "CoreFxPipe_DialShift.App.Pipe"));
        Check("S2: the pipe probe waits at most 500 ms", LegacyInstanceDetector.PipeProbeTimeout == TimeSpan.FromMilliseconds(500));
    }

    private static void Detector()
    {
        var log = new RecordingAppLog();
        var calls = new List<string>();
        LegacyInstanceProbe Probe(string name, Func<bool> result) => new(name, () => { calls.Add(name); return result(); });

        Check("S2: no probes: nothing found", new LegacyInstanceDetector(log, []).Detect() == null);
        var none = new LegacyInstanceDetector(log, [Probe("a", () => false), Probe("b", () => false)]);
        Check("S2: nothing running: null, every probe asked", none.Detect() == null && calls.SequenceEqual(["a", "b"]));
        Check("S2: ... and nothing is logged", log.Entries.Count == 0);

        calls.Clear();
        var hit = new LegacyInstanceDetector(log, [Probe("a", () => false), Probe("b", () => true), Probe("c", () => true)]);
        Check("S2: the first running probe's description is returned", hit.Detect() == "b");
        Check("S2: ... later probes are not asked", calls.SequenceEqual(["a", "b"]));
        Check("S2: ... and app.legacy_instance_running is logged with it",
            log.Entries.Any(e => e is { EventName: "app.legacy_instance_running", Level: AppLogLevel.Warn } && e.Message.Contains("(b)", StringComparison.Ordinal)));

        var failing = new RecordingAppLog();
        var broken = new LegacyInstanceDetector(failing, [Probe("x", () => throw new IOException("boom")), Probe("y", () => true)]);
        Check("S2: a probe that throws is skipped, the next one still counts", broken.Detect() == "y");
        Check("S2: ... the failure is logged as app.legacy_instance_probe_failed",
            failing.Entries.Any(e => e.EventName == "app.legacy_instance_probe_failed" && e.Exception is IOException));
        var onlyBroken = new LegacyInstanceDetector(new RecordingAppLog(), [Probe("x", () => throw new TimeoutException())]);
        Check("S2: an uncertain probe never blocks startup (null, no throw)", NoThrow(() => onlyBroken.Detect()) && onlyBroken.Detect() == null);
        Check("S2: a null log or probe list is rejected",
            Throws<ArgumentNullException>(() => new LegacyInstanceDetector(null!, [])) &&
            Throws<ArgumentNullException>(() => new LegacyInstanceDetector(new RecordingAppLog(), null!)));
    }

    /// <summary>The upstream v0.2.0 lock probe, against a temp lock file held the way v0.2.0 held it.</summary>
    private static void LockFile()
    {
        using var temp = new TempDirectory("legacy-lock");
        var path = temp.Combine(LegacyInstanceDetector.UpstreamMacLockFileName);
        Check("S2: no lock file: not running", !LegacyInstanceDetector.IsLockFileHeld(path));
        Check("S2: ... and the probe doesn't create it", !File.Exists(path));
        Check("S2: a lock file in a missing folder: not running", !LegacyInstanceDetector.IsLockFileHeld(temp.Combine("missing", "running.lock")));

        File.WriteAllText(path, "");
        Check("S2: a lock file nobody holds (the older app quit or crashed): not running", !LegacyInstanceDetector.IsLockFileHeld(path));
        using (new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Check("S2: a lock file held with FileShare.None (upstream v0.2.0 running): running", LegacyInstanceDetector.IsLockFileHeld(path));
        Check("S2: released again: not running", !LegacyInstanceDetector.IsLockFileHeld(path));
        // The probe closes its own handle at once, so the older app could take the lock right after.
        Check("S2: the probe never keeps the lock", NoThrow(() => new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None).Dispose()));
    }

    [SupportedOSPlatform("windows")]
    private static void Mutex()
    {
        var name = $@"Local\DialShift-Tests-{Guid.NewGuid():N}";
        Check("S2 win: no such mutex: not running", !LegacyInstanceDetector.IsMutexHeld(name));
        using (var held = new System.Threading.Mutex(true, name, out var created))
        {
            Check("S2 win: precondition: the test mutex was created", created);
            Check("S2 win: a mutex held by a running app: running", LegacyInstanceDetector.IsMutexHeld(name));
            held.ReleaseMutex();
        }
        Check("S2 win: once its last handle closes: not running", !LegacyInstanceDetector.IsMutexHeld(name));
    }

    /// <summary>The DialShift.Mac pipe probe, against a real .NET named pipe server and stand-ins for a stale socket.</summary>
    [SupportedOSPlatform("macos")]
    private static async Task PipeAsync()
    {
        // Short: a Unix socket path must fit in 104 bytes.
        var name = "DS-legacy-" + Guid.NewGuid().ToString("N")[..8];
        var socketPath = LegacyInstanceDetector.UnixPipeSocketPath(name);
        var timeout = LegacyInstanceDetector.PipeProbeTimeout;
        Check("S2 mac: no socket file: not running", !LegacyInstanceDetector.IsPipeListening(socketPath, timeout));

        await using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
        {
            Check("S2 mac: .NET binds a named pipe at $TMPDIR/CoreFxPipe_<name> (the path the probe checks)", File.Exists(socketPath));
            var connected = server.WaitForConnectionAsync();
            Check("S2 mac: a listening pipe server (DialShift.Mac running): running", LegacyInstanceDetector.IsPipeListening(socketPath, timeout));
            Check("S2 mac: ... the probe's connection reaches the server", await Task.WhenAny(connected, Task.Delay(TimeSpan.FromSeconds(5))) == connected);
        }
        Check("S2 mac: server gone: not running", !LegacyInstanceDetector.IsPipeListening(socketPath, timeout));

        // Directly in $TMPDIR, like the real socket, to stay within the 104-byte limit.
        var stale = Path.Combine(Path.GetTempPath(), "DS-stale-" + Guid.NewGuid().ToString("N")[..8]);
        var regular = Path.Combine(Path.GetTempPath(), "DS-regular-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using (var bound = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
            {
                bound.Bind(new UnixDomainSocketEndPoint(stale));
                // Bound but not listening: what a crashed app's leftover socket file answers (connection refused).
                Check("S2 mac: a stale socket file (refused): not running", !LegacyInstanceDetector.IsPipeListening(stale, timeout));
            }
            File.WriteAllText(regular, "");
            Check("S2 mac: a regular file at the socket path: not running", !LegacyInstanceDetector.IsPipeListening(regular, timeout));
        }
        finally
        {
            File.Delete(stale);
            File.Delete(regular);
        }
    }

    /// <summary>The composition root registers the probes for this OS, and Program's failure is the one users are promised.</summary>
    private static async Task WiringAsync()
    {
        using var temp = new TempDirectory("legacy-wiring");
        var paths = AppPaths.Resolve(name => name == AppPaths.DataDirectoryOverrideVariable ? temp.Path : null, smokeTest: false);
        await using (var services = AppComposition.BuildServiceProvider(paths))
        {
            var detector = services.GetRequiredService<LegacyInstanceDetector>();
            var descriptions = detector.Probes.Select(p => p.Description).ToList();
            if (OperatingSystem.IsWindows())
                Check("S2 win: the registered detector probes the WPF app's mutex", descriptions.SequenceEqual([@"mutex Local\DialShift.App"]));
            else
                Check("S2 mac: the registered detector probes DialShift.Mac's pipe, then v0.2.0's lock in the default data folder (not the override)",
                    descriptions.Count == 2 &&
                    descriptions[0].StartsWith("pipe DialShift.App.Pipe ", StringComparison.Ordinal) &&
                    descriptions[1] == "lock " + Path.Combine(AppPaths.DefaultDataDirectory(), "running.lock"));
        }

        Check("S2: an older DialShift running is a startup failure with the promised text and exit 1",
            DialShift.App.Program.LegacyInstanceRunning ==
            new StartupFailure("An older DialShift is still running. Quit it from its tray icon, then open DialShift again.", ExitCodes.StartupFailed) &&
            ExitCodes.StartupFailed == 1);
        Check("S2: AppPaths.DefaultDataDirectory is the default Resolve picks",
            AppPaths.Resolve(_ => null, smokeTest: false).DataDirectory == AppPaths.DefaultDataDirectory());
    }
}

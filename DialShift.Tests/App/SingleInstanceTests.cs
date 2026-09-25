using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using DialShift.App;
using DialShift.App.Services;
using DialShift.App.SingleInstance;
using DialShift.Tests.Fakes;
using static DialShift.Tests.TestHarness;

namespace DialShift.Tests.App;

/// <summary>
/// Second-instance protocol (acceptance matrix §7.7 CT-SI-01..13, §8.2.4) against the real
/// <see cref="SingleInstanceService"/>: real lock file, real pipe/Unix socket, raw wire clients, and child processes of
/// this test executable (<c>--si-child</c>, <c>--si-crash-primary</c>) for the process-level checks. Every scenario uses
/// its own temp data directory, so its pipe name is unique.
/// </summary>
public static class SingleInstanceTests
{
    private const PipeOptions RawClientOptions = PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous;
    private const string OkReply = "{\"version\":1,\"status\":\"ok\"}\n";
    private const string RejectedReply = "{\"version\":1,\"status\":\"rejected\"}\n";
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(3);

    public static async Task RunAsync()
    {
        MessageParsing();
        PipeNames();
        await PrimaryAndActivationAsync();
        await RejectedMessagesAsync();
        await OversizeAsync();
        await SlowlorisAsync();
        await DisconnectsAsync();
        await ClientGoneBeforeAcceptAsync();
        await RapidDisconnectsAsync();
        await LockHeldWithoutServerAsync();
        await ForeignServerAsync();
        await DisposeReleasesAsync();
        await StaleSocketAsync();
        await SocketModeAsync();
        await ProcessLevelActivationAsync();
        await CrashedPrimaryRecoveryAsync();
        await ActivationDuringTeardownAsync();
        await ActivationBeforeSubscriberAsync();
        await InvalidUtf8OnTheWireAsync();
        await LockErrorsAsync();
        StartupFailureMapping();
    }

    // ---- F3: activation during the startup window ------------------------------------------------------------

    /// <summary>
    /// The listener starts before the app subscribes (Avalonia initializes in between). An activation in that window is
    /// acknowledged, kept (one, coalesced) and raised for the first subscriber, never dropped.
    /// </summary>
    private static async Task ActivationBeforeSubscriberAsync()
    {
        using var temp = new TempDirectory("si-early");
        var log = new RecordingAppLog();
        await using var primary = new SingleInstanceService(PathsFor(temp.Path), log);
        Check("F3 precondition: primary without a subscriber", primary.TryStartPrimary() == SingleInstanceStartResult.Primary);
        await using var client = new SingleInstanceService(PathsFor(temp.Path), new RecordingAppLog());

        Check("F3 an activation before anyone subscribes is acknowledged", await client.ActivateExistingAsync() == SingleInstanceActivationResult.Activated);
        Check("F3 ... a second one too (they coalesce)", await client.ActivateExistingAsync() == SingleInstanceActivationResult.Activated);
        Check("F3 ... logged as single_instance.activated: queued (not delivered)",
            log.Entries.Count(e => e.EventName == "single_instance.activated" && e.Message.EndsWith("queued until the app subscribes.", StringComparison.Ordinal)) == 2 &&
            !log.Entries.Any(e => e.EventName == "single_instance.activated" && e.Message.EndsWith("delivered.", StringComparison.Ordinal)));

        var first = new ActivationCounter(primary);
        Check("F3 the first subscriber receives the pending activation", await first.WaitForAsync(1, EventWait));
        Check("F3 ... replay logged as single_instance.activation_replayed", log.HasEvent("single_instance.activation_replayed"));
        var second = new ActivationCounter(primary);
        await Task.Delay(300);
        Check("F3 ... exactly once (at most one pending), and a later subscriber gets no replay", first.Count == 1 && second.Count == 0);

        Check("F3 with subscribers an activation is delivered to each", await client.ActivateExistingAsync() == SingleInstanceActivationResult.Activated &&
            await first.WaitForAsync(2, EventWait) && await second.WaitForAsync(1, EventWait));
        Check("F3 ... logged as single_instance.activated: delivered",
            log.Entries.Any(e => e.EventName == "single_instance.activated" && e.Message.EndsWith("delivered.", StringComparison.Ordinal)));

        using var disposedDir = new TempDirectory("si-early-disposed");
        var disposed = new SingleInstanceService(PathsFor(disposedDir.Path), new RecordingAppLog());
        Check("F3 precondition: a second primary", disposed.TryStartPrimary() == SingleInstanceStartResult.Primary);
        await using (var other = new SingleInstanceService(PathsFor(disposedDir.Path), new RecordingAppLog()))
            Check("F3 precondition: an activation is pending", await other.ActivateExistingAsync() == SingleInstanceActivationResult.Activated);
        await disposed.DisposeAsync();
        var late = new ActivationCounter(disposed);
        await Task.Delay(300);
        Check("F3 a disposed service never replays its pending activation", late.Count == 0);
    }

    // ---- F5: undecodable text inside the JSON ---------------------------------------------------------------

    private static async Task InvalidUtf8OnTheWireAsync()
    {
        using var temp = new TempDirectory("si-utf8");
        var log = new RecordingAppLog();
        await using var primary = StartPrimary(temp.Path, log, out var activations);
        byte[] payload = [.. "{\"version\":1,\"command\":\""u8, 0xFF, .. "\"}\n"u8];
        Check("F5 invalid UTF-8 inside the command string is answered rejected (not dropped)", await ExchangeAsync(primary.PipeName, payload) == RejectedReply);
        Check("F5 an escaped lone surrogate as the command is answered rejected",
            await ExchangeAsync(primary.PipeName, "{\"version\":1,\"command\":\"\\ud800\"}\n") == RejectedReply);
        Check("F5 ... logged as single_instance.rejected, never single_instance.connection_error",
            log.Entries.Count(e => e.EventName == "single_instance.rejected") == 2 && !log.HasEvent("single_instance.connection_error"));
        Check("F5 ... and nothing was activated", activations.Count == 0);
    }

    // ---- F6: lock errors other than "held by another process" ------------------------------------------------

    private static async Task LockErrorsAsync()
    {
        using var temp = new TempDirectory("si-lockerr");

        var fileInTheWay = temp.Combine("data-is-a-file");
        await File.WriteAllTextAsync(fileInTheWay, "x");
        await CheckLockFailedAsync("the data folder path is a file", fileInTheWay);

        var lockIsFolder = temp.Combine("lock-is-a-folder");
        Directory.CreateDirectory(Path.Combine(lockIsFolder, ".single-instance.lock"));
        await CheckLockFailedAsync("the lock file path is a folder", lockIsFolder);

        var readOnly = temp.Combine("read-only");
        Directory.CreateDirectory(readOnly);
        if (!OperatingSystem.IsWindows() && TempDirectory.TryMakeReadOnly(readOnly))
        {
            await CheckLockFailedAsync("the data folder is read-only", readOnly);
            File.SetUnixFileMode(readOnly, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        else Skip("F6 read-only data folder gives LockFailed", "chmod 0500 is Unix-only and does not stop root");

        var held = temp.Combine("held");
        Directory.CreateDirectory(held);
        using (new FileStream(PathsFor(held).SingleInstanceLockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            IOException? contention = null;
            try { new FileStream(PathsFor(held).SingleInstanceLockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None).Dispose(); }
            catch (IOException ex) { contention = ex; }
            Console.WriteLine($"  lock contention: {contention?.GetType().Name} HResult=0x{contention?.HResult:X8}");
            Check("F6 a lock held by another handle is recognized as contention (sharing violation / EWOULDBLOCK)",
                contention != null && SingleInstanceService.IsHeldByAnotherProcess(contention));
        }
        Check("F6 other I/O errors are not contention",
            !SingleInstanceService.IsHeldByAnotherProcess(new IOException("disk full", OperatingSystem.IsWindows() ? unchecked((int)0x80070070) : 28)) &&
            !SingleInstanceService.IsHeldByAnotherProcess(new IOException("exists", OperatingSystem.IsWindows() ? unchecked((int)0x800700B7) : 17)) &&
            !SingleInstanceService.IsHeldByAnotherProcess(new IOException("generic")));
    }

    private static async Task CheckLockFailedAsync(string scenario, string dataDirectory)
    {
        var log = new RecordingAppLog();
        await using var service = new SingleInstanceService(PathsFor(dataDirectory), log);
        var result = service.TryStartPrimary();
        Console.WriteLine($"  {scenario}: {result}; {log.Entries.FirstOrDefault(e => e.EventName == "single_instance.lock_failed")?.Exception?.GetType().Name}");
        Check($"F6 {scenario}: LockFailed, not AlreadyRunning", result == SingleInstanceStartResult.LockFailed);
        Check($"F6 {scenario}: ... logged as single_instance.lock_failed, not already_running",
            log.HasEvent("single_instance.lock_failed") && !log.HasEvent("single_instance.already_running"));
    }

    // ---- F7: D29 exit codes for the primary-side start results -----------------------------------------------

    private static void StartupFailureMapping()
    {
        var paths = PathsFor(Path.Combine(Path.GetTempPath(), "DialShift-exitcodes"));
        var channel = DialShift.App.Program.StartupFailureFor(SingleInstanceStartResult.Failed, paths);
        var lockFailed = DialShift.App.Program.StartupFailureFor(SingleInstanceStartResult.LockFailed, paths);
        Check("F7 D29 Primary is not a failure", DialShift.App.Program.StartupFailureFor(SingleInstanceStartResult.Primary, paths) == null);
        Check("F7 D29 lock acquired, channel failed: exit 3 (SingleInstanceFailed)", channel?.ExitCode == ExitCodes.SingleInstanceFailed && channel.ExitCode == 3);
        Check("F7 D29 the lock itself couldn't be opened: exit 1 (StartupFailed), with the data folder in the message",
            lockFailed?.ExitCode == ExitCodes.StartupFailed && lockFailed.ExitCode == 1 && lockFailed.Reason.Contains(paths.DataDirectory, StringComparison.Ordinal));
        Check("F7 ... and it does not blame the activation channel", !lockFailed!.Reason.Contains("activation channel", StringComparison.Ordinal));
        Check("F7 AlreadyRunning is a second launch, not a startup failure",
            Throws<ArgumentOutOfRangeException>(() => DialShift.App.Program.StartupFailureFor(SingleInstanceStartResult.AlreadyRunning, paths)));
    }

    // ---- SI-D1 regression -------------------------------------------------------------------------------------

    /// <summary>
    /// SI-D1 (fixed in e3ceaa2): the listener used to dispose its server (and on Unix the shared listening socket) after
    /// every connection and bind a new one, so a client connecting in between landed in the old socket's backlog and was
    /// dropped: <c>ActivateExistingAsync</c> returned <c>NoResponse</c> although a primary was running. Every activation
    /// that arrives while another connection is being torn down must be served.
    /// </summary>
    private static async Task ActivationDuringTeardownAsync()
    {
        using var temp = new TempDirectory("si-d1");
        await using var primary = StartPrimary(temp.Path, new RecordingAppLog(), out var activations);
        await using var client = new SingleInstanceService(PathsFor(temp.Path), new RecordingAppLog());
        var results = new List<SingleInstanceActivationResult>();
        for (var i = 0; i < 5; i++)
        {
            await using (await ConnectRawAsync(primary.PipeName)) { } // connect, then close at once
            results.Add(await client.ActivateExistingAsync());      // immediately, as a second launch would
        }
        Console.WriteLine("  SI-D1 results: " + string.Join(", ", results));
        Check("SI-D1 an activation arriving while the previous connection is torn down is still served (5 of 5 Activated)",
            results.All(r => r == SingleInstanceActivationResult.Activated) && await activations.WaitForAsync(5, EventWait));
    }

    // ---- CT-SI-04/06 parser (pure) ---------------------------------------------------------------------------

    private static void MessageParsing()
    {
        Check("§8.2.4 ToUtf8Line is exactly {\"version\":1,\"command\":\"activate\"}\\n",
            Encoding.UTF8.GetString(SingleInstanceMessage.Activate.ToUtf8Line()) == "{\"version\":1,\"command\":\"activate\"}\n");
        Check("§8.2.4 TryParse accepts the canonical line", SingleInstanceMessage.TryParse(SingleInstanceMessage.Activate.ToUtf8Line(), out var parsed) && parsed == SingleInstanceMessage.Activate);
        Check("§8.2.4 TryParse accepts the line without its newline and with reordered properties",
            Parses("{\"version\":1,\"command\":\"activate\"}") && Parses("{\"command\":\"activate\",\"version\":1}\n"));
        Check("§8.2.4 TryParse accepts insignificant JSON whitespace", Parses("{ \"version\" : 1 , \"command\" : \"activate\" }\n"));

        string[] rejected =
        [
            "", "\n", "not json\n", "[1]\n", "\"activate\"\n", "null\n",
            "{\"version\":2,\"command\":\"activate\"}\n",
            "{\"version\":1,\"command\":\"quit\"}\n",
            "{\"version\":1,\"command\":\"activate\",\"x\":1}\n",
            "{\"version\":1}\n", "{\"command\":\"activate\"}\n",
            "{\"version\":1,\"version\":1,\"command\":\"activate\"}\n",
            "{\"version\":1,\"command\":\"activate\",\"command\":\"activate\"}\n",
            "{\"Version\":1,\"command\":\"activate\"}\n",
            "{\"version\":1,\"command\":\"Activate\"}\n",
            "{\"version\":\"1\",\"command\":\"activate\"}\n",
            "{\"version\":1.5,\"command\":\"activate\"}\n",
            "{\"version\":1,\"command\":null}\n",
            "{\"version\":1,\"command\":\"activate\"} x\n",
            "{\"version\":1,\"command\":\"activate\"}{}\n",
            "{\"version\":1,\"command\":\"activate\"}\n{\"version\":1,\"command\":\"activate\"}\n",
            "{\"version\":1,\"command\":\"activate\"}\n\n",
            "{/*c*/\"version\":1,\"command\":\"activate\"}\n",
            "{\"version\":1,\"command\":\"activate\"",
        ];
        foreach (var line in rejected)
            Check($"CT-SI-06 TryParse rejects {Printable(line)}", !Parses(line));

        var padded = "{\"version\":1,\"command\":\"activate\"" + new string(' ', SingleInstanceMessage.MaxMessageBytes) + "}";
        Check("CT-SI-05 TryParse rejects a valid object longer than MaxMessageBytes", padded.Length > SingleInstanceMessage.MaxMessageBytes && !Parses(padded + "\n"));
        const string prefix = "{\"version\":1,\"command\":\"activate\"";
        var exact = prefix + new string(' ', SingleInstanceMessage.MaxMessageBytes - prefix.Length - 1) + "}";
        Check("CT-SI-05 TryParse accepts exactly MaxMessageBytes plus the newline", exact.Length == SingleInstanceMessage.MaxMessageBytes && Parses(exact + "\n"));
        Check("CT-SI-06 TryParse rejects invalid UTF-8", !SingleInstanceMessage.TryParse([0x7B, 0xFF, 0xFE, 0x7D, 0x0A], out _));
        byte[][] undecodable =
        [
            [.. "{\"version\":1,\"command\":\""u8, 0xFF, .. "\"}\n"u8],
            [.. "{\"version\":1,\"command\":\"activ"u8, 0xC0, 0xAF, .. "ate\"}\n"u8],
            [.. "{\"version\":1,\"command\":\"activate\",\""u8, 0xED, 0xA0, 0x80, .. "\":1}\n"u8],
            Encoding.UTF8.GetBytes("{\"version\":1,\"command\":\"\\ud800\"}\n"),
            Encoding.UTF8.GetBytes("{\"version\":1,\"command\":\"\\udc00activate\"}\n"),
        ];
        var i = 0;
        foreach (var bytes in undecodable)
        {
            var parsed2 = true;
            Check($"F5 TryParse returns false and never throws for undecodable command text #{++i}",
                NoThrow(() => parsed2 = SingleInstanceMessage.TryParse(bytes, out _)) && !parsed2);
        }
        Check("F5 an escaped but valid command still parses (\\u0061ctivate)", Parses("{\"version\":1,\"command\":\"\\u0061ctivate\"}\n"));
    }

    private static bool Parses(string line) => SingleInstanceMessage.TryParse(Encoding.UTF8.GetBytes(line), out _);

    private static string Printable(string line) =>
        line.Length == 0 ? "(empty)" : line.Replace("\n", "\\n", StringComparison.Ordinal);

    // ---- CT-SI-10 --------------------------------------------------------------------------------------------

    private static void PipeNames()
    {
        using var temp = new TempDirectory("si-names");
        var a = temp.Combine("a");
        var b = temp.Combine("b");
        var name = SingleInstanceService.DerivePipeName(a);
        Check("CT-SI-10 pipe name matches ^DialShift-[0-9a-f]{16}$", Regex.IsMatch(name, "^DialShift-[0-9a-f]{16}$"));
        Check("CT-SI-10 same data dir gives the same pipe name", SingleInstanceService.DerivePipeName(a) == name);
        Check("CT-SI-10 a trailing separator does not change the name", SingleInstanceService.DerivePipeName(a + Path.DirectorySeparatorChar) == name);
        Check("CT-SI-10 a non-normalized path (a/../a) gives the same name",
            SingleInstanceService.DerivePipeName(Path.Combine(temp.Path, "a", "..", "a")) == name);
        Check("CT-SI-10 a different data dir gives a different name", SingleInstanceService.DerivePipeName(b) != name);
        if (OperatingSystem.IsWindows())
            Check("CT-SI-10 Windows: the name ignores path case", SingleInstanceService.DerivePipeName(a.ToUpperInvariant()) == name);
        else
            Check("CT-SI-10 Unix: path case is significant", SingleInstanceService.DerivePipeName(a.ToUpperInvariant()) != name);
        Check("CT-SI-10 blank data dir is rejected", Throws<ArgumentException>(() => SingleInstanceService.DerivePipeName(" ")));
        Check("CT-SI-10 the service exposes the derived name",
            new SingleInstanceService(PathsFor(a), new RecordingAppLog()).PipeName == name);
        if (OperatingSystem.IsWindows())
            Skip("CT-SI-10 Unix socket path fits sun_path (104 bytes)", "Unix only; Windows uses a named pipe");
        else
            Check("CT-SI-10 Unix socket path fits sun_path (< 104 bytes)",
                Encoding.UTF8.GetByteCount(SingleInstanceService.GetUnixSocketPath(name)) < 104);
    }

    // ---- CT-SI-01/02 -----------------------------------------------------------------------------------------

    private static async Task PrimaryAndActivationAsync()
    {
        using var temp = new TempDirectory("si-primary");
        var log1 = new RecordingAppLog();
        var log2 = new RecordingAppLog();
        await using var first = new SingleInstanceService(PathsFor(temp.Path), log1);
        await using var second = new SingleInstanceService(PathsFor(temp.Path), log2);
        var activations = new ActivationCounter(first);

        Check("CT-SI-01 first service becomes Primary", first.TryStartPrimary() == SingleInstanceStartResult.Primary);
        Check("CT-SI-01 the lock file exists in the data dir", File.Exists(Path.Combine(temp.Path, ".single-instance.lock")));
        Check("CT-SI-01 TryStartPrimary is idempotent on the primary", first.TryStartPrimary() == SingleInstanceStartResult.Primary);
        Check("CT-SI-01 single_instance.primary is logged", log1.HasEvent("single_instance.primary"));
        Check("CT-SI-01 second service with the same data dir gets AlreadyRunning", second.TryStartPrimary() == SingleInstanceStartResult.AlreadyRunning);
        Check("CT-SI-01 single_instance.already_running is logged", log2.HasEvent("single_instance.already_running"));

        Check("CT-SI-02 ActivateExistingAsync is acknowledged (Activated)", await second.ActivateExistingAsync() == SingleInstanceActivationResult.Activated);
        Check("CT-SI-02 the primary raises ActivationRequested", await activations.WaitForAsync(1, EventWait));
        await Task.Delay(150);
        Check("CT-SI-02 ActivationRequested fires exactly once per activation", activations.Count == 1);
        Check("CT-SI-02 single_instance.activated / activate_sent are logged",
            log1.HasEvent("single_instance.activated") && log2.HasEvent("single_instance.activate_sent"));

        Check("CT-SI-02 the listener re-arms: a second activation is acknowledged", await second.ActivateExistingAsync() == SingleInstanceActivationResult.Activated);
        Check("CT-SI-02 ... and raises ActivationRequested again", await activations.WaitForAsync(2, EventWait));

        first.ActivationRequested += (_, _) => throw new InvalidOperationException("handler boom");
        Check("CT-SI-02 a throwing ActivationRequested handler does not break the ack", await second.ActivateExistingAsync() == SingleInstanceActivationResult.Activated);
        Check("CT-SI-02 ... the handler failure is logged, and the other handler still ran",
            await WaitUntilAsync(() => log1.HasEvent("single_instance.activation_handler_failed"), EventWait) && await activations.WaitForAsync(3, EventWait));
        Check("CT-SI-02 ... and the listener still serves the next activation", await second.ActivateExistingAsync() == SingleInstanceActivationResult.Activated);

        using (var cancelled = new CancellationTokenSource())
        {
            await cancelled.CancelAsync();
            Check("CT-SI-02 a pre-cancelled ActivateExistingAsync throws OperationCanceledException",
                await ThrowsAsync<OperationCanceledException>(() => second.ActivateExistingAsync(cancelled.Token)));
        }
    }

    // ---- CT-SI-04/06 over the wire ---------------------------------------------------------------------------

    private static async Task RejectedMessagesAsync()
    {
        using var temp = new TempDirectory("si-rejected");
        var log = new RecordingAppLog();
        await using var primary = StartPrimary(temp.Path, log, out var activations);

        Check("CT-SI-04 'not json\\n' is answered rejected", await ExchangeAsync(primary.PipeName, "not json\n") == RejectedReply);
        Check("CT-SI-04 ... single_instance.rejected is logged with the byte count only, never the payload",
            log.Entries.Any(e => e.EventName == "single_instance.rejected" && e.Message.Contains("9 bytes", StringComparison.Ordinal)) &&
            log.NoEntryContains("not json"));
        Check("CT-SI-04 ... and the next valid activate succeeds", await ExchangeAsync(primary.PipeName, SingleInstanceMessage.Activate.ToUtf8Line()) == OkReply);
        Check("CT-SI-04 ... raising ActivationRequested once", await activations.WaitForAsync(1, EventWait));

        string[] wire =
        [
            "{\"version\":2,\"command\":\"activate\"}\n",
            "{\"version\":1,\"command\":\"quit\"}\n",
            "{\"version\":1,\"command\":\"activate\",\"x\":1}\n",
            "{\"version\":1,\"command\":\"activate\"} trailing\n",
            "{\"version\":1,\"command\":\"activate\"}\n{\"version\":1,\"command\":\"quit\"}\n",
            "\n",
        ];
        foreach (var message in wire)
            Check($"CT-SI-06 {Printable(message)} is answered rejected", await ExchangeAsync(primary.PipeName, message) == RejectedReply);
        await Task.Delay(150);
        Check("CT-SI-06 no rejected message raised ActivationRequested", activations.Count == 1);
        Check("CT-SI-06 no payload text reaches the log", log.NoEntryContains("quit", "trailing", "\"x\""));
        Check("CT-SI-06 the listener is still alive after every rejection",
            await ExchangeAsync(primary.PipeName, SingleInstanceMessage.Activate.ToUtf8Line()) == OkReply && await activations.WaitForAsync(2, EventWait));
    }

    // ---- CT-SI-05 --------------------------------------------------------------------------------------------

    private static async Task OversizeAsync()
    {
        using var temp = new TempDirectory("si-oversize");
        var log = new RecordingAppLog();
        await using var primary = StartPrimary(temp.Path, log, out var activations);

        var payload = Enumerable.Repeat((byte)'a', 5000).ToArray();
        Check("CT-SI-05 5 000 bytes without a newline are answered rejected", await ExchangeAsync(primary.PipeName, payload) == RejectedReply);
        var expected = $"{SingleInstanceMessage.MaxMessageBytes + 1} bytes read";
        Check($"CT-SI-05 the server read at most MaxMessageBytes + 1 ({expected})",
            log.Entries.Any(e => e.EventName == "single_instance.rejected" && e.Message.Contains(expected, StringComparison.Ordinal)));

        var bigValid = Encoding.UTF8.GetBytes("{\"version\":1,\"command\":\"activate\"" + new string(' ', 5000) + "}\n");
        Check("CT-SI-05 a valid-looking object padded past the limit is rejected", await ExchangeAsync(primary.PipeName, bigValid) == RejectedReply);
        Check("CT-SI-05 no oversize message activated", activations.Count == 0);
        Check("CT-SI-05 the next client is served", await ExchangeAsync(primary.PipeName, SingleInstanceMessage.Activate.ToUtf8Line()) == OkReply);
    }

    // ---- CT-SI-07 --------------------------------------------------------------------------------------------

    private static async Task SlowlorisAsync()
    {
        using var temp = new TempDirectory("si-slow");
        var log = new RecordingAppLog();
        await using var primary = StartPrimary(temp.Path, log, out var activations);
        var readTimeout = SingleInstanceService.ReadTimeout;
        Check("§8.2.4 read timeout is 2 000 ms, connect 1 000 ms, reply 2 000 ms",
            readTimeout == TimeSpan.FromMilliseconds(2000) && SingleInstanceService.ConnectTimeout == TimeSpan.FromMilliseconds(1000) &&
            SingleInstanceService.ReplyTimeout == TimeSpan.FromMilliseconds(2000));

        foreach (var (label, bytes) in new[] { ("sends nothing", Array.Empty<byte>()), ("sends a partial line and stalls", "{\"version\":1"u8.ToArray()) })
        {
            await using var client = await ConnectRawAsync(primary.PipeName);
            var clock = Stopwatch.StartNew();
            if (bytes.Length > 0) { await client.WriteAsync(bytes); await client.FlushAsync(); }
            var reply = await ReadReplyAsync(client, TimeSpan.FromSeconds(6));
            var elapsed = clock.Elapsed;
            Console.WriteLine($"  slowloris ({label}): closed after {elapsed.TotalMilliseconds:F0} ms, reply '{reply}'");
            Check($"CT-SI-07 a client that {label} is closed without a reply", reply == "");
            Check($"CT-SI-07 ... at the 2 s read timeout (1.8 s <= t <= 3.5 s, was {elapsed.TotalMilliseconds:F0} ms)",
                elapsed >= readTimeout - TimeSpan.FromMilliseconds(200) && elapsed <= readTimeout + TimeSpan.FromMilliseconds(1500));
            Check($"CT-SI-07 ... the next client is served", await ExchangeAsync(primary.PipeName, SingleInstanceMessage.Activate.ToUtf8Line()) == OkReply);
        }
        Check("CT-SI-07 timeouts are logged as single_instance.rejected",
            log.Entries.Count(e => e.EventName == "single_instance.rejected" && e.Message.Contains("read timeout", StringComparison.Ordinal)) == 2);
        Check("CT-SI-07 only the two valid clients activated", await activations.WaitForAsync(2, EventWait) && activations.Count == 2);
    }

    // ---- CT-SI-13 --------------------------------------------------------------------------------------------

    private static async Task DisconnectsAsync()
    {
        using var temp = new TempDirectory("si-disconnect");
        var log = new RecordingAppLog();
        await using var primary = StartPrimary(temp.Path, log, out var activations);

        await using (var client = await ConnectRawAsync(primary.PipeName)) { }
        Check("CT-SI-13 connect-then-close: the next client is served", await ExchangeAsync(primary.PipeName, SingleInstanceMessage.Activate.ToUtf8Line()) == OkReply);

        await using (var client = await ConnectRawAsync(primary.PipeName))
        {
            await client.WriteAsync("{\"version\":1,\"comm"u8.ToArray());
            await client.FlushAsync();
        }
        Check("CT-SI-13 disconnect mid-message: the next client is served", await ExchangeAsync(primary.PipeName, SingleInstanceMessage.Activate.ToUtf8Line()) == OkReply);
        Check("CT-SI-13 ... the half message did not activate", await activations.WaitForAsync(2, EventWait) && activations.Count == 2);

        await using (var client = await ConnectRawAsync(primary.PipeName))
        {
            await client.WriteAsync(SingleInstanceMessage.Activate.ToUtf8Line());
            await client.FlushAsync();
        }
        Check("CT-SI-13 disconnect before the reply: the activation still happens", await activations.WaitForAsync(3, EventWait));
        Check("CT-SI-13 ... and the next client is served", await ExchangeAsync(primary.PipeName, SingleInstanceMessage.Activate.ToUtf8Line()) == OkReply);
        Console.WriteLine($"  CT-SI-13 clients gone before accept: {ClientsGoneBeforeAccept(log)}");
        Check("CT-SI-13 no listener error back-off was needed" + ListenerErrors(log), !log.HasEvent("single_instance.listener_error"));
    }

    /// <summary>
    /// Both handler slots are held by stalled clients, so the armed instance sits idle until a slot frees. A client
    /// connects to it and leaves. On Windows that client is connected to the instance at once, and the later
    /// <c>ConnectNamedPipe</c> fails with <c>ERROR_NO_DATA</c>. On Unix it waits in the backlog and is accepted as an
    /// empty connection. The stalled clients keep their handles after the server drops them. On Windows those closed
    /// instances can still count against the OS instance limit, which is why that limit is not the service's own bound.
    /// Neither case may cost a listener back-off, and the next activation must be served.
    /// </summary>
    private static async Task ClientGoneBeforeAcceptAsync()
    {
        using var temp = new TempDirectory("si-gone");
        var log = new RecordingAppLog();
        await using var primary = StartPrimary(temp.Path, log, out var activations);

        await using var stalled1 = await ConnectRawAsync(primary.PipeName);
        await using var stalled2 = await ConnectRawAsync(primary.PipeName);
        await using (await ConnectRawAsync(primary.PipeName)) { } // onto the idle armed instance, then gone
        var replies = await Task.WhenAll(ReadReplyAsync(stalled1, TimeSpan.FromSeconds(6)), ReadReplyAsync(stalled2, TimeSpan.FromSeconds(6)));
        Check("CT-SI-13 both stalled clients are dropped at the read timeout (their handles stay open)", replies.All(r => r == ""));

        Check("CT-SI-13 a client gone before accept: the next activation is served",
            await ExchangeAsync(primary.PipeName, SingleInstanceMessage.Activate.ToUtf8Line()) == OkReply && await activations.WaitForAsync(1, EventWait));
        Check("CT-SI-13 ... with no listener error back-off" + ListenerErrors(log), !log.HasEvent("single_instance.listener_error"));
        Check($"CT-SI-13 ... and at most MaxServerInstances ({SingleInstanceService.MaxServerInstances}) instances held (peak {primary.PeakServerInstances})",
            primary.PeakServerInstances <= SingleInstanceService.MaxServerInstances);
        if (OperatingSystem.IsWindows())
            Check($"CT-SI-13 Windows: the departed client was discarded as a connection error ({ClientsGoneBeforeAccept(log)} logged)", ClientsGoneBeforeAccept(log) >= 1);
        else
            Check("CT-SI-13 Unix: the departed client was accepted from the backlog and rejected as empty (0 bytes read)",
                log.Entries.Any(e => e.EventName == "single_instance.rejected" && e.Message.Contains("(0 bytes read)", StringComparison.Ordinal)));
    }

    /// <summary>50 rapid connect-then-close and disconnect-before-reply cycles, then a real second launch.</summary>
    private static async Task RapidDisconnectsAsync()
    {
        const int cycles = 50;
        using var temp = new TempDirectory("si-rapid");
        var log = new RecordingAppLog();
        await using var primary = StartPrimary(temp.Path, log, out var activations);

        var clock = Stopwatch.StartNew();
        for (var i = 0; i < cycles; i++)
        {
            await using (await ConnectRawAsync(primary.PipeName)) { }
            await using (var client = await ConnectRawAsync(primary.PipeName))
            {
                await client.WriteAsync(SingleInstanceMessage.Activate.ToUtf8Line());
                await client.FlushAsync();
            }
        }
        var elapsed = clock.Elapsed;

        await using var second = new SingleInstanceService(PathsFor(temp.Path), new RecordingAppLog());
        var before = activations.Count;
        var result = await second.ActivateExistingAsync();
        Console.WriteLine($"  rapid disconnects: {cycles} cycles in {elapsed.TotalMilliseconds:F0} ms; fire-and-forget activations raised so far {before} of {cycles}; " +
            $"clients gone before accept {ClientsGoneBeforeAccept(log)}; peak instances {primary.PeakServerInstances}; then {result}");
        Check($"CT-SI-13 after {cycles} rapid connect-then-close / disconnect-before-reply cycles a second launch is Activated",
            result == SingleInstanceActivationResult.Activated && await activations.WaitForAsync(before + 1, EventWait));
        Check("CT-SI-13 ... with no listener error back-off" + ListenerErrors(log), !log.HasEvent("single_instance.listener_error"));
        Check($"CT-SI-13 ... and at most MaxServerInstances ({SingleInstanceService.MaxServerInstances}) instances held (peak {primary.PeakServerInstances})",
            primary.PeakServerInstances <= SingleInstanceService.MaxServerInstances);
    }

    private static int ClientsGoneBeforeAccept(RecordingAppLog log) =>
        log.Entries.Count(e => e.EventName == "single_instance.connection_error" && e.Message.Contains("before its connection was accepted", StringComparison.Ordinal));

    /// <summary>"" when no <c>single_instance.listener_error</c> was logged; otherwise every one, with its exception type, HRESULT and message.</summary>
    private static string ListenerErrors(RecordingAppLog log)
    {
        var errors = log.Entries.Where(e => e.EventName == "single_instance.listener_error").ToList();
        return errors.Count == 0
            ? ""
            : $" (logged {errors.Count}: " + string.Join(" | ", errors.Select(e =>
                $"{e.Message} {e.Exception?.GetType().FullName} 0x{e.Exception?.HResult:X8}: {e.Exception?.Message}")) + ")";
    }

    // ---- CT-SI-08 --------------------------------------------------------------------------------------------

    private static async Task LockHeldWithoutServerAsync()
    {
        using var temp = new TempDirectory("si-noserver");
        var paths = PathsFor(temp.Path);
        using var foreignLock = new FileStream(paths.SingleInstanceLockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var log = new RecordingAppLog();
        await using var service = new SingleInstanceService(paths, log);

        Check("CT-SI-08 lock held by someone else gives AlreadyRunning", service.TryStartPrimary() == SingleInstanceStartResult.AlreadyRunning);
        var clock = Stopwatch.StartNew();
        var result = await service.ActivateExistingAsync();
        var elapsed = clock.Elapsed;
        Console.WriteLine($"  no-server activation: {result} after {elapsed.TotalMilliseconds:F0} ms");
        Check("CT-SI-08 ActivateExistingAsync returns NoResponse", result == SingleInstanceActivationResult.NoResponse);
        Check($"CT-SI-08 ... within the 1 s connect timeout + 500 ms (was {elapsed.TotalMilliseconds:F0} ms)", elapsed <= TimeSpan.FromMilliseconds(1500));
        Check("CT-SI-08 ... logged as single_instance.activate_failed", log.HasEvent("single_instance.activate_failed"));
    }

    // ---- CT-SI-09 --------------------------------------------------------------------------------------------

    private static async Task ForeignServerAsync()
    {
        using var temp = new TempDirectory("si-foreign");
        var paths = PathsFor(temp.Path);
        var pipeName = SingleInstanceService.DerivePipeName(temp.Path);
        var log = new RecordingAppLog();
        using var stop = new CancellationTokenSource();
        var foreign = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, RawClientOptions);
        var foreignWait = foreign.WaitForConnectionAsync(stop.Token);

        await using (var service = new SingleInstanceService(paths, log))
        {
            Check("CT-SI-09 a foreign server on the pipe name gives Failed", service.TryStartPrimary() == SingleInstanceStartResult.Failed);
            Check("CT-SI-09 ... single_instance.server_failed is logged", log.HasEvent("single_instance.server_failed"));
            Check("CT-SI-09 ... and the lock file was released", CanTakeLock(paths.SingleInstanceLockFile));
        }

        await stop.CancelAsync();
        try { await foreignWait; } catch (Exception) { /* cancelled or connected by the probe */ }
        await foreign.DisposeAsync();

        await using var retry = new SingleInstanceService(paths, new RecordingAppLog());
        Check("CT-SI-09 once the squatter is gone a new primary starts", retry.TryStartPrimary() == SingleInstanceStartResult.Primary);
    }

    // ---- CT-SI-11 --------------------------------------------------------------------------------------------

    private static async Task DisposeReleasesAsync()
    {
        using var temp = new TempDirectory("si-dispose");
        var paths = PathsFor(temp.Path);
        var first = StartPrimary(temp.Path, new RecordingAppLog(), out var firstActivations);
        await first.DisposeAsync();
        Check("CT-SI-11 DisposeAsync releases the lock", CanTakeLock(paths.SingleInstanceLockFile));
        Check("CT-SI-11 DisposeAsync is idempotent", await NoThrowAsync(() => first.DisposeAsync().AsTask()));
        Check("CT-SI-11 a disposed service refuses TryStartPrimary", Throws<ObjectDisposedException>(() => first.TryStartPrimary()));

        await using (var probe = new SingleInstanceService(paths, new RecordingAppLog()))
            Check("CT-SI-11 after dispose nobody answers: NoResponse", await probe.ActivateExistingAsync() == SingleInstanceActivationResult.NoResponse);
        Check("CT-SI-11 a disposed primary never raises ActivationRequested", firstActivations.Count == 0);

        await using var second = NewService(temp.Path, new RecordingAppLog(), out var secondActivations);
        Check("CT-SI-11 a new primary starts in the same process and data dir", second.TryStartPrimary() == SingleInstanceStartResult.Primary);
        await using var client = new SingleInstanceService(paths, new RecordingAppLog());
        Check("CT-SI-11 ... and it answers activation", await client.ActivateExistingAsync() == SingleInstanceActivationResult.Activated && await secondActivations.WaitForAsync(1, EventWait));
    }

    // ---- stale socket (Unix, brief 1 §7.5) -------------------------------------------------------------------

    private static async Task StaleSocketAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip("§8.2.4 stale Unix socket file is recovered", "Unix only; Windows named pipes leave no file behind");
            return;
        }

        using var temp = new TempDirectory("si-stale");
        var pipeName = SingleInstanceService.DerivePipeName(temp.Path);
        var socketPath = SingleInstanceService.GetUnixSocketPath(pipeName);

        // A bound but never-listening socket: connect is refused exactly like a crashed primary's leftover file.
        using (var squatter = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            squatter.Bind(new UnixDomainSocketEndPoint(socketPath));
            Check("§8.2.4 precondition: a dead socket file exists at the pipe path", File.Exists(socketPath));
            var log = new RecordingAppLog();
            await using var primary = NewService(temp.Path, log, out var activations);
            Check("§8.2.4 a stale socket is removed and the primary starts", primary.TryStartPrimary() == SingleInstanceStartResult.Primary);
            Check("§8.2.4 ... single_instance.stale_socket is logged", log.HasEvent("single_instance.stale_socket"));
            await using var client = new SingleInstanceService(PathsFor(temp.Path), new RecordingAppLog());
            Check("§8.2.4 ... and activation works over the new socket",
                await client.ActivateExistingAsync() == SingleInstanceActivationResult.Activated && await activations.WaitForAsync(1, EventWait));
        }

        using (var regularFile = new TempDirectory("si-stale-file"))
        {
            var path = SingleInstanceService.GetUnixSocketPath(SingleInstanceService.DerivePipeName(regularFile.Path));
            await File.WriteAllTextAsync(path, "not a socket");
            try
            {
                await using var primary = NewService(regularFile.Path, new RecordingAppLog(), out _);
                Check("§8.2.4 a regular file squatting the socket path is replaced too", primary.TryStartPrimary() == SingleInstanceStartResult.Primary);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    // ---- CT-SI-12 --------------------------------------------------------------------------------------------

    private static async Task SocketModeAsync()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Skip("CT-SI-12 socket file mode is 0600", OperatingSystem.IsWindows() ? "macOS only; Windows uses a named pipe ACL" : "macOS only");
            return;
        }

        using var temp = new TempDirectory("si-mode");
        var log = new RecordingAppLog();
        await using var primary = StartPrimary(temp.Path, log, out _);
        var socketPath = SingleInstanceService.GetUnixSocketPath(primary.PipeName);
        const UnixFileMode owner = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var mode = File.GetUnixFileMode(socketPath);
        Console.WriteLine($"  socket {socketPath} mode 0{Convert.ToString((int)mode, 8)} after start; service log: {log.Entries.FirstOrDefault(e => e.EventName == "single_instance.socket")?.Message}");
        Check("CT-SI-12 socket file lives under $TMPDIR", socketPath.StartsWith(Path.GetTempPath(), StringComparison.Ordinal));
        Check("CT-SI-12 socket file mode is 0600 after start", mode == owner);
        Check("CT-SI-12 the observed mode is logged once as single_instance.socket", log.Entries.Count(e => e.EventName == "single_instance.socket") == 1);

        await using var client = new SingleInstanceService(PathsFor(temp.Path), new RecordingAppLog());
        Check("CT-SI-12 activation still works with the tightened mode", await client.ActivateExistingAsync() == SingleInstanceActivationResult.Activated);
        Check("CT-SI-12 the re-bound socket is 0600 as well",
            await WaitUntilAsync(() => OperatingSystem.IsMacOS() && File.Exists(socketPath) && File.GetUnixFileMode(socketPath) == owner, TimeSpan.FromSeconds(2)));
        Check("CT-SI-12 single_instance.socket is still logged only once", log.Entries.Count(e => e.EventName == "single_instance.socket") == 1);
    }

    // ---- CT-SI-03 (protocol level) / HS-09 -------------------------------------------------------------------

    private static async Task ProcessLevelActivationAsync()
    {
        using var temp = new TempDirectory("si-process");
        var log = new RecordingAppLog();
        await using var primary = StartPrimary(temp.Path, log, out var activations);

        var child = await RunChildAsync("--si-child", temp.Path, TimeSpan.FromSeconds(20));
        Console.WriteLine($"  --si-child: exit {child.ExitCode} after {child.Elapsed.TotalMilliseconds:F0} ms; {child.Output.Trim()}");
        Check("CT-SI-03 second process exits 0 (Activated)", child.ExitCode == 0);
        Check($"CT-SI-03 ... within 3 s (was {child.Elapsed.TotalMilliseconds:F0} ms)", child.Elapsed <= TimeSpan.FromSeconds(3));
        Check("CT-SI-03 ... and the first process's ActivationRequested fired (activates, not merely exits)", await activations.WaitForAsync(1, EventWait));
        Check("CT-SI-03 ... single_instance.activated is in the primary's log", log.HasEvent("single_instance.activated"));
        await using (var again = new SingleInstanceService(PathsFor(temp.Path), new RecordingAppLog()))
            Check("CT-SI-03 the first process remains primary and keeps serving", await again.ActivateExistingAsync() == SingleInstanceActivationResult.Activated);

        using var noServer = new TempDirectory("si-process-noserver");
        using (new FileStream(PathsFor(noServer.Path).SingleInstanceLockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var orphan = await RunChildAsync("--si-child", noServer.Path, TimeSpan.FromSeconds(20));
            Console.WriteLine($"  --si-child (lock held, no server): exit {orphan.ExitCode}; {orphan.Output.Trim()}");
            Check("§8.2.4 exit code 2 when the running instance gives NoResponse", orphan.ExitCode == 2);
        }
    }

    /// <summary>A primary process dies without disposing: the OS drops its lock, and a new primary recovers.</summary>
    private static async Task CrashedPrimaryRecoveryAsync()
    {
        using var temp = new TempDirectory("si-crash");
        var crashed = await RunChildAsync("--si-crash-primary", temp.Path, TimeSpan.FromSeconds(20));
        Console.WriteLine($"  --si-crash-primary: exit {crashed.ExitCode}; {crashed.Output.Trim()}");
        Check("CT-SI-03 precondition: the child became primary and exited without cleanup", crashed.ExitCode == 0);

        var pipeName = SingleInstanceService.DerivePipeName(temp.Path);
        if (!OperatingSystem.IsWindows())
            Check("§8.2.4 the crashed primary left its socket file behind", File.Exists(SingleInstanceService.GetUnixSocketPath(pipeName)));

        var log = new RecordingAppLog();
        await using var primary = NewService(temp.Path, log, out var activations);
        Check("CT-SI-01 after a primary crash the next launch becomes Primary (OS released the lock)", primary.TryStartPrimary() == SingleInstanceStartResult.Primary);
        if (!OperatingSystem.IsWindows())
            Check("§8.2.4 ... after removing the leftover socket (single_instance.stale_socket)", log.HasEvent("single_instance.stale_socket"));
        var child = await RunChildAsync("--si-child", temp.Path, TimeSpan.FromSeconds(20));
        Check("CT-SI-03 ... and a later second launch activates it", child.ExitCode == 0 && await activations.WaitForAsync(1, EventWait));
    }

    // ---- child-process modes ---------------------------------------------------------------------------------

    /// <summary>
    /// Entry point for <c>DialShift.Tests --si-child &lt;dataDir&gt;</c> (a second instance: exit 0 Activated, 1
    /// LockFailed, 2 Rejected/NoResponse, 3 Failed, 4 unexpectedly became primary) and <c>--si-crash-primary &lt;dataDir&gt;</c> (become
    /// primary, then exit 0 without disposing, like a crash; 3 when it could not become primary).
    /// </summary>
    public static async Task<int> RunChildAsync(string[] args)
    {
        if (args.Length != 2 || !Path.IsPathFullyQualified(args[1]))
        {
            Console.Error.WriteLine("Usage: DialShift.Tests --si-child|--si-crash-primary <absolute data dir>");
            return 64;
        }

        var paths = PathsFor(args[1]);
        var log = new FileAppLog(Path.Combine(args[1], "child.log"));
        var service = new SingleInstanceService(paths, log);
        var start = service.TryStartPrimary();
        Console.WriteLine($"start={start}");

        if (args[0] == "--si-crash-primary")
        {
            if (start != SingleInstanceStartResult.Primary) return 3;
            Console.Out.Flush();
            Environment.Exit(0); // no DisposeAsync: the lock and socket are left to the OS, as after a crash
        }

        if (args[0] != "--si-child") return 64;
        await using (service)
        {
            switch (start)
            {
                case SingleInstanceStartResult.Primary:
                    return 4;
                case SingleInstanceStartResult.Failed:
                    return 3;
                case SingleInstanceStartResult.LockFailed:
                    return 1;
            }
            var result = await service.ActivateExistingAsync();
            Console.WriteLine($"activation={result}");
            return result == SingleInstanceActivationResult.Activated ? 0 : 2;
        }
    }

    private sealed record ChildResult(int ExitCode, TimeSpan Elapsed, string Output);

    private static async Task<ChildResult> RunChildAsync(string mode, string dataDirectory, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        using var process = Process.Start(SelfProcess.StartInfo(mode, dataDirectory)) ?? throw new InvalidOperationException("Couldn't start the child process.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            return new ChildResult(-1, clock.Elapsed, "timed out; " + await stdout + await stderr);
        }
        var elapsed = clock.Elapsed;
        return new ChildResult(process.ExitCode, elapsed, (await stdout + await stderr).Replace(Environment.NewLine, " ", StringComparison.Ordinal));
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    private static AppPaths PathsFor(string dataDirectory) =>
        AppPaths.Resolve(name => name == AppPaths.DataDirectoryOverrideVariable ? dataDirectory : null, smokeTest: false);

    private static SingleInstanceService NewService(string dataDirectory, RecordingAppLog log, out ActivationCounter activations)
    {
        var service = new SingleInstanceService(PathsFor(dataDirectory), log);
        activations = new ActivationCounter(service);
        return service;
    }

    /// <summary>A started primary; a start failure is a broken precondition of the scenario, reported with the service log.</summary>
    private static SingleInstanceService StartPrimary(string dataDirectory, RecordingAppLog log, out ActivationCounter activations)
    {
        var service = NewService(dataDirectory, log, out activations);
        var result = service.TryStartPrimary();
        if (result != SingleInstanceStartResult.Primary)
            throw new CheckFailedException($"precondition: TryStartPrimary returned {result}; log: {string.Join(" | ", log.Entries.Select(e => e.Text))}");
        return service;
    }

    private static bool CanTakeLock(string lockFile)
    {
        try
        {
            using var stream = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static async Task<NamedPipeClientStream> ConnectRawAsync(string pipeName)
    {
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, RawClientOptions);
        await client.ConnectAsync(2000);
        return client;
    }

    private static Task<string?> ExchangeAsync(string pipeName, string message) => ExchangeAsync(pipeName, Encoding.UTF8.GetBytes(message));

    /// <summary>Raw client: connect, write <paramref name="payload"/> while reading the one-line reply. Returns "" on EOF, null on timeout.</summary>
    private static async Task<string?> ExchangeAsync(string pipeName, byte[] payload)
    {
        await using var client = await ConnectRawAsync(pipeName);
        var write = WriteIgnoringDisconnectAsync(client, payload);
        var reply = await ReadReplyAsync(client, TimeSpan.FromSeconds(5));
        await write;
        return reply;
    }

    private static async Task WriteIgnoringDisconnectAsync(Stream stream, byte[] payload)
    {
        try
        {
            await stream.WriteAsync(payload);
            await stream.FlushAsync();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The server may reply and close before it has read everything (oversize input).
        }
    }

    private static async Task<string?> ReadReplyAsync(Stream stream, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        var buffer = new byte[256];
        var total = 0;
        try
        {
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total), deadline.Token);
                if (read == 0) break;
                total += read;
                if (buffer[total - 1] == (byte)'\n') break;
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (IOException)
        {
            // A reset connection reads as end of stream.
        }
        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > timeout) return false;
            await Task.Delay(20);
        }
        return true;
    }

    /// <summary>Counts <see cref="ISingleInstanceService.ActivationRequested"/> raises (thread-pool thread) and lets a check await a count.</summary>
    private sealed class ActivationCounter
    {
        private int count;

        public ActivationCounter(ISingleInstanceService service) => service.ActivationRequested += (_, _) => Interlocked.Increment(ref count);

        public int Count => Volatile.Read(ref count);

        public Task<bool> WaitForAsync(int expected, TimeSpan timeout) => WaitUntilAsync(() => Count >= expected, timeout);
    }
}

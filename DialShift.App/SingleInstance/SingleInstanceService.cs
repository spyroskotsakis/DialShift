using System.Globalization;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using DialShift.Core.Playback;

namespace DialShift.App.SingleInstance;

/// <summary>
/// Lock file plus activation pipe (acceptance matrix §8.2.4, brief 1 §7.5).
/// </summary>
/// <remarks>
/// <para><b>Lock:</b> <see cref="AppPaths.SingleInstanceLockFile"/> in the per-user data directory, opened with
/// <c>FileShare.None</c> and held until <see cref="DisposeAsync"/>. On macOS .NET implements this with
/// <c>flock(LOCK_EX|LOCK_NB)</c>, so the OS releases it if the process dies. Only a sharing/lock violation (Windows)
/// or <c>EWOULDBLOCK</c> (macOS) means <see cref="SingleInstanceStartResult.AlreadyRunning"/>; any other failure to
/// create or open it is <see cref="SingleInstanceStartResult.LockFailed"/> (<see cref="IsHeldByAnotherProcess"/>).</para>
/// <para><b>Startup window:</b> the listener starts before the app subscribes to <see cref="ActivationRequested"/>
/// (the UI toolkit initializes in between). An activation that arrives while there is no subscriber is kept as one
/// pending activation (more coalesce into it) and raised for the next subscriber; it is acknowledged <c>ok</c> and
/// logged as <c>single_instance.activated</c> with "queued", and the replay as <c>single_instance.activation_replayed</c>.
/// A delivered activation is logged as <c>single_instance.activated</c> with "delivered". After dispose nothing is kept
/// or raised: a late message is answered <c>rejected</c>.</para>
/// <para><b>Pipe name:</b> <see cref="DerivePipeName"/>: a hash of the app identity and the normalized data directory,
/// so it is per-user, stable, and never derived from untrusted input.</para>
/// <para><b>Server:</b> <c>PipeOptions.CurrentUserOnly | Asynchronous</c>, byte mode, at most
/// <see cref="MaxServerInstances"/> instances held by this service (the OS-level limit is looser, see
/// <see cref="OsInstanceLimit"/>). The instance that actually binds the name (the first one, and any
/// re-bind after every instance of this service has gone) adds <c>FirstPipeInstance</c>, which refuses to start over a
/// live pipe of the same name (without it, .NET on Unix silently unlinks and re-binds the socket path). Later instances
/// join the bound name without it: on Windows a second <c>FILE_FLAG_FIRST_PIPE_INSTANCE</c> create fails, and on
/// net10.0 Unix it throws <see cref="UnauthorizedAccessException"/> while this process already serves the name
/// (verified on macOS). On Unix a leftover socket file from a crashed primary is detected by a probe connect, removed,
/// and the create is retried once.</para>
/// <para><b>Always listening:</b> the next server instance is created <i>before</i> an accepted connection is handed
/// to its handler, so the name never has zero instances between connections. On Unix every instance shares one
/// ref-counted listening socket, which closes when the last instance is disposed; a client that connected into that
/// gap used to sit in the old socket's backlog and was dropped. On Windows the armed instance lets a client connect at
/// once instead of waiting for a free instance.</para>
/// <para><b>Concurrency:</b> at most <see cref="MaxConcurrentConnections"/> connections are handled at once, each
/// off the accept loop, so a client that connects and stalls (held for at most <see cref="ReadTimeout"/>) can't block
/// a real activation. Further clients wait in the socket backlog (Unix) or on the armed instance (Windows) until a
/// handler finishes.</para>
/// <para><b>Protocol (v1):</b> one UTF-8 line per connection, read with a <see cref="ReadTimeout"/> deadline and at
/// most <see cref="SingleInstanceMessage.MaxMessageBytes"/> + 1 bytes, validated by
/// <see cref="SingleInstanceMessage.TryParse"/> before anything happens, answered with one reply line, then closed.
/// Invalid input is logged as <c>single_instance.rejected</c> (byte count only, never the payload) and is not an app
/// error. The only effect of a valid message is <see cref="ActivationRequested"/>.</para>
/// <para><b>Listener:</b> a failed connection is logged as <c>single_instance.connection_error</c> and only closes that
/// connection. That includes a client that connected and left before it was accepted (Windows <c>ERROR_NO_DATA</c>):
/// its instance is replaced and discarded, with no back-off. Any other accept or create failure is logged as
/// <c>single_instance.listener_error</c> and retried after a 1 s back-off. The listener stops only on dispose, which
/// cancels and awaits every handler and disposes every instance (the last one removes the Unix socket file).</para>
/// <para><b>macOS socket permissions:</b> on <c>net10.0</c> the socket file mode comes from the process umask, not
/// from <c>CurrentUserOnly</c> (the explicit <c>0600</c> is a .NET 11 change). After every server instance is created
/// (a real bind, or a re-check of the shared socket), the actual socket file is inspected and group/other bits are
/// removed if present; the observed mode is logged once as <c>single_instance.socket</c>. Its directory,
/// <c>$TMPDIR</c>, is itself per-user on macOS. <c>CurrentUserOnly</c> additionally checks the peer's uid.</para>
/// </remarks>
public sealed class SingleInstanceService : ISingleInstanceService
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(1000);
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromMilliseconds(2000);
    public static readonly TimeSpan ReplyTimeout = TimeSpan.FromMilliseconds(2000);
    public static readonly TimeSpan ListenerErrorBackoff = TimeSpan.FromSeconds(1);

    /// <summary>Connections handled at the same time; one stalled client can't block a real activation.</summary>
    public const int MaxConcurrentConnections = 2;

    /// <summary>The handled connections plus the one armed instance waiting for the next client.</summary>
    public const int MaxServerInstances = MaxConcurrentConnections + 1;

    /// <summary>
    /// The <c>maxNumberOfServerInstances</c> passed to the OS. This service bounds its own instances to
    /// <see cref="MaxServerInstances"/> through its handler slots. The OS limit is not a safe place to enforce that. On
    /// Windows a pipe instance exists "as long as a server or client process has an open handle" (documented), so an
    /// instance the server has already closed can still count against the limit until its client closes too. A client
    /// that is slow to close after its reply, or a stalled client that keeps its handle after the read timeout, could
    /// then use up a limit of 3, and creating the next armed instance would fail with "All pipe instances are busy". On
    /// Unix, .NET counts only live server objects (and always listens with the maximum backlog), so nothing changes there.
    /// </summary>
    private const int OsInstanceLimit = NamedPipeServerStream.MaxAllowedServerInstances;

    /// <summary>How long the stale-socket probe waits for a live server before treating the file as leftover.</summary>
    private static readonly TimeSpan StaleProbeTimeout = TimeSpan.FromMilliseconds(250);

    private const string PipeIdentity = "com.tsiger.dialshift/single-instance/v1|";
    private const PipeOptions ClientOptions = PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous;
    private const PipeOptions ServerOptions = ClientOptions;
    private const PipeOptions BindingServerOptions = ServerOptions | PipeOptions.FirstPipeInstance;

    private static readonly byte[] OkReply = Encoding.UTF8.GetBytes("{\"version\":1,\"status\":\"ok\"}\n");
    private static readonly byte[] RejectedReply = Encoding.UTF8.GetBytes("{\"version\":1,\"status\":\"rejected\"}\n");

    private readonly AppPaths paths;
    private readonly IAppLog log;
    private readonly Lock gate = new();
    private FileStream? lockFile;
    private CancellationTokenSource? listenerCts;
    private Task? listenerTask;
    private bool disposed;
    private bool socketModeLogged;

    /// <summary>Server instances of this service not yet disposed (guarded by <see cref="gate"/>).</summary>
    private int liveServers;

    /// <summary>The highest <see cref="liveServers"/> so far (guarded by <see cref="gate"/>).</summary>
    private int peakServers;

    public SingleInstanceService(AppPaths paths, IAppLog log)
    {
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        PipeName = DerivePipeName(paths.DataDirectory);
    }

    public string PipeName { get; }

    /// <summary>Subscribers (guarded by <see cref="gate"/>).</summary>
    private EventHandler? activationRequested;

    /// <summary>An activation arrived while nobody was subscribed (guarded by <see cref="gate"/>).</summary>
    private bool activationPending;

    public event EventHandler? ActivationRequested
    {
        add
        {
            if (value == null) return;
            bool replay;
            lock (gate)
            {
                activationRequested += value;
                replay = activationPending && !disposed;
                activationPending = false;
            }
            if (!replay) return;
            log.Info("single_instance.activation_replayed", "Delivered an activation that arrived before the app was ready.");
            Dispatch(value);
        }
        remove
        {
            lock (gate) activationRequested -= value;
        }
    }

    /// <summary>
    /// <c>"DialShift-"</c> + the first 16 lower-hex characters of
    /// SHA-256(UTF-8(<c>"com.tsiger.dialshift/single-instance/v1|"</c> + normalized data directory)).
    /// The directory is <see cref="Path.GetFullPath(string)"/> without a trailing separator, upper-invariant on
    /// Windows. The short name keeps the macOS socket path under the 104-byte <c>sun_path</c> limit.
    /// </summary>
    public static string DerivePipeName(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
        if (OperatingSystem.IsWindows()) normalized = normalized.ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(PipeIdentity + normalized));
        return "DialShift-" + Convert.ToHexStringLower(hash)[..16];
    }

    /// <summary>The Unix-domain socket file .NET binds for <paramref name="pipeName"/> on macOS/Linux.</summary>
    public static string GetUnixSocketPath(string pipeName) =>
        Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + pipeName);

    public SingleInstanceStartResult TryStartPrimary()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (listenerTask != null) return SingleInstanceStartResult.Primary;

            try
            {
                Directory.CreateDirectory(paths.DataDirectory);
                lockFile = new FileStream(paths.SingleInstanceLockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when (IsHeldByAnotherProcess(ex))
            {
                log.Info("single_instance.already_running", "Another DialShift instance holds the lock.");
                return SingleInstanceStartResult.AlreadyRunning;
            }
            catch (Exception ex)
            {
                // Not contention: the data folder or the lock file itself is unusable (a file in the way, read-only
                // volume, disk full, access denied). Nobody is known to be running, so this is a startup failure.
                log.Error("single_instance.lock_failed", $"Couldn't open the single-instance lock at {paths.SingleInstanceLockFile}.", ex);
                return SingleInstanceStartResult.LockFailed;
            }

            NamedPipeServerStream firstServer;
            try
            {
                firstServer = CreateServerWithStaleRecovery();
            }
            catch (Exception ex)
            {
                // Never keep the lock without a working activation pipe: a later launch could then neither
                // become primary nor activate us.
                log.Error("single_instance.server_failed", $"Couldn't start the activation pipe {PipeName}; releasing the lock.", ex);
                lockFile.Dispose();
                lockFile = null;
                return SingleInstanceStartResult.Failed;
            }

            listenerCts = new CancellationTokenSource();
            var token = listenerCts.Token;
            listenerTask = Task.Run(() => ListenAsync(firstServer, token), CancellationToken.None);
            log.Info("single_instance.primary", $"Primary instance; pipe {PipeName}.");
            return SingleInstanceStartResult.Primary;
        }
    }

    // Windows: HRESULT_FROM_WIN32 of ERROR_SHARING_VIOLATION (32) and ERROR_LOCK_VIOLATION (33), which .NET puts in
    // IOException.HResult when another handle holds the file with FileShare.None.
    private const int HResultSharingViolation = unchecked((int)0x80070020);
    private const int HResultLockViolation = unchecked((int)0x80070021);

    // macOS: .NET takes flock(LOCK_EX | LOCK_NB) for FileShare.None and, when it is held, throws IOException with the
    // raw errno as HResult: EWOULDBLOCK (35 on Darwin; verified with net10.0 on macOS 26). Other errors carry their own
    // errno (EEXIST 17 when a file is in the way of the data folder, ENOSPC 28 when the disk is full), and access
    // problems are UnauthorizedAccessException.
    private const int ErrnoWouldBlockDarwin = 35;

    /// <summary>True only for "another handle holds the lock"; every other I/O error is a real failure.</summary>
    internal static bool IsHeldByAnotherProcess(IOException ex) =>
        OperatingSystem.IsWindows()
            ? ex.HResult is HResultSharingViolation or HResultLockViolation
            : OperatingSystem.IsMacOS() && ex.HResult == ErrnoWouldBlockDarwin;

    public async Task<SingleInstanceActivationResult> ActivateExistingAsync(CancellationToken cancellationToken = default)
    {
        var result = await SendActivateAsync(cancellationToken).ConfigureAwait(false);
        if (result == SingleInstanceActivationResult.Activated)
            log.Info("single_instance.activate_sent", "The running instance acknowledged activation.");
        else
            log.Warn("single_instance.activate_failed", $"The running instance did not activate: {result}.");
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        Task? listener;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            listener = listenerTask;
            listenerCts?.Cancel();
        }

        if (listener != null)
        {
            try { await listener.ConfigureAwait(false); }
            catch (Exception ex) { log.Warn("single_instance.listener_stop", "The activation listener stopped with an error.", ex); }
        }

        lock (gate)
        {
            // The listener disposed every server instance before it completed.
            listenerCts?.Dispose();
            listenerCts = null;
            lockFile?.Dispose();
            lockFile = null;
        }
    }

    // ---- client ------------------------------------------------------------------------------------------

    private async Task<SingleInstanceActivationResult> SendActivateAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, ClientOptions);
            try
            {
                await client.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return SingleInstanceActivationResult.NoResponse;
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(ReplyTimeout);
            await client.WriteAsync(SingleInstanceMessage.Activate.ToUtf8Line(), deadline.Token).ConfigureAwait(false);
            await client.FlushAsync(deadline.Token).ConfigureAwait(false);

            var reply = new byte[RejectedReply.Length + 1];
            var length = await ReadLineAsync(client, reply, deadline.Token).ConfigureAwait(false);
            var line = reply.AsSpan(0, length);
            if (line.SequenceEqual(OkReply)) return SingleInstanceActivationResult.Activated;
            if (line.SequenceEqual(RejectedReply)) return SingleInstanceActivationResult.Rejected;
            return SingleInstanceActivationResult.NoResponse;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SingleInstanceActivationResult.NoResponse; // reply deadline
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        {
            log.Warn("single_instance.activate_error", "Couldn't talk to the running instance.", ex);
            return SingleInstanceActivationResult.NoResponse;
        }
    }

    // ---- server ------------------------------------------------------------------------------------------

    /// <summary>
    /// Creates one server instance and counts it in <see cref="liveServers"/>. It uses <c>FirstPipeInstance</c> only if
    /// none of this service's instances is alive, i.e. only when .NET really binds the name. With a live instance,
    /// .NET joins it (on Unix, the same listening socket), and <c>FirstPipeInstance</c> would fail. Every instance must be
    /// released with <see cref="ReleaseServer"/>. Creation and release share <see cref="gate"/>, so "none alive" can't
    /// change while an instance is created.
    /// </summary>
    private NamedPipeServerStream CreateServer()
    {
        lock (gate)
        {
            var options = liveServers == 0 ? BindingServerOptions : ServerOptions;
            var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, OsInstanceLimit, PipeTransmissionMode.Byte, options);
            liveServers++;
            peakServers = Math.Max(peakServers, liveServers);
            if (!OperatingSystem.IsWindows()) ValidateUnixSocket();
            return server;
        }
    }

    /// <summary>The most server instances this service has held at once (never above <see cref="MaxServerInstances"/>).</summary>
    internal int PeakServerInstances
    {
        get { lock (gate) return peakServers; }
    }

    private void ReleaseServer(NamedPipeServerStream server)
    {
        lock (gate)
        {
            server.Dispose();
            liveServers--;
        }
    }

    private NamedPipeServerStream CreateServerWithStaleRecovery()
    {
        try
        {
            return CreateServer();
        }
        catch (Exception ex) when (!OperatingSystem.IsWindows() && ex is IOException or UnauthorizedAccessException)
        {
            // FirstPipeInstance refused because the socket file exists. We hold the lock, so no DialShift primary
            // for this data directory is alive: the file is either a leftover from a crash or a foreign squatter.
            var socketPath = GetUnixSocketPath(PipeName);
            if (!File.Exists(socketPath) || IsServerListening()) throw;
            log.Warn("single_instance.stale_socket", $"Removing a leftover activation socket {socketPath}.");
            File.Delete(socketPath);
            return CreateServer();
        }
    }

    private bool IsServerListening()
    {
        try
        {
            using var probe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, ClientOptions);
            probe.Connect((int)StaleProbeTimeout.TotalMilliseconds);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void ValidateUnixSocket()
    {
        if (OperatingSystem.IsWindows()) return;
        var socketPath = GetUnixSocketPath(PipeName);
        try
        {
            var mode = File.GetUnixFileMode(socketPath);
            const UnixFileMode groupOrOther =
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            var tightened = (mode & groupOrOther) != 0;
            if (tightened) File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            // Checked for every instance (and re-bind), so report the observed mode once per service.
            if (socketModeLogged) return;
            socketModeLogged = true;
            log.Info("single_instance.socket", tightened
                ? $"Activation socket {socketPath} had umask-derived mode {FormatMode(mode)}; set to {FormatMode(File.GetUnixFileMode(socketPath))}."
                : $"Activation socket {socketPath} mode {FormatMode(mode)}.");
        }
        catch (Exception ex)
        {
            // CurrentUserOnly still rejects peers with a different uid; this is defense in depth.
            log.Warn("single_instance.socket_unverified", $"Couldn't verify permissions of {socketPath}.", ex);
        }
    }

    private static string FormatMode(UnixFileMode mode) =>
        "0" + Convert.ToString((int)mode & 0x1FF, 8).PadLeft(3, '0');

    /// <summary>
    /// Accept loop. It owns <paramref name="first"/> and every instance it creates. Each accepted connection goes to
    /// <see cref="ServeConnectionAsync"/>, which runs on its own and releases its instance and handler slot. On exit
    /// (cancellation), it releases the armed instance and awaits every handler, so no instance survives.
    /// </summary>
    /// <remarks>
    /// The loop takes a handler slot <i>before</i> it waits on an instance, and every instance it creates while holding
    /// that slot (the next armed one, or the replacement of one whose client left) exists together with at most
    /// <see cref="MaxConcurrentConnections"/> - 1 other handled instances. So this service never holds more than
    /// <see cref="MaxServerInstances"/> instances (<see cref="PeakServerInstances"/>).
    /// </remarks>
    private async Task ListenAsync(NamedPipeServerStream first, CancellationToken token)
    {
        using var slots = new SemaphoreSlim(MaxConcurrentConnections, MaxConcurrentConnections);
        var handlers = new List<Task>();
        NamedPipeServerStream? listening = first;
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await slots.WaitAsync(token).ConfigureAwait(false);
                    NamedPipeServerStream connected;
                    try
                    {
                        listening ??= CreateServer();
                        if (!await TryAcceptAsync(listening, token).ConfigureAwait(false))
                        {
                            // The instance is dead, not the listener. Replace it BEFORE releasing it, as when arming
                            // (SI-D1): the name stays served, and no FirstPipeInstance re-bind is needed. A failed
                            // replacement is a real listener error (below); the dead instance is released either way.
                            var dead = listening;
                            listening = null;
                            try { listening = CreateServer(); }
                            finally { ReleaseServer(dead); }
                            slots.Release();
                            continue;
                        }
                        connected = listening;
                        listening = null;
                    }
                    catch
                    {
                        slots.Release();
                        throw;
                    }

                    // Arm the next instance BEFORE handing this connection off. While this one is alive the name is
                    // still served, so a client that connects in between is accepted, never dropped (SI-D1).
                    Exception? armFailure = null;
                    try { listening = CreateServer(); }
                    catch (Exception ex) { armFailure = ex; }

                    // One command per connection: the handler closes it and frees its slot.
                    handlers.Add(Task.Run(() => ServeConnectionAsync(connected, slots, token), CancellationToken.None));
                    handlers.RemoveAll(static handler => handler.IsCompleted);

                    if (armFailure != null)
                    {
                        log.Warn("single_instance.listener_error", "Couldn't arm the next activation pipe instance; retrying.", armFailure);
                        await Task.Delay(ListenerErrorBackoff, token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    log.Warn("single_instance.listener_error", "Activation listener error; continuing.", ex);
                    if (listening != null)
                    {
                        ReleaseServer(listening);
                        listening = null;
                    }
                    try { await Task.Delay(ListenerErrorBackoff, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }
        finally
        {
            if (listening != null) ReleaseServer(listening);
            // Handlers observe the same token and never throw.
            await Task.WhenAll(handlers).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits for a client on <paramref name="server"/>. Returns false, logged as <c>single_instance.connection_error</c>,
    /// when the client that connected to it has already left (<see cref="IsClientGone"/>). The instance is then unusable.
    /// Any other failure is thrown.
    /// </summary>
    /// <remarks>
    /// On Windows a client can connect to an instance as soon as it exists, before <c>ConnectNamedPipe</c> is issued
    /// (documented). Every instance has that window: the first one until the listener task runs, an armed one until the
    /// hand-off is done, and longer while both handler slots are busy. If that client closes first,
    /// <c>ConnectNamedPipe</c> fails with <c>ERROR_NO_DATA</c> (documented).
    /// .NET cannot read from such an instance and cannot disconnect it, because it was never marked connected, so the
    /// instance is discarded. A client that connected and is still there gives <c>ERROR_PIPE_CONNECTED</c>, which .NET
    /// already reports as success (verified in the net10.0 Windows <c>System.IO.Pipes</c>). On Unix, accept returns a
    /// connection whose client has left like any other, and the handler reads end of stream.
    /// </remarks>
    private async Task<bool> TryAcceptAsync(NamedPipeServerStream server, CancellationToken token)
    {
        try
        {
            await server.WaitForConnectionAsync(token).ConfigureAwait(false);
            return true;
        }
        catch (IOException ex) when (IsClientGone(ex))
        {
            log.Warn("single_instance.connection_error", "A client disconnected before its connection was accepted; the listener continues.", ex);
            return false;
        }
    }

    // HRESULT_FROM_WIN32 of ERROR_BROKEN_PIPE (109), ERROR_NO_DATA (232) and ERROR_PIPE_NOT_CONNECTED (233): the codes
    // that .NET's Windows pipe reads treat as "the other end is gone". A failed accept carries them in IOException.HResult.
    private const int HResultBrokenPipe = unchecked((int)0x8007006D);
    private const int HResultNoData = unchecked((int)0x800700E8);
    private const int HResultPipeNotConnected = unchecked((int)0x800700E9);

    private static bool IsClientGone(IOException ex) =>
        ex.HResult is HResultBrokenPipe or HResultNoData or HResultPipeNotConnected;

    /// <summary>Handles one connection, then always releases its server instance and its handler slot.</summary>
    private async Task ServeConnectionAsync(NamedPipeServerStream server, SemaphoreSlim slots, CancellationToken token)
    {
        try
        {
            await HandleConnectionAsync(server, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            // A client that resets mid-read and similar failures only affect this connection.
            log.Warn("single_instance.connection_error", "An activation connection failed; the listener continues.", ex);
        }
        finally
        {
            ReleaseServer(server);
            slots.Release();
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(ReadTimeout);

        var buffer = new byte[SingleInstanceMessage.MaxMessageBytes + 1];
        int length;
        try
        {
            length = await ReadLineAsync(server, buffer, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            log.Warn("single_instance.rejected", "Rejected a connection: no complete message within the read timeout.");
            return;
        }

        if (length == 0 || buffer[length - 1] != (byte)'\n' ||
            !SingleInstanceMessage.TryParse(buffer.AsSpan(0, length), out _))
        {
            log.Warn("single_instance.rejected", $"Rejected an invalid activation message ({length.ToString(CultureInfo.InvariantCulture)} bytes read).");
            await TryReplyAsync(server, RejectedReply, deadline.Token).ConfigureAwait(false);
            return;
        }

        switch (DeliverActivation())
        {
            case Delivery.Delivered:
                log.Info("single_instance.activated", "A second launch asked this instance to show its window: delivered.");
                break;
            case Delivery.Queued:
                log.Info("single_instance.activated", "A second launch asked this instance to show its window: queued until the app subscribes.");
                break;
            default:
                log.Warn("single_instance.rejected", "Rejected an activation that arrived while shutting down.");
                await TryReplyAsync(server, RejectedReply, deadline.Token).ConfigureAwait(false);
                return;
        }
        await TryReplyAsync(server, OkReply, deadline.Token).ConfigureAwait(false);
    }

    private async Task TryReplyAsync(NamedPipeServerStream server, byte[] reply, CancellationToken token)
    {
        try
        {
            await server.WriteAsync(reply, token).ConfigureAwait(false);
            await server.FlushAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The client went away or stalled; the activation (if any) already happened.
            log.Warn("single_instance.reply_failed", "Couldn't deliver the reply to the client.", ex);
        }
    }

    private enum Delivery { Delivered, Queued, ShuttingDown }

    /// <summary>Raises <see cref="ActivationRequested"/>, or keeps it pending while nobody is subscribed.</summary>
    private Delivery DeliverActivation()
    {
        EventHandler? handler;
        lock (gate)
        {
            if (disposed) return Delivery.ShuttingDown;
            handler = activationRequested;
            if (handler == null)
            {
                activationPending = true;
                return Delivery.Queued;
            }
        }
        Dispatch(handler);
        return Delivery.Delivered;
    }

    private void Dispatch(EventHandler handler)
    {
        // Off the listener so a slow UI handler can't stall the pipe; handler failures are logged, never fatal.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { handler(this, EventArgs.Empty); }
            catch (Exception ex) { log.Error("single_instance.activation_handler_failed", "The activation handler threw.", ex); }
        });
    }

    /// <summary>
    /// Reads until a '\n' arrives, the stream ends or <paramref name="buffer"/> is full, whichever comes first, and
    /// returns the byte count. Never reads beyond the buffer, so a client can't make the server read more than the
    /// limit. Bytes that arrived after the '\n' in the same read are kept, so a caller requiring the last byte to be
    /// '\n' also rejects trailing content.
    /// </summary>
    private static async Task<int> ReadLineAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), token).ConfigureAwait(false);
            if (read == 0) break;
            var newline = Array.IndexOf(buffer, (byte)'\n', total, read);
            total += read;
            if (newline >= 0) break;
        }
        return total;
    }
}

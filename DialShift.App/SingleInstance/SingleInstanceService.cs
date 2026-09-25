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
/// <c>flock(LOCK_EX|LOCK_NB)</c>, so the OS releases it if the process dies.</para>
/// <para><b>Pipe name:</b> <see cref="DerivePipeName"/>: a hash of the app identity and the normalized data directory,
/// so it is per-user, stable, and never derived from untrusted input.</para>
/// <para><b>Server:</b> <c>PipeOptions.CurrentUserOnly | Asynchronous | FirstPipeInstance</c>, one instance, byte
/// mode. <c>FirstPipeInstance</c> refuses to start over a live pipe of the same name (without it, .NET on Unix
/// silently unlinks and re-binds the socket path). On Unix a leftover socket file from a crashed primary is detected
/// by a probe connect, removed, and the create is retried once.</para>
/// <para><b>Protocol (v1):</b> one UTF-8 line per connection, read with a <see cref="ReadTimeout"/> deadline and at
/// most <see cref="SingleInstanceMessage.MaxMessageBytes"/> + 1 bytes, validated by
/// <see cref="SingleInstanceMessage.TryParse"/> before anything happens, answered with one reply line, then closed.
/// Invalid input is logged as <c>single_instance.rejected</c> (byte count only, never the payload) and is not an app
/// error. The only effect of a valid message is <see cref="ActivationRequested"/>.</para>
/// <para><b>Listener:</b> survives every per-connection exception (logged, 1 s back-off) and stops only on dispose.</para>
/// <para><b>macOS socket permissions:</b> on <c>net10.0</c> the socket file mode comes from the process umask, not
/// from <c>CurrentUserOnly</c> (the explicit <c>0600</c> is a .NET 11 change). After every server instance is created
/// (the socket is re-bound per connection), the actual socket file is inspected and group/other bits are removed if
/// present; the observed mode is logged once as <c>single_instance.socket</c>. Its directory,
/// <c>$TMPDIR</c>, is itself per-user on macOS. <c>CurrentUserOnly</c> additionally checks the peer's uid.</para>
/// </remarks>
public sealed class SingleInstanceService : ISingleInstanceService
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(1000);
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromMilliseconds(2000);
    public static readonly TimeSpan ReplyTimeout = TimeSpan.FromMilliseconds(2000);
    public static readonly TimeSpan ListenerErrorBackoff = TimeSpan.FromSeconds(1);

    /// <summary>How long the stale-socket probe waits for a live server before treating the file as leftover.</summary>
    private static readonly TimeSpan StaleProbeTimeout = TimeSpan.FromMilliseconds(250);

    private const string PipeIdentity = "com.tsiger.dialshift/single-instance/v1|";
    private const PipeOptions ClientOptions = PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous;
    private const PipeOptions ServerOptions = ClientOptions | PipeOptions.FirstPipeInstance;

    private static readonly byte[] OkReply = Encoding.UTF8.GetBytes("{\"version\":1,\"status\":\"ok\"}\n");
    private static readonly byte[] RejectedReply = Encoding.UTF8.GetBytes("{\"version\":1,\"status\":\"rejected\"}\n");

    private readonly AppPaths paths;
    private readonly IAppLog log;
    private readonly Lock gate = new();
    private FileStream? lockFile;
    private NamedPipeServerStream? firstServer;
    private CancellationTokenSource? listenerCts;
    private Task? listenerTask;
    private bool disposed;
    private bool socketModeLogged;

    public SingleInstanceService(AppPaths paths, IAppLog log)
    {
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        PipeName = DerivePipeName(paths.DataDirectory);
    }

    public string PipeName { get; }

    public event EventHandler? ActivationRequested;

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
            catch (IOException)
            {
                // Held by the running primary (sharing violation / flock EWOULDBLOCK).
                log.Info("single_instance.already_running", "Another DialShift instance holds the lock.");
                return SingleInstanceStartResult.AlreadyRunning;
            }
            catch (Exception ex)
            {
                log.Error("single_instance.lock_failed", $"Couldn't open the single-instance lock at {paths.SingleInstanceLockFile}.", ex);
                return SingleInstanceStartResult.Failed;
            }

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
            listenerTask = Task.Run(() => ListenAsync(token), CancellationToken.None);
            log.Info("single_instance.primary", $"Primary instance; pipe {PipeName}.");
            return SingleInstanceStartResult.Primary;
        }
    }

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
            firstServer?.Dispose();
            firstServer = null;
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

    private NamedPipeServerStream CreateServer()
    {
        var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, ServerOptions);
        if (!OperatingSystem.IsWindows()) ValidateUnixSocket();
        return server;
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

            // The socket is re-bound for every connection, so report the observed mode once per service.
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

    private async Task ListenAsync(CancellationToken token)
    {
        NamedPipeServerStream? server;
        lock (gate)
        {
            server = firstServer;
            firstServer = null;
        }

        while (!token.IsCancellationRequested)
        {
            try
            {
                server ??= CreateServer();
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                await HandleConnectionAsync(server, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.Warn("single_instance.listener_error", "Activation listener error; continuing.", ex);
                server?.Dispose();
                server = null;
                try { await Task.Delay(ListenerErrorBackoff, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            // One command per connection: close it and create a fresh instance for the next client.
            server.Dispose();
            server = null;
        }

        server?.Dispose();
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

        log.Info("single_instance.activated", "A second launch asked this instance to show its window.");
        RaiseActivationRequested();
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

    private void RaiseActivationRequested()
    {
        var handler = ActivationRequested;
        if (handler == null) return;
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

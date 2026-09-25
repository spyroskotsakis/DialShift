using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DialShift.Core.Playback;

namespace DialShift.App.Services;

/// <summary>
/// The App's <see cref="IAppLog"/> (acceptance matrix §8.2.7, decision D11): JSON Lines in
/// <c>&lt;DataDirectory&gt;/dialshift.log</c>, one object per line:
/// <c>{"ts":"…","level":"info|warn|error","event":"…","msg":"…","ex":"…"}</c>, UTF-8 without BOM.
/// </summary>
/// <remarks>
/// <para>Message and exception text are passed through <see cref="StreamUrlRedactor.RedactText"/> (URLs reduced to
/// <c>scheme://host[:port]/…</c>, stray user-info and secret-named values removed, home prefix replaced by <c>~</c>).
/// Callers still must not pass settings contents, pipe payloads or HTTP headers.</para>
/// <para>Before a write that would push the file past <see cref="MaxFileBytes"/>, the file is renamed to
/// <c>dialshift.log.1</c>, replacing the previous one. Each write opens, appends and closes the file with
/// <c>FileShare.ReadWrite | FileShare.Delete</c>, so viewers and a second-instance process can open it too.</para>
/// <para><b>Several processes on one file.</b> A second launch logs to the same file while the primary runs.
/// <c>FileMode.Append</c> is not an atomic append in .NET: it finds the end once at open and then writes at that
/// offset. Verified on net10.0/macOS: no <c>O_APPEND</c>, so a line another writer appended after the open is
/// overwritten. On Windows it is a <c>GENERIC_WRITE</c> handle, not an append-only one. Two writers therefore overwrite each other's
/// lines, and two rotations can race (the second renames the file the first just started, losing the first's
/// <c>.1</c>). Each size check, rotation and append therefore runs while this process holds two locks. One is an
/// in-process lock shared by every instance for the same file. The other is a cross-process lock: the lock file
/// <see cref="LockFile"/> opened with <c>FileShare.None</c>, which is <c>flock(LOCK_EX)</c> on Unix and a sharing
/// violation on Windows. The OS releases it if the holder dies. Other approaches were rejected: a P/Invoke
/// <c>O_APPEND</c> writer would make appends atomic but would still leave rotation racing, and a named mutex is
/// thread-affine with platform-specific semantics. The
/// lock file lives in the per-user temp directory, the same place the single-instance socket relies on, because the
/// data directory only holds the files the spec lists (§8.2.6). It is never deleted: deleting a lock file lets two
/// processes lock different inodes.</para>
/// <para><b>Bounded latency.</b> The lock is held only for one stat, an optional rename and one small write. A
/// writer waits at most <see cref="LockWaitBudget"/>. After that it assumes the holder is stuck (for example suspended
/// in a debugger, so not writing) and appends without the cross-process lock and without rotating. If the lock file
/// can't be opened at all (for example a read-only temp directory), no process can coordinate: it writes right away
/// and still rotates, so the file stays bounded.</para>
/// <para>Thread-safe. Never throws: I/O and formatting failures are swallowed.</para>
/// </remarks>
public sealed class FileAppLog : IAppLog
{
    /// <summary>Rotation threshold: 1 MiB.</summary>
    public const long MaxFileBytes = 1024 * 1024;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        // The file is read by people and tools, never embedded in HTML; keep station names readable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The longest a write waits for another process's cross-process log lock.</summary>
    public static readonly TimeSpan LockWaitBudget = TimeSpan.FromMilliseconds(250);

    /// <summary>In-process locks by log file, shared by every instance writing to the same file.</summary>
    private static readonly ConcurrentDictionary<string, Lock> ProcessLocks = new(StringComparer.Ordinal);

    private readonly Lock gate;
    private readonly string rotatedFile;

    public FileAppLog(string logFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logFile);
        LogFile = Path.GetFullPath(logFile);
        rotatedFile = LogFile + ".1";
        var identity = OperatingSystem.IsWindows() ? LogFile.ToUpperInvariant() : LogFile;
        gate = ProcessLocks.GetOrAdd(identity, static _ => new Lock());
        LockFile = Path.Combine(Path.GetTempPath(),
            "DialShift-log-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16] + ".lock");
    }

    public string LogFile { get; }

    /// <summary>
    /// Cross-process lock for <see cref="LogFile"/>: <c>&lt;temp&gt;/DialShift-log-&lt;16 hex of SHA-256(log path)&gt;.lock</c>,
    /// an empty file that is never deleted.
    /// </summary>
    public string LockFile { get; }

    public void Info(string eventName, string message) => Write("info", eventName, message, null);

    public void Warn(string eventName, string message, Exception? ex = null) => Write("warn", eventName, message, ex);

    public void Error(string eventName, string message, Exception? ex = null) => Write("error", eventName, message, ex);

    /// <summary>
    /// Writes the <c>app.start</c> line required by brief 1 §11 DoD: app version, runtime identifier, process
    /// architecture, OS description, the selected playback engine and the data-directory source.
    /// </summary>
    public void LogStartup(string playbackEngineName, DataDirectorySource dataDirectorySource)
    {
        try
        {
            var assembly = typeof(FileAppLog).Assembly;
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                          ?? assembly.GetName().Version?.ToString()
                          ?? "unknown";
            Info("app.start",
                $"version={version} rid={RuntimeInformation.RuntimeIdentifier} arch={RuntimeInformation.ProcessArchitecture} " +
                $"os=\"{RuntimeInformation.OSDescription}\" engine={playbackEngineName} data_dir_source={dataDirectorySource}");
        }
        catch (Exception)
        {
            // Never throws.
        }
    }

    private void Write(string level, string eventName, string message, Exception? ex)
    {
        try
        {
            var line = Format(level, eventName, message, ex);
            lock (gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
                using var crossProcess = TryAcquireCrossProcessLock(out var contended);
                // Rotating while another process holds the lock could rename the file it just rotated to.
                if (!contended) RotateIfNeeded(line.Length);
                using var stream = new FileStream(LogFile, FileMode.Append, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                stream.Write(line);
            }
        }
        catch (Exception)
        {
            // Logging must never take the app down.
        }
    }

    /// <summary>
    /// Opens <see cref="LockFile"/> exclusively. It retries sharing violations (another process is writing) with a
    /// short back-off until <see cref="LockWaitBudget"/> runs out. Returns null when the budget is spent
    /// (<paramref name="contended"/> is true) or when the lock file can't be opened at all (false: no process can
    /// coordinate, so rotation still runs and keeps the file bounded).
    /// </summary>
    private FileStream? TryAcquireCrossProcessLock(out bool contended)
    {
        contended = false;
        var clock = Stopwatch.StartNew();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(LockFile, FileMode.OpenOrCreate, FileAccess.Read, FileShare.None);
            }
            catch (IOException ex) when (ex.GetType() == typeof(IOException))
            {
                // Held by another writer (flock EWOULDBLOCK / ERROR_SHARING_VIOLATION); it holds it for microseconds.
                if (clock.Elapsed >= LockWaitBudget)
                {
                    contended = true;
                    return null;
                }
                if (attempt < 3) Thread.Yield();
                else Thread.Sleep(1);
            }
            catch (Exception)
            {
                // Not a contention (access denied, missing temp directory): waiting won't help.
                return null;
            }
        }
    }

    private void RotateIfNeeded(int incomingBytes)
    {
        var info = new FileInfo(LogFile);
        if (!info.Exists || info.Length + incomingBytes <= MaxFileBytes) return;
        File.Move(LogFile, rotatedFile, overwrite: true);
    }

    private static byte[] Format(string level, string eventName, string message, Exception? ex)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("ts", DateTimeOffset.UtcNow.ToString("O"));
            writer.WriteString("level", level);
            writer.WriteString("event", eventName);
            writer.WriteString("msg", StreamUrlRedactor.RedactText(message));
            if (ex != null) writer.WriteString("ex", StreamUrlRedactor.RedactText(ex.ToString()));
            writer.WriteEndObject();
        }
        buffer.Write("\n"u8);
        return buffer.WrittenSpan.ToArray();
    }
}

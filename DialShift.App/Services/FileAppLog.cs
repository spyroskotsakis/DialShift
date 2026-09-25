using System.Buffers;
using System.Reflection;
using System.Runtime.InteropServices;
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
/// <para>Message and exception text are passed through <see cref="StreamUrlRedactor"/> (URLs reduced to
/// scheme + host + port, home prefix replaced by <c>~</c>). Callers still must not pass settings contents,
/// pipe payloads or HTTP headers.</para>
/// <para>Before a write that would push the file past <see cref="MaxFileBytes"/>, the file is renamed to
/// <c>dialshift.log.1</c>, replacing the previous one. Each write opens, appends and closes the file with
/// <c>FileShare.ReadWrite | FileShare.Delete</c>, so a second-instance process can log to the same file.</para>
/// <para>Thread-safe (one lock). Never throws: I/O and formatting failures are swallowed.</para>
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

    private readonly Lock gate = new();
    private readonly string rotatedFile;

    public FileAppLog(string logFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logFile);
        LogFile = Path.GetFullPath(logFile);
        rotatedFile = LogFile + ".1";
    }

    public string LogFile { get; }

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
                RotateIfNeeded(line.Length);
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

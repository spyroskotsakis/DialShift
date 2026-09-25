using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using DialShift.Core.Playback;

namespace DialShift.Core;

/// <summary>
/// Reads and atomically writes <c>settings.json</c> in the data directory it is given.
/// <para>
/// Recovery (BHV-03): a file that can't be read or is invalid is copied to
/// <c>settings.json.unreadable-&lt;yyyyMMddHHmmssfff&gt;</c> (local time) and defaults are returned with a
/// <see cref="Warning"/>. When that name is taken, <c>-2</c>, <c>-3</c>, … is appended (CF-04). The original is never
/// modified by <see cref="Load"/>, and no exception escapes the recovery path.
/// </para>
/// <para>
/// Data safety: if the copy can't be made (disk full, read-only folder, permissions), the corrupt original must not be
/// lost. <see cref="Load"/> still returns defaults, the warning says no copy exists, and the store remembers it. The
/// next <see cref="Save"/> tries the copy again first: if it succeeds the save goes ahead, otherwise <see cref="Save"/>
/// throws <see cref="IOException"/> and leaves the original untouched. So the original is only ever replaced once a
/// flushed copy of it exists.
/// </para>
/// </summary>
public sealed class SettingsStore(string directory, IClock? clock = null)
{
    /// <summary>Upper bound on backup names tried for one timestamp (the plain name, then <c>-2</c> … <c>-N</c>).</summary>
    private const int MaxBackupNames = 100;

    private readonly IClock clock = clock ?? SystemClock.Instance;

    /// <summary>The last <see cref="Load"/> recovered from an unreadable file but could not preserve a copy of it.</summary>
    private bool backupPending;

    public string DirectoryPath { get; } = directory;
    public string FilePath => Path.Combine(DirectoryPath, "settings.json");

    /// <summary>Set by a <see cref="Load"/> that recovered to defaults; reset at the start of every <see cref="Load"/> (CF-03).</summary>
    public string? Warning { get; private set; }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public Settings Load()
    {
        Warning = null;
        backupPending = false;
        if (!File.Exists(FilePath)) return Settings.Defaults();
        try
        {
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOptions) ?? throw new JsonException("Empty settings.");
            if (settings.Version != 1) throw new JsonException("Unsupported settings version.");
            if (settings.Stations is null || settings.Schedule is null || settings.Stations.Any(s => s is null || string.IsNullOrWhiteSpace(s.Name) || !ValidUrl(s.Url)) || settings.Schedule.Any(e => e is null || e.Days is null))
                throw new JsonException("Invalid settings data.");
            // ScheduleEntry.TimeZone is not validated: any string loads, and an id this computer cannot resolve falls back to
            // local time at evaluation (Scheduler.TryResolveZone), so a stale id never reaches the recovery path below (QA-N3).
            // A non-string value ("TimeZone": 123) is malformed JSON for the model, like any other mistyped field, and does.
            settings.Volume = Math.Clamp(settings.Volume, 0, 100);
            // Slot times are canonicalized in memory (D53): a legacy/culture "08.30" becomes "08:30", so conflicts, sorting
            // and display agree. The file is not rewritten here; the next Save persists it. A time that does not parse is
            // left as it is and is not corruption: that slot is ignored at evaluation, as before.
            foreach (var entry in settings.Schedule)
                if (Scheduler.TryTime(entry.Time, out var time)) entry.Time = Scheduler.FormatTime(time);
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            if (TryPreserve(out var backup, out var failure))
            {
                Warning = $"Your settings could not be read. A copy was preserved at {backup}.";
            }
            else
            {
                backupPending = true;
                Warning = $"Your settings could not be read, and a copy could not be made ({failure.Message}). " +
                          $"DialShift is using default settings and will not replace {FilePath} until a copy of it can be made.";
            }
            return Settings.Defaults();
        }
    }

    /// <exception cref="IOException">
    /// The last <see cref="Load"/> could not preserve the unreadable original and it still can't be copied; nothing was written.
    /// </exception>
    public void Save(Settings settings)
    {
        if (backupPending)
        {
            if (File.Exists(FilePath) && !TryPreserve(out _, out var failure))
                throw new IOException(
                    $"your previous settings file couldn't be backed up ({failure.Message}), so it was left untouched. " +
                    "Free some disk space or check the folder's permissions, then try again.", failure);
            backupPending = false;
        }
        Directory.CreateDirectory(DirectoryPath);
        var temporary = FilePath + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, settings, JsonOptions);
            file.Flush(true);
        }
        File.Move(temporary, FilePath, true);
    }

    public static bool ValidUrl(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.Scheme == "http") && !string.IsNullOrWhiteSpace(uri.Host);

    /// <summary>
    /// Copies <see cref="FilePath"/> to a new, unused <c>.unreadable-*</c> name and flushes it to disk. Never throws: any
    /// failure (including every name being taken) returns false, and a partial copy is removed.
    /// </summary>
    private bool TryPreserve([NotNullWhen(true)] out string? backup, [NotNullWhen(false)] out Exception? failure)
    {
        backup = null;
        failure = null;
        var stem = FilePath + ".unreadable-" + clock.UtcNow.ToLocalTime().ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        try
        {
            using var source = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            for (var n = 1; n <= MaxBackupNames; n++)
            {
                var candidate = n == 1 ? stem : $"{stem}-{n}";
                FileStream target;
                // CreateNew is the collision check: it fails atomically when the name exists, so no copy is ever overwritten.
                try { target = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
                catch (IOException) when (Path.Exists(candidate)) { continue; }
                try
                {
                    using (target)
                    {
                        source.Position = 0;
                        source.CopyTo(target);
                        target.Flush(true);
                    }
                }
                catch
                {
                    TryDelete(candidate);
                    throw;
                }
                backup = candidate;
                return true;
            }
            throw new IOException($"{MaxBackupNames} backup names starting at {Path.GetFileName(stem)} are already taken.");
        }
        catch (Exception ex)
        {
            // Any failure means "no copy exists": the caller keeps the original safe, whatever the cause.
            failure = ex;
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

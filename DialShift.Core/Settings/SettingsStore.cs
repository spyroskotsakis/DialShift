using System.Text.Json;

namespace DialShift.Core;

public sealed class SettingsStore(string directory)
{
    public string DirectoryPath { get; } = directory;
    public string FilePath => Path.Combine(DirectoryPath, "settings.json");
    public string? Warning { get; private set; }
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public Settings Load()
    {
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
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            var backup = FilePath + ".unreadable-" + DateTime.Now.ToString("yyyyMMddHHmmssfff");
            // Preserve the original before allowing defaults to be saved.
            File.Copy(FilePath, backup);
            Warning = $"Your settings could not be read. A copy was preserved at {backup}.";
            return Settings.Defaults();
        }
    }

    public void Save(Settings settings)
    {
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
}

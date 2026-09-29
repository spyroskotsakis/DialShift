namespace DialShift.Core;

public sealed class Settings
{
    public int Version { get; set; } = 1;
    public List<Station> Stations { get; set; } = [];
    public List<ScheduleEntry> Schedule { get; set; } = [];
    public int Volume { get; set; } = 60;
    public bool ScheduleEnabled { get; set; }
    public bool LaunchAtLogin { get; set; }
    public bool StartInTray { get; set; }
    public Guid? FallbackStationId { get; set; }
    public Guid? LastStationId { get; set; }

    /// <summary>
    /// A fresh install starts with no stations. The three starter stations are seeded on first run from the bundled
    /// <c>starter-stations.json</c> (see DialShift.App's <c>StarterStations</c>), so the source of truth for station
    /// data stays in <c>data/</c> and out of C# literals (CAT-17).
    /// </summary>
    public static Settings Defaults() => new();
}

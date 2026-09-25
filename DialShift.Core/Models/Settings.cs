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

    public static Settings Defaults() => new()
    {
        Stations =
        [
            new() { Name = "Groove Salad", Tag = "SomaFM · Ambient / downtempo", Url = "https://ice5.somafm.com/groovesalad-128-aac" },
            new() { Name = "Drone Zone", Tag = "SomaFM · Atmospheric", Url = "https://ice5.somafm.com/dronezone-128-aac" },
            new() { Name = "Secret Agent", Tag = "SomaFM · Cinematic grooves", Url = "https://ice5.somafm.com/secretagent-128-aac" }
        ]
    };
}

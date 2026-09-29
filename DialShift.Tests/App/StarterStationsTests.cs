using DialShift.App.Services;
using DialShift.Core;
using static DialShift.Tests.TestHarness;

namespace DialShift.Tests.App;

/// <summary>
/// <see cref="StarterStations.Load"/> (D60, CAT-17): the tiny first-run starter list reads a bare
/// <c>{name, stream_url, tag}</c> array from <c>starter-stations.json</c> and never throws — any failure yields an empty list.
/// </summary>
public static class StarterStationsTests
{
    public static void Run()
    {
        using var temp = new TempDirectory("starter");

        Check("D60 a missing starter-stations.json yields an empty list (first-run never fails)",
            StarterStations.Load(temp.Path).Count == 0);

        var file = Path.Combine(temp.Path, StarterStations.FileName);
        File.WriteAllText(file, """
            [
              { "name": "Groove Salad", "stream_url": "https://ice5.somafm.com/groovesalad-128-aac", "tag": "SomaFM · Ambient / downtempo" },
              { "name": "  Drone Zone  ", "stream_url": "https://ice5.somafm.com/dronezone-128-aac", "tag": "   " },
              { "name": "", "stream_url": "https://ice5.somafm.com/secretagent-128-aac", "tag": "Cinematic grooves" },
              { "name": "Blocked", "stream_url": "file:///etc/passwd", "tag": "should be skipped" },
              { "name": "No Tag", "stream_url": "https://example.org/live" }
            ]
            """);
        var loaded = StarterStations.Load(temp.Path);
        Check("D60 valid rows map snake_case name/stream_url/tag to Name/Url/Tag; an empty name or an invalid URL is skipped",
            loaded.Count == 3
            && loaded[0].Name == "Groove Salad" && loaded[0].Url == "https://ice5.somafm.com/groovesalad-128-aac" && loaded[0].Tag == "SomaFM · Ambient / downtempo"
            && loaded[1].Name == "Drone Zone" && loaded[1].Url == "https://ice5.somafm.com/dronezone-128-aac"
            && loaded[2].Name == "No Tag" && loaded[2].Url == "https://example.org/live"
            && loaded.All(s => s.Name != "" && s.Name != "Blocked"));
        Check("D60 a whitespace or missing tag defaults to \"Internet radio\"",
            loaded[1].Tag == "Internet radio" && loaded[2].Tag == "Internet radio");

        File.WriteAllText(file, "{ not json");
        Check("D60 malformed JSON yields an empty list (never throws)", StarterStations.Load(temp.Path).Count == 0);
    }
}

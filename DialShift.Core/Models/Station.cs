using System.Text.Json.Serialization;

namespace DialShift.Core;

public sealed class Station
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Tag { get; set; } = "Internet radio";

    /// <summary>Free-text notes about the station, set when it was added from the station catalog (brief 3). Null
    /// for stations entered by hand and for every station saved before this field existed.</summary>
    /// <remarks>Null is not written, so a station without notes serializes exactly as before; adding the field does
    /// not change <see cref="Settings.Version"/>. SettingsStore validates only Name and Url, so any string loads.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Notes { get; set; }

    /// <summary>The VPN region as a human-readable region label ("United Kingdom", …) the stream is reachable
    /// through, set when it was added from the station catalog (brief 3). Null for hand-entered stations, for
    /// stations that need no VPN, and for every station saved before this field existed.</summary>
    /// <remarks>Null is not written, so a station without a VPN region serializes exactly as before; adding the
    /// field does not change <see cref="Settings.Version"/>. SettingsStore validates only Name and Url, so any
    /// string loads.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VpnRegion { get; set; }

    public override string ToString() => Name;
}

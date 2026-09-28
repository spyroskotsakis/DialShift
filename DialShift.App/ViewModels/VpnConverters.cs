using Avalonia.Data.Converters;

namespace DialShift.App.ViewModels;

/// <summary>
/// The VPN badge's text for the one picker whose items are Core <see cref="DialShift.Core.Station"/> objects rather than a
/// row view model — the slot editor's station picker (<c>ScheduleEditorDialog</c>). Everywhere else the badge binds a row's
/// own <c>VpnText</c>; here the region is on the item itself, so a converter keeps the wording in <see cref="UiText.VpnText"/>
/// alone and no view spells the badge out (the picker's selection stays the <c>Station</c>, so nothing else changes).
/// </summary>
public static class VpnConverters
{
    /// <summary>A station's stored region as the badge's text: "VPN · United Kingdom" (<see cref="UiText.VpnText"/>), "" when there is none.</summary>
    public static readonly IValueConverter Text = new FuncValueConverter<string?, string>(UiText.VpnText);
}

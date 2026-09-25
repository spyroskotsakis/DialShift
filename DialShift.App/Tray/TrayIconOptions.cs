namespace DialShift.App.Tray;

/// <summary>
/// Per-OS tray presentation, chosen once at composition (BHV-19/20). macOS: a monochrome template image the menu bar
/// tints, and a click opens the menu. Windows: a color icon, left-click opens the window and right-click the menu.
/// </summary>
/// <param name="IconUri">Avalonia resource URI of the icon (<c>.ico</c> on Windows, template PNG on macOS).</param>
/// <param name="IsTemplateIcon">Sets <c>MacOSProperties.IsTemplateIcon</c>; ignored off macOS.</param>
/// <param name="OpenWindowOnClick">Wires <c>TrayIcon.Clicked</c> to show the main window.</param>
public sealed record TrayIconOptions(Uri IconUri, bool IsTemplateIcon, bool OpenWindowOnClick)
{
    /// <summary>macOS menu-bar template image: alpha-only artwork, tinted by the system for light and dark menu bars.
    /// Avalonia hands the status item a single bitmap and resizes it to <c>floor(menu font size × 4/3)</c> points
    /// (17 pt at the 13 pt default), so one 44 px asset gives the Retina backing; an <c>@2x</c> variant would never be read.</summary>
    public static readonly Uri MacTemplateIcon = new("avares://DialShift/Assets/tray.png");

    /// <summary>Windows tray icon: the application <c>.ico</c>, whose small frames are drawn for the notification area (PK-04).</summary>
    public static readonly Uri WindowsIcon = new("avares://DialShift/Assets/dialshift.ico");

    public static TrayIconOptions ForCurrentPlatform() => OperatingSystem.IsMacOS()
        ? new TrayIconOptions(MacTemplateIcon, IsTemplateIcon: true, OpenWindowOnClick: false)
        : new TrayIconOptions(WindowsIcon, IsTemplateIcon: false, OpenWindowOnClick: true);
}

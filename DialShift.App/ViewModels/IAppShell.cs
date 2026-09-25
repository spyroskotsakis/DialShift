namespace DialShift.App.ViewModels;

/// <summary>
/// Window and process lifecycle actions the view models and the tray ask for. The App implements it:
/// show brings the main window to the front (BHV-15), hide keeps audio and the schedule running (BHV-12/14),
/// and quit runs the single teardown path (BHV-11).
/// </summary>
public interface IAppShell
{
    void ShowMainWindow();

    void HideMainWindow();

    void Quit();
}

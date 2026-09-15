using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DialShift.Core;

namespace DialShift;

public partial class App : Application
{
    public Settings Settings { get; private set; } = null!;
    public SettingsStore Store { get; private set; } = null!;
    public RadioController Radio { get; private set; } = null!;
    public MainWindow MainWindow { get; private set; } = null!;

    private TrayIcon? tray;
    private static FileStream? _lock;
    private IClassicDesktopStyleApplicationLifetime? desktop;
    private bool exiting;
    public static string[] StartupArgs = [];

    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DialShift");

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktopLifetime)
        {
            desktop = desktopLifetime;
            desktopLifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Store = new SettingsStore(DataDirectory);
            Settings = Store.Load();
            Radio = new RadioController(Settings);
            MainWindow = new MainWindow(this);
            CreateTray();
            Radio.Changed += UpdateTray;

            var startInTray = StartupArgs.Contains("--tray") || Settings.StartInTray;
            desktopLifetime.MainWindow = MainWindow;
            if (startInTray) MainWindow.Hide();

            Radio.StartSchedule();
            if (Store.Warning != null) _ = Message.Show(MainWindow, "DialShift · Settings recovered", Store.Warning);
        }
        base.OnFrameworkInitializationCompleted();
    }

    public static bool TryAcquireSingleInstance()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            _lock = new FileStream(Path.Combine(DataDirectory, ".single-instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public void Save()
    {
        try { Store.Save(Settings); }
        catch (Exception ex)
        {
            Log(ex);
            _ = Message.Show(MainWindow, "DialShift · Save failed", "Couldn't save your changes: " + ex.Message);
        }
    }

    public void Refresh()
    {
        Save();
        BuildTrayMenu();
        MainWindow.RefreshPage();
    }

    private void CreateTray()
    {
        tray = new TrayIcon
        {
            Icon = new WindowIcon(new Bitmap(AssetLoader.Open(new Uri("avares://DialShift/Assets/tray.png")))),
            ToolTipText = "DialShift · Ready",
            Menu = BuildTrayMenu()
        };
        TrayIcon.SetIcons(this, new TrayIcons { tray });
    }

    private NativeMenu BuildTrayMenu()
    {
        var menu = new NativeMenu();
        menu.Items.Add(new NativeMenuItem("Open DialShift") { Command = new Command(ShowWindow) });
        menu.Items.Add(new NativeMenuItem("Play / Pause") { Command = new Command(() => Radio.Toggle()) });
        menu.Items.Add(new NativeMenuItem("Next station") { Command = new Command(() => Radio.NextStation()) });

        var stations = new NativeMenuItem("Stations");
        var submenu = new NativeMenu();
        foreach (var station in Settings.Stations)
        {
            var s = station;
            submenu.Items.Add(new NativeMenuItem(station.Name) { Command = new Command(() => { Radio.Play(s); Save(); }) });
        }
        stations.Menu = submenu;
        menu.Items.Add(stations);

        var schedule = new NativeMenuItem("Follow schedule")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = Settings.ScheduleEnabled,
            Command = new Command(() => { Settings.ScheduleEnabled = !Settings.ScheduleEnabled; Radio.RefreshSchedule(); Refresh(); })
        };
        menu.Items.Add(schedule);

        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(new NativeMenuItem("Volume +10") { Command = new Command(() => { Radio.SetVolume(Settings.Volume + 10); Save(); }) });
        menu.Items.Add(new NativeMenuItem("Volume −10") { Command = new Command(() => { Radio.SetVolume(Settings.Volume - 10); Save(); }) });
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(new NativeMenuItem("Quit DialShift") { Command = new Command(ExitApp) });
        return menu;
    }

    private void UpdateTray()
    {
        if (tray == null) return;
        var text = $"DialShift · {(Radio.IsActive ? Radio.Current?.Name ?? "Connecting" : "Paused")}";
        tray.ToolTipText = text.Length > 63 ? text[..63] : text;
    }

    public void ShowWindow()
    {
        if (MainWindow == null) return;
        MainWindow.Show();
        MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
    }

    public void HideToTray() => MainWindow.Hide();

    public void SetStartup(bool enabled)
    {
        var plist = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "LaunchAgents", "com.tsiger.dialshift.plist");
        try
        {
            if (enabled)
            {
                var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path.");
                var xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                          "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
                          "<plist version=\"1.0\"><dict>\n" +
                          "  <key>Label</key><string>com.tsiger.dialshift</string>\n" +
                          "  <key>ProgramArguments</key><array><string>" + exe + "</string><string>--tray</string></array>\n" +
                          "  <key>RunAtLoad</key><true/>\n" +
                          "  <key>ProcessType</key><string>Interactive</string>\n" +
                          "</dict></plist>\n";
                Directory.CreateDirectory(Path.GetDirectoryName(plist)!);
                File.WriteAllText(plist, xml);
            }
            else if (File.Exists(plist)) File.Delete(plist);
        }
        catch (Exception ex) { Log(ex); throw; }
        Settings.LaunchAtLogin = enabled;
        Save();
    }

    public void ExitApp()
    {
        if (exiting) return;
        exiting = true;
        if (Settings != null && Store != null) Save();
        Radio?.Dispose();
        if (tray != null) tray.IsVisible = false;
        _lock?.Dispose();
        desktop?.Shutdown();
    }

    public static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.AppendAllText(Path.Combine(DataDirectory, "dialshift.log"), $"{DateTime.Now:O} {ex}\n");
        }
        catch { }
    }
}

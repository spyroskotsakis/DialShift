using System;
using System.Windows.Input;
using Avalonia;

namespace DialShift;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        App.StartupArgs = args;
        if (!App.TryAcquireSingleInstance()) return; // another instance is already running
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}

/// <summary>A tiny ICommand for NativeMenu / TrayIcon actions.</summary>
internal sealed class Command(Action action) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => action();
}

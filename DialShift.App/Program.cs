using System;
using System.Windows.Input;
using Avalonia;

namespace DialShift.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        App.StartupArgs = args;
        try
        {
            App.Paths = AppPaths.Resolve(Environment.GetEnvironmentVariable, smokeTest: args.Contains("--smoke-test"));
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine("DialShift can't start: " + ex.Message);
            return 1;
        }
        if (!App.TryAcquireSingleInstance())
        {
            App.SignalExistingInstance(); // bring the running instance to the front
            return 0;
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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

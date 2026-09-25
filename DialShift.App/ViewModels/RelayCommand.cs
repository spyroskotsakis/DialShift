using System.Windows.Input;

namespace DialShift.App.ViewModels;

/// <summary>Synchronous <see cref="ICommand"/> for view models, the tray menu and dialogs.</summary>
public sealed class RelayCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute();
}

/// <summary>
/// Asynchronous <see cref="ICommand"/>. It is disabled while it runs, so a double click cannot start a command twice.
/// Exceptions never escape into the UI loop: they go to <paramref name="onError"/> (the view models log them and show a message).
/// </summary>
public sealed class AsyncRelayCommand(Func<Task> execute, Action<Exception> onError, Func<bool>? canExecute = null) : ICommand
{
    private bool running;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !running && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter) => await ExecuteAsync();

    /// <summary>Runs the command if it can execute. Tests await this instead of <see cref="Execute"/>.</summary>
    public async Task ExecuteAsync()
    {
        if (!CanExecute(null)) return;
        running = true;
        NotifyCanExecuteChanged();
        try { await execute(); }
        catch (Exception ex) { onError(ex); }
        finally
        {
            running = false;
            NotifyCanExecuteChanged();
        }
    }

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

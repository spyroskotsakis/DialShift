using Avalonia.Threading;

namespace DialShift.App.Services;

/// <summary><see cref="IUiDispatcher"/> over <see cref="Dispatcher.UIThread"/>; used only to apply view-model state.</summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    public async Task InvokeAsync(Action action) => await Dispatcher.UIThread.InvokeAsync(action);
}

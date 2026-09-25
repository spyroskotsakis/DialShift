namespace DialShift.App.Services;

/// <summary>
/// UI-thread marshalling (acceptance matrix §8.2.5). <c>AvaloniaUiDispatcher</c> wraps
/// <c>Dispatcher.UIThread</c> and is the only place playback state is marshalled to the UI,
/// for view-model updates only.
/// </summary>
public interface IUiDispatcher
{
    bool CheckAccess();
    void Post(Action action);
    Task InvokeAsync(Action action);
}

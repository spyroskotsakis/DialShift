using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DialShift.App.ViewModels;

namespace DialShift.App.Views;

/// <summary>
/// The main window. View-only behavior lives here: close and minimize hide to the tray (BHV-12/13) while audio and the
/// schedule keep running, and the volume is persisted when the slider is released (BHV-28), as today.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // The slider handles pointer input itself, so listen to handled events too.
        VolumeSlider.AddHandler(PointerReleasedEvent, OnVolumeCommitted, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        VolumeSlider.AddHandler(KeyUpEvent, OnVolumeCommitted, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    public MainWindow(MainWindowViewModel viewModel) : this() => DataContext = viewModel;

    private MainWindowViewModel? subscribed;

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (subscribed != null) subscribed.PropertyChanged -= OnViewModelPropertyChanged;
        subscribed = ViewModel;
        if (subscribed != null) subscribed.PropertyChanged += OnViewModelPropertyChanged;
    }

    // A newly selected tab starts at its top.
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.CurrentPage)) PageScroller.Offset = default;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown) return;
        e.Cancel = true;
        Hide();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty && WindowState == WindowState.Minimized) Hide();
    }

    private void OnVolumeCommitted(object? sender, RoutedEventArgs e) => ViewModel?.PersistVolumeCommand.Execute(null);
}

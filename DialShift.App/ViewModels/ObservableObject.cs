using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DialShift.App.ViewModels;

/// <summary>Minimal <see cref="INotifyPropertyChanged"/> base. View models stay free of Avalonia controls and the UI thread so
/// they can be unit-tested; the one Avalonia type they carry is a decoded catalog logo (<c>Bitmap</c>), which the tests leave null.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

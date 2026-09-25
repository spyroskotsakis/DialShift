using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using DialShift.App.ViewModels;

namespace DialShift.App.Views.Pages;

public partial class SchedulePage : UserControl
{
    /// <summary>
    /// While the page is shown, zoned rows' "Next: …" lines are recomputed every 15 s, so a start that has passed moves on
    /// even when nothing else changes (playback snapshots are published only on change). View state only.
    /// </summary>
    private readonly DispatcherTimer nextStarts = new() { Interval = TimeSpan.FromSeconds(15) };

    public SchedulePage()
    {
        InitializeComponent();
        nextStarts.Tick += (_, _) => (DataContext as SchedulePageViewModel)?.UpdateNextStarts();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as SchedulePageViewModel)?.UpdateNextStarts();
        nextStarts.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        nextStarts.Stop();
        base.OnDetachedFromVisualTree(e);
    }
}

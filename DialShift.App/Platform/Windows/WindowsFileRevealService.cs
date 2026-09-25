using System.Diagnostics;
using System.Runtime.Versioning;

namespace DialShift.App.Platform.Windows;

/// <summary>
/// Windows <see cref="IFileRevealService"/> (acceptance matrix §8.2.3): <c>explorer.exe &lt;dir&gt;</c> opens a folder,
/// <c>explorer.exe /select,&lt;file&gt;</c> opens the containing folder with the file selected.
/// </summary>
/// <remarks>
/// <c>explorer.exe</c> is started by full path from the Windows directory (never resolved via <c>PATH</c>) with
/// <c>UseShellExecute=false</c> and <see cref="ProcessStartInfo.ArgumentList"/>. <c>/select,</c> and the path form a
/// <b>single</b> argument entry, which .NET quotes as a whole when the path contains spaces. The path is made absolute
/// first, so it starts with a drive letter or <c>\\</c> and cannot be read as another Explorer switch. Explorer's
/// exit code is not meaningful (it is often 1 on success), so it is not checked.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsFileRevealService : IFileRevealService
{
    private readonly Func<ProcessStartInfo, CancellationToken, Task> launch;

    /// <param name="launch">Process-launcher seam for tests (HS-12). Defaults to starting the process without waiting.</param>
    public WindowsFileRevealService(Func<ProcessStartInfo, CancellationToken, Task>? launch = null)
    {
        this.launch = launch ?? StartAsync;
    }

    public Task RevealInFileManagerAsync(string fileOrDirectoryPath, CancellationToken cancellationToken = default)
    {
        var target = RevealTarget.Resolve(fileOrDirectoryPath);
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var startInfo = new ProcessStartInfo(explorer) { UseShellExecute = false };
        startInfo.ArgumentList.Add(target.IsDirectory ? target.FullPath : "/select," + target.FullPath);
        return launch(startInfo, cancellationToken);
    }

    private static Task StartAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("File Explorer could not be opened.");
        return Task.CompletedTask;
    }
}

using System.Diagnostics;
using System.Runtime.Versioning;

namespace DialShift.App.Platform.MacOS;

/// <summary>
/// macOS <see cref="IFileRevealService"/> (acceptance matrix §8.2.3): <c>/usr/bin/open &lt;dir&gt;</c> for a folder,
/// <c>/usr/bin/open -R &lt;file&gt;</c> to reveal a file in Finder.
/// </summary>
/// <remarks>
/// The path is passed as its own <see cref="ProcessStartInfo.ArgumentList"/> entry with <c>UseShellExecute=false</c>:
/// no shell and no command-string interpolation. The path is made absolute first, so it always starts with '/' and
/// can never be mistaken for an <c>open</c> option.
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class MacFileRevealService : IFileRevealService
{
    private const string OpenTool = "/usr/bin/open";
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<ProcessStartInfo, CancellationToken, Task> launch;

    /// <param name="launch">Process-launcher seam for tests (HS-12). Defaults to starting the process and checking its exit code.</param>
    public MacFileRevealService(Func<ProcessStartInfo, CancellationToken, Task>? launch = null)
    {
        this.launch = launch ?? RunAsync;
    }

    public Task RevealInFileManagerAsync(string fileOrDirectoryPath, CancellationToken cancellationToken = default)
    {
        var target = RevealTarget.Resolve(fileOrDirectoryPath);
        var startInfo = new ProcessStartInfo(OpenTool) { UseShellExecute = false, RedirectStandardError = true };
        if (!target.IsDirectory) startInfo.ArgumentList.Add("-R");
        startInfo.ArgumentList.Add(target.FullPath);
        return launch(startInfo, cancellationToken);
    }

    private static async Task RunAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Finder could not be opened.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OpenTimeout);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Finder could not open the folder: {(await stderr.ConfigureAwait(false)).Trim()}");
    }
}

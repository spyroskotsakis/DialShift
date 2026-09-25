namespace DialShift.App.Platform;

/// <summary>
/// Opens a directory or reveals a file in the OS file manager (acceptance matrix §8.2.3).
/// macOS: <c>/usr/bin/open &lt;dir&gt;</c> or <c>/usr/bin/open -R &lt;file&gt;</c>.
/// Windows: <c>explorer.exe &lt;dir&gt;</c> or <c>explorer.exe /select,&lt;file&gt;</c>.
/// Implementations use <c>UseShellExecute=false</c> with <c>ProcessStartInfo.ArgumentList</c> and never
/// interpolate paths into a command string.
/// </summary>
public interface IFileRevealService
{
    /// <summary>
    /// Opens <paramref name="fileOrDirectoryPath"/> if it is a directory, or reveals it if it is a file.
    /// A missing path raises <see cref="DirectoryNotFoundException"/> or <see cref="FileNotFoundException"/>.
    /// </summary>
    Task RevealInFileManagerAsync(
        string fileOrDirectoryPath,
        CancellationToken cancellationToken = default);
}

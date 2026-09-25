namespace DialShift.App.Platform;

/// <summary>Validated input for <see cref="IFileRevealService"/>: an absolute, existing file or directory path.</summary>
internal readonly record struct RevealTarget(string FullPath, bool IsDirectory)
{
    /// <exception cref="DirectoryNotFoundException">Nothing exists at the path and it looks like a folder.</exception>
    /// <exception cref="FileNotFoundException">Nothing exists at the path and it looks like a file.</exception>
    public static RevealTarget Resolve(string fileOrDirectoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileOrDirectoryPath);
        var fullPath = Path.GetFullPath(fileOrDirectoryPath);
        if (Directory.Exists(fullPath)) return new(Path.TrimEndingDirectorySeparator(fullPath), IsDirectory: true);
        if (File.Exists(fullPath)) return new(fullPath, IsDirectory: false);

        if (Path.EndsInDirectorySeparator(fullPath) || !Path.HasExtension(fullPath))
            throw new DirectoryNotFoundException($"The folder \"{fullPath}\" doesn't exist.");
        throw new FileNotFoundException($"The file \"{fullPath}\" doesn't exist.", fullPath);
    }
}

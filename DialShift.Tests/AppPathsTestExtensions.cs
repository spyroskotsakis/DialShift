using DialShift.App;
using DialShift.Core;

namespace DialShift.Tests;

/// <summary>Test shorthand for files the app derives from <see cref="AppPaths.DataDirectory"/>.</summary>
internal static class AppPathsTestExtensions
{
    extension(AppPaths paths)
    {
        /// <summary>The <c>settings.json</c> the app's <see cref="SettingsStore"/> reads and writes for this data directory
        /// (the composition root builds it from <see cref="AppPaths.DataDirectory"/>).</summary>
        public string SettingsFile => new SettingsStore(paths.DataDirectory).FilePath;
    }
}

using System.Runtime.Versioning;
using DialShift.App.Platform.MacOS;
using DialShift.App.Platform.Windows;
using DialShift.App.Services;
using DialShift.App.SingleInstance;
using DialShift.Core.Playback;
using Microsoft.Extensions.DependencyInjection;

namespace DialShift.App.Platform;

/// <summary>
/// Composition-root registration of OS capability services (brief 1 §4.2): the platform is chosen exactly once
/// here with runtime checks, and no other code branches on the OS to pick an implementation.
/// </summary>
public static class PlatformServices
{
    /// <summary>
    /// Registers, as singletons: <see cref="FileAppLog"/> (also as <see cref="IAppLog"/>),
    /// <see cref="ISingleInstanceService"/>, <see cref="IClock"/>, and the per-OS <see cref="IMonotonicClock"/>
    /// (sleep-inclusive), <see cref="IStartupRegistration"/>, <see cref="ISystemPowerEvents"/> and
    /// <see cref="IFileRevealService"/>.
    /// </summary>
    /// <remarks>
    /// Prerequisite: the composition root registers the <see cref="AppPaths"/> it resolved once in <c>Program.Main</c>
    /// (<c>services.AddSingleton(paths)</c>). <see cref="ISingleInstanceService"/> is only
    /// <see cref="IAsyncDisposable"/>, so dispose the provider with <c>DisposeAsync</c> (synchronous
    /// <c>Dispose</c> throws for such services). The App should still dispose the power events and the single-instance
    /// service explicitly in its quit order (acceptance matrix BHV-11).
    /// </remarks>
    /// <exception cref="PlatformNotSupportedException">The OS is neither Windows nor macOS.</exception>
    public static IServiceCollection AddDialShiftPlatform(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(sp => new FileAppLog(sp.GetRequiredService<AppPaths>().LogFile));
        services.AddSingleton<IAppLog>(sp => sp.GetRequiredService<FileAppLog>());
        services.AddSingleton<ISingleInstanceService>(sp =>
            new SingleInstanceService(sp.GetRequiredService<AppPaths>(), sp.GetRequiredService<IAppLog>()));
        services.AddSingleton<IClock>(SystemClock.Instance);

        if (OperatingSystem.IsWindows()) AddWindows(services);
        else if (OperatingSystem.IsMacOS()) AddMacOS(services);
        else
            throw new PlatformNotSupportedException(
                $"DialShift runs on Windows and macOS only; this OS ({System.Runtime.InteropServices.RuntimeInformation.OSDescription}) is not supported.");
        return services;
    }

    [SupportedOSPlatform("windows")]
    private static void AddWindows(IServiceCollection services)
    {
        services.AddSingleton<IMonotonicClock>(WindowsMonotonicClock.Instance);
        services.AddSingleton<IStartupRegistration>(sp => new WindowsStartupRegistration(sp.GetRequiredService<IAppLog>()));
        services.AddSingleton<ISystemPowerEvents>(sp => new WindowsPowerEvents(sp.GetRequiredService<IAppLog>()));
        services.AddSingleton<IFileRevealService>(_ => new WindowsFileRevealService());
    }

    [SupportedOSPlatform("macos")]
    private static void AddMacOS(IServiceCollection services)
    {
        services.AddSingleton<IMonotonicClock>(MacMonotonicClock.Instance);
        services.AddSingleton<IStartupRegistration>(sp => new MacStartupRegistration(sp.GetRequiredService<IAppLog>()));
        services.AddSingleton<ISystemPowerEvents>(sp => new MacPowerEvents(sp.GetRequiredService<IAppLog>()));
        services.AddSingleton<IFileRevealService>(_ => new MacFileRevealService());
    }
}

using System.Diagnostics;

namespace DialShift.Tests;

/// <summary>Starts this test executable again as a child process (the <c>--si-*</c> and <c>--log-child</c> modes).</summary>
public static class SelfProcess
{
    /// <summary>Start info for <c>dotnet DialShift.Tests.dll &lt;args&gt;</c> with stdout and stderr redirected.</summary>
    public static ProcessStartInfo StartInfo(params string[] args)
    {
        var startInfo = new ProcessStartInfo(DotnetHost())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(typeof(SelfProcess).Assembly.Location);
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);
        return startInfo;
    }

    /// <summary>The <c>dotnet</c> host that runs this process (never resolved from <c>PATH</c> when it can be found).</summary>
    private static string DotnetHost()
    {
        var current = Environment.ProcessPath;
        if (current != null && Path.GetFileNameWithoutExtension(current).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return current;
        // typeof(object) lives in <root>/shared/Microsoft.NETCore.App/<version>/.
        var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var host = Path.GetFullPath(Path.Combine(runtimeDirectory, "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        return File.Exists(host) ? host : "dotnet";
    }
}

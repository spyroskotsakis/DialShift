using System.Net.Http.Headers;

namespace DialShift.App.Services;

/// <summary>The User-Agent of every HTTP request the app makes itself (catalog logos, the TLS trust warm-up). The players send their own.</summary>
internal static class AppUserAgent
{
    /// <summary><c>DialShift/&lt;major.minor.patch&gt;</c>, from the App assembly version.</summary>
    public static ProductInfoHeaderValue Create() =>
        new("DialShift", typeof(AppUserAgent).Assembly.GetName().Version?.ToString(3) ?? "0");
}

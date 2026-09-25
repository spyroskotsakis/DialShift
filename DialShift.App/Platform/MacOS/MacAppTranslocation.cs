namespace DialShift.App.Platform.MacOS;

/// <summary>
/// Gatekeeper App Translocation: a quarantined app opened where it was downloaded (for example straight from
/// <c>~/Downloads</c>, without moving it) runs from a random, read-only copy under
/// <c>/private/var/folders/…/AppTranslocation/&lt;UUID&gt;/d/DialShift.app</c>. That path is gone after the app quits,
/// so nothing that must outlive the process (the launch-at-login entry) may point at it.
/// </summary>
/// <remarks>
/// Detection is the path check below rather than <c>SecTranslocateIsTranslocatedURL</c>: Security.framework has always
/// mounted translocated apps under an <c>AppTranslocation</c> directory, the check needs no P/Invoke or CoreFoundation
/// objects, and it is pure, so it is testable with any path. The user's fix is to move DialShift to Applications
/// (which clears the translocation) and open it again.
/// </remarks>
public static class MacAppTranslocation
{
    /// <summary>The path segment every translocated app runs under.</summary>
    public const string PathSegment = "/AppTranslocation/";

    /// <summary>True when <paramref name="executablePath"/> is inside a translocated copy of an app.</summary>
    public static bool IsTranslocated(string? executablePath) =>
        !string.IsNullOrEmpty(executablePath) && executablePath.Contains(PathSegment, StringComparison.Ordinal);
}

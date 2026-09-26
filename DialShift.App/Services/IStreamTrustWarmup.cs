namespace DialShift.App.Services;

/// <summary>
/// Makes the operating system trust an https stream's certificate chain before a native player opens it (D100). The
/// Windows engine needs it: LibVLC's GnuTLS reads only the roots already in the Windows store and never asks Windows to
/// download a missing one, which a .NET TLS connection does. Real implementation: <see cref="WindowsTrustWarmup"/>.
/// </summary>
/// <remarks>
/// The engine that is given a warm-up owns it and disposes it with itself. <see cref="WarmAsync"/> may block its caller
/// before its first await (starting an HTTP request looks up the system proxy), so the engine calls it on the thread pool
/// and bounds its wait from there.
/// </remarks>
public interface IStreamTrustWarmup : IDisposable
{
    /// <summary>
    /// Connects to <paramref name="url"/> (following its redirects) so that the TLS handshakes build and validate the
    /// certificate chains, then returns. A token that is already cancelled throws first, whatever the URL; otherwise it
    /// returns at once for anything but an absolute https URL, and after <see cref="IDisposable.Dispose"/>. Never
    /// reports a failure: the player then fails as it would have without the warm-up.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="url"/> is null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> is or becomes cancelled; nothing else is thrown.</exception>
    Task WarmAsync(Uri url, CancellationToken ct);
}

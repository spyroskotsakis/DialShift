using Avalonia.Media.Imaging;

namespace DialShift.App.Services;

/// <summary>Loads the remote logos of catalog stations for the Add dialog (D66, docs/catalog-contracts.md §4.3).
/// Many catalog logos are dead or missing; the caller shows the monogram whenever the result is null.</summary>
public interface ICatalogLogoLoader
{
    /// <summary>The decoded logo, or null on any failure (not http/https, timeout, too large, not an image,
    /// cancelled). Never throws; never runs network or decoding work on the calling thread.</summary>
    Task<Bitmap?> LoadAsync(string url, CancellationToken cancellationToken);
}

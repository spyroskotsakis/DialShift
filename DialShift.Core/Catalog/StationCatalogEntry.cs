namespace DialShift.Core.Catalog;

/// <summary>One station of the generated app catalog (docs/catalog-contracts.md §2). Plain immutable data.</summary>
/// <remarks>Strings are never null; "" means unknown. The catalog provider guarantees: Name, Country and StreamUrl
/// are non-empty, StreamUrl passes SettingsStore.ValidUrl and is at most 2,048 characters, Logo is "" or an http(s)
/// URL, Bitrate is null or positive, Votes is null or non-negative. The query engine does not re-validate.</remarks>
public sealed record StationCatalogEntry
{
    /// <summary>The station's name as the catalog lists it; may be longer than the station dialog's 100 characters (D73).</summary>
    public required string Name { get; init; }

    /// <summary>The name in the station's own script or language, when the source gives one.</summary>
    public string NameLocal { get; init; } = "";

    /// <summary>The country code (<c>GR</c>, <c>FR</c>, <c>DE</c>, …), or <c>Internet</c> for the collections. The Country filter compares this.</summary>
    public required string Country { get; init; }

    /// <summary>The country's display name; "" means show <see cref="Country"/>.</summary>
    public string CountryLabel { get; init; } = "";

    public string City { get; init; } = "";
    public string Region { get; init; } = "";

    /// <summary>The frequency as the source writes it: FM MHz such as <c>101.5</c>, or AM kHz such as <c>1593</c>.</summary>
    public string FrequencyFm { get; init; } = "";

    public string Type { get; init; } = "";
    public string Genre { get; init; } = "";
    public string Language { get; init; } = "";
    public bool InternetOnly { get; init; }
    public required string StreamUrl { get; init; }
    public string Codec { get; init; } = "";

    /// <summary>Stream bitrate in kbit/s; null when unknown.</summary>
    public int? Bitrate { get; init; }

    /// <summary>Popularity votes; null when unknown, and ranked as 0 (§3.3).</summary>
    public int? Votes { get; init; }

    /// <summary>Free-text notes; copied to <see cref="Station.Notes"/> when the picked stream is saved unchanged (D73).</summary>
    public string Notes { get; init; } = "";

    public string Logo { get; init; } = "";

    /// <summary>The precomputed "Description / genre" text a picked station gets (<c>app_tag</c> of the pipeline).</summary>
    public string Tag { get; init; } = "";
}

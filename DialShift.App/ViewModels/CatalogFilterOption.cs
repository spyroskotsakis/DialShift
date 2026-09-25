namespace DialShift.App.ViewModels;

/// <summary>
/// One entry of a catalog filter picker in the Add dialog (docs/catalog-contracts.md §5.2). <see cref="Value"/> is what the
/// query compares (the country code for Country); null is <see cref="All"/>, no filter. Records compare by value, so the
/// picker keeps its selection when the option list is rebuilt.
/// </summary>
public sealed record CatalogFilterOption(string? Value, string Label)
{
    public static CatalogFilterOption All { get; } = new(null, "All");

    public override string ToString() => Label;
}

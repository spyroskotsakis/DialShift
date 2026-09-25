namespace DialShift.Core;

public sealed class Station
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Tag { get; set; } = "Internet radio";
    public override string ToString() => Name;
}

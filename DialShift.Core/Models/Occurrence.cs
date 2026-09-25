namespace DialShift.Core;

public sealed record Occurrence(ScheduleEntry Entry, DateTime At)
{
    public string Key => $"{Entry.Id}:{At:yyyy-MM-ddTHH:mm}";
}

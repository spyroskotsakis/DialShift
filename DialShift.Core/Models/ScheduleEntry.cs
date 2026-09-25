namespace DialShift.Core;

public sealed class ScheduleEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StationId { get; set; }
    public string Label { get; set; } = "";
    public string Time { get; set; } = "08:00";
    public List<DayOfWeek> Days { get; set; } = [];
    public bool Enabled { get; set; } = true;
}

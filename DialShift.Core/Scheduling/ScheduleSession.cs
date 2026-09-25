namespace DialShift.Core;

public sealed class ScheduleSession
{
    private Occurrence? last;

    public void HoldCurrent(Settings settings, DateTime now)
    {
        var current = Scheduler.Evaluate(settings, now).Current;
        if (last == null || current?.At >= last.At) last = current;
    }

    public Occurrence? TakeChange(Settings settings, DateTime now, bool force = false)
    {
        if (!settings.ScheduleEnabled) return null;
        var current = Scheduler.Evaluate(settings, now).Current;
        if (current == null) { last = null; return null; }
        // Do not replay an older occurrence when the wall clock moves backwards.
        if (!force && last != null && (current.Key == last.Key || current.At < last.At)) return null;
        last = current;
        return current;
    }
}

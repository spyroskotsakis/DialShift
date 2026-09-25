using DialShift.Core;
using static DialShift.Tests.TestHarness;

namespace DialShift.Tests.Core;

/// <summary>
/// <c>ScheduleSession</c> dedup and hold semantics. <see cref="LegacyDst"/> is the original Program.cs DST sequence
/// (CT-SES-07: unchanged); CT-SES-* are acceptance-matrix §7.3. <c>[quirk]</c> checks mirror current behavior on purpose.
/// </summary>
public static class ScheduleSessionTests
{
    /// <summary>2026-09-14 is a Monday. Unspecified-kind wall-clock values only.</summary>
    private static DateTime Mon(int hour, int minute = 0) => new(2026, 9, 14, hour, minute, 0);

    /// <summary>Schedule on; slot A Mon 09:00 (station 0), slot B Mon 11:00 (station 1).</summary>
    private static (Settings Settings, ScheduleEntry A, ScheduleEntry B) TwoSlots()
    {
        var settings = Settings.Defaults();
        settings.ScheduleEnabled = true;
        var a = new ScheduleEntry { StationId = settings.Stations[0].Id, Time = "09:00", Days = [DayOfWeek.Monday] };
        var b = new ScheduleEntry { StationId = settings.Stations[1].Id, Time = "11:00", Days = [DayOfWeek.Monday] };
        settings.Schedule.AddRange([a, b]);
        return (settings, a, b);
    }

    private static bool Is(Occurrence? occurrence, ScheduleEntry entry, DateTime at) => occurrence?.Entry == entry && occurrence.At == at;

    public static void Run()
    {
        LegacyDst();
        DisabledIsInert();
        HoldWithinSlot();
        HoldNeverMovesBackwards();
        HoldWithNoCurrent();
        ForcedReplay();
        ResetWhenNothingCurrent();
        EditedSlotSuppressed();
        BackwardClockJump();
    }

    private static void LegacyDst()
    {
        var stations = Settings.Defaults().Stations;
        var dst = new Settings { Stations = stations, Schedule = [new() { StationId = stations[0].Id, Time = "03:30", Days = [DayOfWeek.Sunday] }] };
        dst.ScheduleEnabled = true;
        var session = new ScheduleSession();
        Check("Session fires DST occurrence once", session.TakeChange(dst, new DateTime(2026, 10, 25, 3, 40, 0)) != null);
        Check("Fall-back does not replay older occurrence", session.TakeChange(dst, new DateTime(2026, 10, 25, 3, 10, 0)) == null);
        Check("Repeated hour does not replay same occurrence", session.TakeChange(dst, new DateTime(2026, 10, 25, 3, 40, 0)) == null);
        Check("Following week still fires", session.TakeChange(dst, new DateTime(2026, 11, 1, 3, 40, 0)) != null);
    }

    private static void DisabledIsInert()
    {
        var (settings, a, _) = TwoSlots();
        var session = new ScheduleSession();
        settings.ScheduleEnabled = false;
        Check("CT-SES-01 schedule off: TakeChange returns null", session.TakeChange(settings, Mon(9, 5)) == null);
        settings.ScheduleEnabled = true;
        Check("CT-SES-01 re-enabled: the same occurrence fires (off did not record it)", Is(session.TakeChange(settings, Mon(9, 5)), a, Mon(9)));
        settings.ScheduleEnabled = false;
        Check("CT-SES-01 schedule off again: null", session.TakeChange(settings, Mon(9, 6)) == null);
        settings.ScheduleEnabled = true;
        Check("CT-SES-01 off did not reset the taken occurrence", session.TakeChange(settings, Mon(9, 7)) == null);
    }

    private static void HoldWithinSlot()
    {
        var (settings, _, b) = TwoSlots();
        var session = new ScheduleSession();
        session.HoldCurrent(settings, Mon(10));
        Check("CT-SES-02 after HoldCurrent at 10:00, TakeChange at 10:05 is null (slot A held)", session.TakeChange(settings, Mon(10, 5)) == null);
        Check("CT-SES-02 the next occurrence (B 11:00) still fires", Is(session.TakeChange(settings, Mon(11)), b, Mon(11)));
    }

    private static void HoldNeverMovesBackwards()
    {
        var (settings, _, b) = TwoSlots();
        var session = new ScheduleSession();
        Check("CT-SES-03 setup: B 11:00 taken", Is(session.TakeChange(settings, Mon(11)), b, Mon(11)));
        session.HoldCurrent(settings, Mon(10, 30)); // clock went back: current is A 09:00 < last (B 11:00)
        Check("[quirk] CT-SES-03 TakeChange at 10:31 is null (older occurrence not replayed)", session.TakeChange(settings, Mon(10, 31)) == null);
        // Had HoldCurrent moved `last` back to A 09:00, B 11:00 would count as new here.
        Check("[quirk] CT-SES-03 HoldCurrent left last = B 11:00 (TakeChange at 11:00 is null)", session.TakeChange(settings, Mon(11)) == null);
    }

    private static void HoldWithNoCurrent()
    {
        var settings = Settings.Defaults();
        settings.ScheduleEnabled = true;
        var session = new ScheduleSession();
        session.HoldCurrent(settings, Mon(8, 30)); // no slots yet: nothing current, nothing held
        var first = new ScheduleEntry { StationId = settings.Stations[0].Id, Time = "09:00", Days = [DayOfWeek.Monday] };
        settings.Schedule.Add(first);
        Check("CT-SES-04 HoldCurrent with no current occurrence does not block the first slot", Is(session.TakeChange(settings, Mon(9)), first, Mon(9)));
    }

    private static void ForcedReplay()
    {
        var (settings, a, _) = TwoSlots();
        var session = new ScheduleSession();
        var taken = session.TakeChange(settings, Mon(9, 5));
        Check("CT-SES-05 setup: A taken, unforced repeat is null", Is(taken, a, Mon(9)) && session.TakeChange(settings, Mon(9, 6)) == null);
        Check("[quirk] CT-SES-05 forced refresh replays the already-fired slot (BHV-47)", session.TakeChange(settings, Mon(9, 6), force: true)?.Key == taken!.Key);
    }

    private static void ResetWhenNothingCurrent()
    {
        var (settings, a, b) = TwoSlots();
        var session = new ScheduleSession();
        Check("CT-SES-06 setup: A taken", Is(session.TakeChange(settings, Mon(9, 5)), a, Mon(9)));
        a.Enabled = b.Enabled = false;
        Check("CT-SES-06 all slots disabled: null (and last is reset)", session.TakeChange(settings, Mon(9, 6)) == null);
        a.Enabled = b.Enabled = true;
        Check("[quirk] CT-SES-06 re-enabled: the same occurrence fires again", Is(session.TakeChange(settings, Mon(9, 7)), a, Mon(9)));
    }

    private static void EditedSlotSuppressed()
    {
        var settings = Settings.Defaults();
        settings.ScheduleEnabled = true;
        var slot = new ScheduleEntry { StationId = settings.Stations[0].Id, Time = "10:00", Days = [DayOfWeek.Monday] };
        settings.Schedule.Add(slot);
        var session = new ScheduleSession();
        Check("CT-SES-08 setup: today's 10:00 taken", Is(session.TakeChange(settings, Mon(10)), slot, Mon(10)));
        slot.Time = "09:30"; // edited in place (same Id) to an earlier time
        Check("[quirk] CT-SES-08 edited earlier slot is suppressed without force (QA-N8 Phase 1 pin)", session.TakeChange(settings, Mon(10, 30)) == null);
        Check("CT-SES-08 forced refresh fires the edited 09:30 occurrence", Is(session.TakeChange(settings, Mon(10, 30), force: true), slot, Mon(9, 30)));
    }

    private static void BackwardClockJump()
    {
        // A wall clock corrected backwards past a fired slot suppresses every occurrence older than that slot
        // until wall time passes it again (same `current.At < last.At` guard as CT-SES-03/08).
        var settings = Settings.Defaults();
        settings.ScheduleEnabled = true;
        var tuesday = new ScheduleEntry { StationId = settings.Stations[0].Id, Time = "09:00", Days = [DayOfWeek.Tuesday] };
        var wednesday = new ScheduleEntry { StationId = settings.Stations[1].Id, Time = "10:00", Days = [DayOfWeek.Wednesday] };
        settings.Schedule.AddRange([tuesday, wednesday]);
        var session = new ScheduleSession();
        Check("Backward clock jump setup: Wed 10:00 taken", Is(session.TakeChange(settings, Mon(10).AddDays(2)), wednesday, Mon(10).AddDays(2)));
        Check("[quirk] Backward clock jump: Mon 12:00 does not replay last week's Wed slot", session.TakeChange(settings, Mon(12)) == null);
        Check("[quirk] Backward clock jump: Tue 09:00 slot is suppressed (older than last fired)", session.TakeChange(settings, Mon(9).AddDays(1)) == null);
    }
}

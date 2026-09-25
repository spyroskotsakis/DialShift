# Schedule & Timezone — Research and Change Plan

> **Status: Implemented on branch `refactor/single-codebase-timezone`. See the acceptance matrix timezone tracker ([`docs/acceptance-matrix.md` §6](acceptance-matrix.md#6-timezone-qa-tracker-brief-2)).** The implementation's deviations from this plan, and what the plan got wrong, are in [§13 Implementation notes](#13-implementation-notes-2026-09-25). Sections 1–12 are the original research and are kept as written. Their line numbers, file names (`DialShift/`, `DialShift.Mac/`, `Models.cs`, `RadioController`, `SmokeChecks.cs`) and code sketches describe the code at tag `legacy-last-known-good`, not the current tree.
>
> **Original status (research phase):** Research only — no code changes have been made. This document records (1) exactly how the schedule logic works today, (2) the change needed to let a schedule slot carry its own timezone and fire at the correct **local** time on the computer, and (3) the precise code changes required across `DialShift.Core`, the Windows WPF app, and the macOS Avalonia app, followed by a QA test plan.
>
> **Amended 2026-09-25:** quality gates added as §12 — no dead code, docs always current, best UI/UX, prod-ready test-and-fix loop.
>
> **QA note:** Sections below were reviewed by a two-pass QA review (one verifying the "how it works today" walkthrough against source, one brute-forcing the proposed algorithm against 51,484,800 sampled instants × 12 entry zones × 8 local zones × 5 times × 3 day-sets). The core algorithm is correct, but the review surfaced **4 blocking defects** and 9 non-blocking corrections, all folded in below and marked **QA-B1..QA-B4** (blocking) and **QA-N1..QA-N9** (non-blocking).

---

## 1. How the schedule works today

### 1.1 Data model — `DialShift.Core/Models.cs`

| Type | Lines | Fields | Notes |
|---|---|---|---|
| `ScheduleEntry` | 15–23 | `Id`, `StationId`, `Label`, `Time` (`"HH:mm"` string), `Days` (`List<DayOfWeek>`), `Enabled` | **No timezone field.** |
| `Settings` | 25–46 | `Schedule` (`List<ScheduleEntry>`), `ScheduleEnabled`, `FallbackStationId`, `LastStationId`, `Volume`, … | `Version = 1`. |
| `Occurrence` | 48–51 | `(ScheduleEntry Entry, DateTime At)` | `Key = $"{Entry.Id}:{At:yyyy-MM-ddTHH:mm}"`. |
| `Scheduler` | 53–78 | `TryTime`, `Evaluate`, `Conflicts` | Static. |
| `ScheduleSession` | 80–100 | `HoldCurrent`, `TakeChange` | Per-running-session dedup. |
| `SettingsStore` | 102–144 | `Load`, `Save`, `ValidUrl` | Atomic JSON round-trip. |

The slot time is stored as a **string** (`"08:00"`), parsed with `TimeOnly.TryParseExact(text, "HH:mm", …)`. There is no concept of a date or a timezone — only a wall-clock time + a set of weekdays.

### 1.2 Evaluation — `Scheduler.Evaluate(settings, now)`

Called with `now = DateTime.Now` (computer-local) everywhere. Algorithm:

1. Build a set of valid station ids.
2. For each enabled entry whose station still exists and whose `Time` parses:
   - For `offset` in **−7 … +7 days** (15 days total):
     - `date = now.Date.AddDays(offset)` — a **computer-local** calendar day.
     - If `date.DayOfWeek` is in `entry.Days`, add `Occurrence(entry, date.Add(time))`.
3. Return:
   - `Current` = the occurrence with the greatest `At <= now` (most recent start).
   - `Next` = the occurrence with the smallest `At > now` (soonest future start).
   - Both orderings end in `.ThenBy(o => o.Entry.Id)` — a deterministic tiebreak when two slots share an instant.

The ±7-day window exists so both `Current` (up to a week back) and `Next` (up to a week forward) are always found, including the "slot started on a previous day" case.

### 1.3 Session dedup — `ScheduleSession`

- `HoldCurrent(settings, now)` — records the current occurrence (only when `last == null || current?.At >= last.At`) so a manual `Play` isn't immediately overridden by the next tick.
- `TakeChange(settings, now, force)`:
  - Returns `null` immediately when `ScheduleEnabled` is false.
  - Resets `last = null` when there is no current occurrence.
  - Returns the current occurrence only if it is new: the guard is `if (!force && last != null && (current.Key == last.Key || current.At < last.At)) return null;`. `force: true` (used by `StartSchedule`/`RefreshSchedule`) skips the guard entirely, so an already-fired slot **is** replayed on a forced refresh — that is intended.
  - Handles wall-clock moving backwards (the `current.At < last.At` clause) so an older occurrence is not replayed.

### 1.4 Consumption — `RadioController`

`Next`, `StartSchedule`, `RefreshSchedule`, `CheckSchedule(force)`, `Tick()`, `ResumeFromSleep()`, `Play(manual)` are **identical in schedule semantics** on both platforms, and live at the **same line numbers**:

| Member | Lines (Win / Mac) | What it does |
|---|---|---|
| `Next` property | 36 / 36 | `Scheduler.Evaluate(settings, DateTime.Now).Next` — the "UP NEXT" footer. |
| `StartSchedule()` | 52 / 52 | `CheckSchedule(true)`. |
| `RefreshSchedule()` | 53 / 53 | `CheckSchedule(true)` + notify. |
| `CheckSchedule(force)` | 59–66 / 59–66 | `TakeChange` → if a new slot, `Play(station, manual:false)`. |
| `Play(manual)` | 68 / 68 | if `manual`, `HoldCurrent` (so manual selection sticks until the next slot). |
| `ResumeFromSleep()` | 111 / 111 | `CheckSchedule()` + force-reconnect. |
| `Tick()` | 185 / 185 | runs `CheckSchedule()` every 1 s (plus retry/fallback/stall logic). |

**QA-N1 — `Tick()` is NOT byte-identical across platforms.** The macOS `Tick()` has an extra wake-detection preamble (macOS only):

```csharp
// DialShift.Mac/RadioController.cs:188-192
var now = DateTime.UtcNow;
var gap = now - lastTick;
lastTick = now;
if (gap.TotalSeconds >= 15) ResumeFromSleep();
```

Windows `Tick()` begins with `CheckSchedule()` and reaches `ResumeFromSleep` only via the OS `SystemEvents.PowerModeChanged` event. The *schedule* logic is identical; the *resume path* differs. This matters for §5.5 and §9.2 case 12: a Windows-only smoke check will not exercise the Mac wake path.

**Key point:** every consumer only touches the `Occurrence.Entry`/`Occurrence.At` values that `Evaluate` returns. None of them reason about timezones themselves.

### 1.5 UI surface (both apps)

- **`ScheduleDialog`** (`DialShift/Dialogs.cs` 82–115, `DialShift.Mac/Dialogs.cs` 91–124): station combo → `Time` field → day-of-week checkboxes (+ Weekdays/Weekend/Every day presets) → Enabled toggle. On save it builds a `ScheduleEntry` and runs `Scheduler.Conflicts`.
- **`ShowSchedule`** (`MainWindow.cs` 167–193 / 159–187): a day-of-week tab row, then the slots filtered by the selected day (`.Where(e => e.Days.Contains(Week[selectedDay]))`) and ordered by `Time` string. Helper text: *"Times follow your local time zone."* / *"Times follow your Windows time zone."* (`MainWindow.cs` 186 Mac / 192 Windows).
- **`UpdatePlayer`** (method at `MainWindow.cs` 114 Windows / 106 Mac): `upNext.Text = … $"UP NEXT · {next.At:ddd HH:mm} …"` — `next.At` is already computer-local, so the display is inherently "local".
- **Footer** (`MainWindow.cs` 94 Windows / 85 Mac): already shows `"LOCAL TIME · " + TimeZoneInfo.Local.StandardName`.

### 1.6 Existing tests

- **`DialShift.Tests/Program.cs`** — deterministic checks including two named-for-DST cases at lines 34–43 (`2026-03-29 03:30` spring "skipped slot", `2026-10-25 03:30`/`03:40` fall "repeated hour"). **QA-N2:** these tests are *already* machine-independent — they inject `now` and `Scheduler.Evaluate` does pure `now.Date.AddDays(offset).Add(time)` wall-clock arithmetic that never reads `TimeZoneInfo`, so a UTC machine passes them too. They exercise **no** timezone machinery; the "DST" label is illustrative only.
- **`DialShift/SmokeChecks.cs`** 39–52 — integration checks that a just-added slot "catches up" and that a new occurrence overrides a manual selection.

### 1.7 Current timezone assumption

The whole pipeline assumes the slot's `Time`/`Days` are in the **computer's** local timezone. There is no conversion. This is correct only when the user's intended schedule zone equals the computer's zone — it breaks when a schedule is defined for a different zone (e.g. following a foreign station's program guide, or travelling).

---

## 2. Requirement — what "add timezone" means

A schedule slot must carry a timezone. The slot's `Time` + `Days` are interpreted **in that timezone**, then converted to the **computer's current local time**, and the **existing trigger logic runs unchanged on the converted local time**.

Concretely:

- Slot `"08:00 Europe/Athens"` on a computer set to `America/New_York` fires at the correct local instant (02:00 or 03:00 local, depending on DST) — not at 08:00 local.
- A slot with **no timezone** (null/empty) behaves exactly as today (computer-local) → **backward compatible** with existing `settings.json` files.

> "…based on the local time that the computer is in to trigger the existing logic" → convert the slot's zoned time to computer-local, then hand the result to the existing `Evaluate`/`ScheduleSession`/`CheckSchedule` machinery.

---

## 3. Key finding — the change is small and mostly isolated to Core

All time math lives in `Scheduler.Evaluate` (`DialShift.Core/Models.cs`). Everything downstream (`ScheduleSession`, `RadioController.CheckSchedule`/`Tick`/`ResumeFromSleep`/`Next`, the "UP NEXT" display) consumes the `Occurrence.At` that `Evaluate` produces. If `Evaluate` returns occurrences already expressed in **computer-local** time, the rest works **unchanged**.

Therefore the work is:

1. Add `ScheduleEntry.TimeZone` (nullable string).
2. In `Evaluate`, convert each candidate slot from the entry's zone to computer-local before building the `Occurrence`.
3. Make `Conflicts` timezone-aware.
4. Add a timezone picker to `ScheduleDialog` and show the zone on each slot row (both UIs).
5. Add tests + optional smoke check.

`RadioController` needs **no logic change** — though see **QA-N1** (resume paths differ) for test coverage.

---

## 4. Proposed design

### 4.1 Data model

- `ScheduleEntry.TimeZone` — `string?`, default `null`.
- `null` / empty / whitespace → "computer local time" (today's behavior).
- Otherwise an **IANA timezone id** (`"Europe/Athens"`, `"America/New_York"`, `"Asia/Kolkata"`).

**QA-N3 — do NOT bump `Settings.Version`; say why.** `SettingsStore.Load` treats any other version as corruption: it renames the file to `settings.json.unreadable-*` and resets to defaults. Keeping `Version = 1` means an old build reading a new file silently ignores the extra `TimeZone` (fires at the wrong local time, but keeps the user's stations/volume). Bumping to `2` would make old builds **wipe the user's data**. The correct choice is to stay at `1`. (Adding a user-editable string field also exposes a new hand-edit hazard: `"TimeZone": 123` throws `JsonException` → the same destructive reset path. Note it; do not try to "fix" it here.)

### 4.2 Timezone resolution

- **`ResolveZone(string?)` returns `null` for null/empty/whitespace.** The caller then takes the zero-conversion path; `TimeZoneInfo.Local` is **never** an object on the null path. (This is **QA-B1** — see §7/§8: returning `Local` for null would gap-shift phantom local wall times and break the existing DST test.)
- For a non-empty id: `TimeZoneInfo.FindSystemTimeZoneById(id)` inside a `try/catch (TimeZoneNotFoundException / InvalidTimeZoneException / ArgumentNullException)` → fall back to `null` (local) and log. **Trim the id before lookup** (a trailing space silently degrades a valid id to local, which is a wrong-fire-time bug, not a crash).
- **Cache** resolved `TimeZoneInfo` instances in a `ConcurrentDictionary<string, TimeZoneInfo>`, **but** see **QA-N4** below — the cache is about `FindSystemTimeZoneById`, which is *not* the dominant cost; the dominant cost is the per-candidate DST checks (§4.3) that run 15× per entry per `Evaluate`.

**QA-N4 — the real perf cost, and two cache correctness caveats.**
- **DST-offset staleness is NOT a problem** (a cached `TimeZoneInfo` holds adjustment *rules* and computes offsets per call, so it tracks DST correctly across a running app). This was the prior draft's stated worry and it is unfounded.
- **The real staleness** is that a cached `TimeZoneInfo` (and `TimeZoneInfo.Local`) snapshots OS tzdata at construction, so an OS tzdata update or a system-zone change while running stays invisible until `TimeZoneInfo.ClearCachedData()` or a restart.
- **The real perf cost** is that `IsInvalidTime` / `IsAmbiguousTime` / `ConvertTimeToUtc` / `ConvertTimeFromUtc` now run up to 15× per entry per `Evaluate`, and `Evaluate` runs ~1/s from `Tick` **plus** once per read of `RadioController.Next` (line 36 on both). The mitigation is not a zone cache but a **per-`(zoneId, localZoneId, zoneDate, time)` memo with day-change invalidation** (see §5.1).

### 4.3 Algorithm change in `Evaluate` (corrected sketch — not applied)

```csharp
public static (Occurrence? Current, Occurrence? Next) Evaluate(Settings settings, DateTime now, TimeZoneInfo? localZone = null)
{
    var local = localZone ?? TimeZoneInfo.Local;
    now = DateTime.SpecifyKind(now, DateTimeKind.Unspecified);   // QA-B2: see below
    var ids = settings.Stations.Select(s => s.Id).ToHashSet();
    var occurrences = new List<Occurrence>();
    foreach (var entry in settings.Schedule.Where(e => e.Enabled && ids.Contains(e.StationId)))
    {
        if (!TryTime(entry.Time, out var time)) continue;
        var zone = ResolveZone(entry.TimeZone);                  // null => local
        if (zone == null)
        {
            // unchanged zero-conversion path (today's behavior, byte-identical)
            for (var offset = -7; offset <= 7; offset++)
            {
                var date = now.Date.AddDays(offset);
                if (entry.Days.Contains(date.DayOfWeek))
                    occurrences.Add(new(entry, date.Add(time.ToTimeSpan())));
            }
            continue;
        }
        var nowInZone = TimeZoneInfo.ConvertTime(now, local, zone);
        for (var offset = -7; offset <= 7; offset++)
        {
            var zoneDate = nowInZone.Date.AddDays(offset);
            if (!entry.Days.Contains(zoneDate.DayOfWeek)) continue;
            var zoneWall = zoneDate.Add(time.ToTimeSpan());
            var at = ToComputerLocal(zoneWall, zone, local);
            occurrences.Add(new(entry, at));
        }
    }
    return (occurrences.Where(o => o.At <= now).OrderByDescending(o => o.At).ThenBy(o => o.Entry.Id).FirstOrDefault(),
            occurrences.Where(o => o.At > now).OrderBy(o => o.At).ThenBy(o => o.Entry.Id).FirstOrDefault());
}
```

Critical details — each one corresponds to a QA blocking defect:

- **The day-of-week is evaluated in the entry's zone** (`zoneDate.DayOfWeek`), not the computer's — otherwise a `Mon 23:00 UTC` slot would be grouped/fired on the wrong local day. The ±7-day window is also anchored in the entry's zone. **This is correct and was brute-force verified** (see §7): the window is *tight but sufficient* — the true Current/Next is always within ~2 h of `nowInZone`, and the weekday filter that pushes candidates to the edges is bounded by exactly ±7 zone-days.
- **QA-B2 — `localZone` injection requires `DateTimeKind.Unspecified` and 3-arg overloads.** `TimeZoneInfo.ConvertTime(DateTime.Now, injectedZone, dest)` throws `ArgumentException` ("Kind is Local, but the source time zone must be TimeZoneInfo.Local"). The 2-arg `ConvertTime(now, zone)` always uses the *machine's* zone as source, so it would silently ignore the injection and leave the machine-dependence §9 set out to remove. Fix: normalize `now = DateTime.SpecifyKind(now, DateTimeKind.Unspecified)` at the top, and use **only** the 3-arg overloads with the resolved `local`/`zone`.
- **`localZone` must also thread through `ScheduleSession.HoldCurrent`/`TakeChange`** — they call `Evaluate` internally, otherwise §9.2 case 11 (session dedup across DST) cannot be tested deterministically.
- `ToComputerLocal` must resolve the two DST edge cases explicitly — see §7 and **QA-B3**.

### 4.4 Conflict detection change

`Scheduler.Conflicts` currently compares only `Time` and `Days`. It must become zone-aware. **QA-N5** corrects the prior draft:

- Normalize each `TimeZone` with `string.IsNullOrWhiteSpace(x) ? "" : x.Trim()` first, then compare **resolved `zone?.Id`** (not the raw string, not `TimeZoneInfo.Local.Id`). Raw-string comparison would miss `null` vs `" "` (both mean local) and `"europe/athens"` vs `"Europe/Athens"` (case-insensitively equal in `FindSystemTimeZoneById`).
- Two slots **conflict iff** same resolved zone **and** same `Time` **and** overlapping days. Preserve the existing "editing the same entry is allowed" (`e.Id != candidate.Id`) behavior.
- **This is an approximation, not an exact "same instant" rule.** `Europe/Athens`↔`Europe/Helsinki`, `Europe/Athens`↔`Europe/Kiev`, `America/New_York`↔`America/Toronto`, `Asia/Kolkata`↔`Asia/Calcutta` fire at the same instant but compare unequal; `UTC`↔`Europe/London` collide half the year. No id-based rule can catch these. Document the limitation; it's acceptable for a conflict *hint*, not a correctness guarantee.

### 4.5 UI changes (both apps) — **QA-B4** governs the id source

- **`ScheduleDialog`**: add a "Time zone" `ComboBox`. **Do not** source it directly from `TimeZoneInfo.GetSystemTimeZones()` and store those ids — on Windows those are **Windows registry ids** (`"Pacific Standard Time"`, `"GTB Standard Time"`), not IANA ids, so a slot created on macOS (IANA `"Europe/Athens"`) would not be in the list on Windows and saving would silently rewrite it to "Local time" (**QA-B4**).
- **Correct approach — store IANA always, canonicalize at the boundary:**
  - Build the picker list by mapping each `GetSystemTimeZones()` entry through `TimeZoneInfo.TryConvertWindowsIdToIanaId(z.Id, out var iana) ? iana : z.Id`. On macOS this is the identity (already IANA); on Windows it yields the IANA id.
  - Add a "Local time" sentinel at the top that stores `null`.
  - Resolve at load time IANA-first, with an explicit `TimeZoneInfo.TryConvertIanaIdToWindowsId` fallback for ids that only resolve under the Windows scheme.
  - **Never** compare `entry.TimeZone == TimeZoneInfo.Local.Id`.
  - **Surface a missing/unresolvable id in the UI** (e.g. "(unknown zone)") rather than only in the log, so a cross-platform migration with a stale id is visible, not a silent fire-time change.
- **`ShowSchedule`**: display the zone (short IANA id or display name) on each slot row; reword the footer helper text.
- **`UpdatePlayer`** "UP NEXT": `next.At` is already local, so the fire *instant* is correct — but see **QA-N6** for a zone-label requirement.
- **QA-N6 — the day-tab and "UP NEXT" can disagree with the slot's zone day.** A `Mon 01:00 Asia/Kolkata` slot on a `Pacific/Pago_Pago` computer fires at local **Sunday 08:30** but is listed under the Monday tab (`selectedDay` is derived from local `DateTime.Now.DayOfWeek`). Show the zone and/or the next local fire time on the row; the zone label in "UP NEXT" is closer to *required* than nice-to-have.

---

## 5. Detailed code changes (file by file)

### 5.1 `DialShift.Core/Models.cs` — the core change

1. `ScheduleEntry`: add `public string? TimeZone { get; set; }`.
2. `Scheduler`: add `ResolveZone(string?)` — returns `null` for null/empty/whitespace, else `FindSystemTimeZoneById(Trim(id))` with fallback to `null` + log. Cache the resolved instances, but compare zones by `Id`, not reference (**QA-N4**).
3. `Scheduler`: add `ToComputerLocal(DateTime zoneWall, TimeZoneInfo zone, TimeZoneInfo local)` — explicit DST handling per **QA-B3**:
   ```csharp
   if (zone.IsAmbiguousTime(zoneWall))
       return zoneWall - zone.GetAmbiguousTimeOffsets(zoneWall).Max();   // fire once, at the earlier (daylight) instant
   if (zone.IsInvalidTime(zoneWall))
       return /* shift to first valid instant — see §7.1 */;
   return TimeZoneInfo.ConvertTime(zoneWall, zone, local);
   ```
4. `Scheduler.Evaluate`: implement §4.3 (null-zone path stays byte-identical; zoned path converts).
5. `Scheduler.Conflicts`: implement §4.4 (normalize → compare resolved `zone?.Id`).
6. `SettingsStore.Load`: tolerate/validate `TimeZone` (invalid ids deferred to resolve-time fallback, **not** a load failure — never let a bad id trigger the `.unreadable-*` destructive path).
7. Add the optional `TimeZoneInfo? localZone = null` parameter to `Evaluate` **and** thread it through `ScheduleSession.HoldCurrent`/`TakeChange` (**QA-B2**).
8. Consider a per-`(zoneId, localZoneId, zoneDate, time)` memo keyed on the local calendar day, invalidated when `now.Date` changes (**QA-N4**).

### 5.2 `DialShift.Tests/Program.cs` — new deterministic tests

Add cases (using explicit zones, a fixed `now`, and `SpecifyKind(Unspecified)` so they are **machine-independent by construction** — the existing DST cases already are, see **QA-N2**):

- null zone == local (backward compat) — asserts the byte-identical `At`, not just "fires".
- zone == local → identical fire time.
- east-offset conversion (`Europe/Athens` vs `UTC`).
- non-whole-hour offset (`Asia/Kolkata` +05:30).
- DST spring gap and **differing-zone** fall overlap (see §7; a same-zone ambiguous case is an identity conversion and passes under either policy, so it catches nothing — **QA-B3**).
- cross-midnight day boundary (slot `Mon 23:00` in one zone lands `Tue` locally).
- invalid stored id → fallback to local, no throw.
- conflict: same resolved zone/time → reject; different zone/same time → allow; `null` vs `" "` → reject.
- `Current`/`Next` still correct after conversion.
- session dedup across DST (needs `localZone` threaded through `ScheduleSession`).

### 5.3 `DialShift.Mac/Dialogs.cs` + `DialShift.Mac/MainWindow.cs` (Avalonia)

- Add the timezone `ComboBox` to `ScheduleDialog`; set `entry.TimeZone` on save. Use the §4.5 canonicalization (IANA ids via `TryConvertWindowsIdToIanaId`).
- `ShowSchedule`: show the zone on each row; reword helper text.

### 5.4 `DialShift/Dialogs.cs` + `DialShift/MainWindow.cs` (Windows WPF)

- Same as §5.3 using WPF `ComboBox` / `TextBlock`, with the §4.5 Windows-id→IANA mapping.

### 5.5 `RadioController` (both) — verify only

- No schedule-logic change expected; `CheckSchedule`/`Next`/`ResumeFromSleep` already operate on `Evaluate`'s output.
- **QA-N1:** remember the resume *path* differs (Mac timer-gap vs Windows `SystemEvents`), so test sleep/resume on both.

### 5.6 `DialShift/SmokeChecks.cs` (optional, Windows)

- Add one integration step: create a slot with an explicit zone, assert `Desired`/`Current` selection and the "UP NEXT" local time. (Does not cover the Mac wake path — see **QA-N1**.)

---

## 6. Timezone id strategy (cross-platform) — **corrected per QA-B4**

- **Store IANA ids** as the single on-disk format.
- **QA-B4 — the prior claim ("`TimeZoneInfo.Local.Id` / `GetSystemTimeZones()` return IANA ids on both Windows and macOS") is FALSE.** On Windows, `TimeZoneInfo.Local.Id` and `GetSystemTimeZones()` return **Windows registry ids** (`"Pacific Standard Time"`, `"GTB Standard Time"`). ICU globalization changes which ids can be *resolved* (`FindSystemTimeZoneById` will accept an IANA id), not the id *scheme* returned. `TryConvertWindowsIdToIanaId`/`TryConvertIanaIdToWindowsId` exist precisely to bridge this.
- **Canonicalization lives at the boundaries only:**
  - UI picker: map each `GetSystemTimeZones()` entry → IANA via `TryConvertWindowsIdToIanaId`.
  - Resolve: IANA-first, `TryConvertIanaIdToWindowsId` fallback.
  - Never persist a Windows id; never compare `entry.TimeZone` to `TimeZoneInfo.Local.Id`.
- **Constraint to record:** the Windows project must **not** later set `<InvariantGlobalization>true</InvariantGlobalization>`, or timezone resolution changes to NLS mode. Flag in review if it ever appears.

---

## 7. DST & edge cases (must be handled and QA-verified)

The conversion direction, day-of-week anchor, ±7-day window, and `At <= now`/`At > now` selection were **brute-force verified across 51,484,800 sampled instants with 0 mismatches** (including every DST transition, ±14/−11 extreme offsets, 30-min DST, +12:45, and midnight transitions). The remaining edge cases are about *policy*, not *math*:

1. **Spring-forward gap (invalid wall time in the entry's zone).** A slot at `02:30` on a day that jumps `02:00 → 03:00` names a time that never occurs. `ConvertTime` throws. **Resolution:** shift to the first valid instant. **QA-N7:** this matches today's *effective* firing instant (the null-zone code reports `At = 02:30` and fires on the first tick after the real `03:00`), but the prior draft's rationale was wrong — the choice "shift to first valid instant" is correct *because the alternative* (shift by the gap duration, `java.time`-style → `03:30`) would fire **30 min later than today**. State that. Also note two **unhandled sub-cases** the prior draft omitted: (a) the day-of-week filter runs on `zoneDate` *before* the shift, so zones whose gap crosses midnight (`America/Santiago`, `Asia/Beirut`, Cuba — 00:00→01:00) land the shifted instant on a **different calendar day** than the matched weekday; (b) whole-day gaps (`Pacific/Apia` 2011-12-30, `Pacific/Kiritimati` 1994-12-31) fire the next day. These are correct-by-construction but should be in the test matrix.

2. **Fall-back overlap (ambiguous wall time).** A slot at `02:30` on a day that repeats `02:00 → 03:00` occurs twice. **QA-B3 — the prior draft's policy was the *opposite* of what `ConvertTime` does.** `ConvertTime` on an ambiguous time uses the **standard** offset (fires the *later* instant); the doc recommended the daylight (earlier) instant. That's a silent 1-hour error. **Resolution (explicit):**
   ```csharp
   if (zone.IsAmbiguousTime(zoneWall)) return zoneWall - zone.GetAmbiguousTimeOffsets(zoneWall).Max();
   ```
   (fire once, at the earlier/daylight instant), and **add a differing-zone ambiguous test** — a same-zone ambiguous case is an identity conversion and passes under either policy, so it cannot catch this.

3. **Non-whole-hour offsets** (India `+05:30`, Nepal `+05:45`, Chatham `+12:45`): day boundaries shift; the ±7-day window and day-of-week check still resolve current/next correctly (brute-forced).

4. **Day-of-week is in the entry's zone** (§4.3) — the single most important behavioral change.

5. **Slot near midnight** crossing into a different local day.

6. **Zone/OS change while running.** **QA-N4 + QA-N8:** `TimeZoneInfo.Local` and cached instances snapshot OS tzdata at construction, so an OS zone change or tzdata update is invisible until `ClearCachedData()`/restart. The prior claim "`TimeZoneInfo.Local` reflects the OS's current rules on each call" is false. There is no automatic fix — document that a zone change requires a restart (or periodic `ClearCachedData()`, out of scope).

7. **Settings copied between machines/OSes.** Stored IANA ids resolve on both (with the §4.5 Windows-id→IANA canonicalization). Missing/invalid id → fallback to local **and surface in the UI**, never crash, never hit the `.unreadable-*` path.

8. **QA-N8 — a zone change can suppress the catch-up fire.** `ScheduleSession.TakeChange` drops a change when `current.At < last.At`. With conversion, a zone edit or OS zone change moves the *same* occurrence's local `At` by up to ~26 h **backwards**, so the guard swallows a genuinely new Current and the app keeps playing the previous station until the next slot. (For same-zone/same-entry occurrences the guard is safe — consecutive occurrences are ≥24 h apart while offsets move ≤2 h, so `At` cannot invert.) **Mitigation:** key the dedup guard on `Occurrence.Key`/zone-wall identity, or reset `last` when zone data changes.

9. **`Occurrence.At` is a local wall clock — never Utc.** **QA-N9:** `DateTime.Date` preserves `Kind`, so the null path yields `At.Kind == Local` while the converted path yields `Unspecified`. Harmless for today's tick comparisons and `Key` formatting, but nothing may ever call `.ToLocalTime()`/`.ToUniversalTime()` on `Occurrence.At`. Pin this invariant in the code comment.

---

## 8. Risks & gotchas

- **Backward compatibility is non-negotiable** and only holds if **QA-B1** is respected: `ResolveZone(null)` must return `null` (zero-conversion path), never `TimeZoneInfo.Local`. Otherwise the null path gap-shifts phantom local wall times and fails `DialShift.Tests/Program.cs:35`.
- **Test determinism (corrected, QA-N2):** the existing DST tests are already machine-independent (pure wall-clock arithmetic). The *new* tests must be made deterministic via `SpecifyKind(Unspecified)` + the `localZone` injection threaded through `ScheduleSession` (**QA-B2**) — not by relying on the machine's zone.
- **Conflict change** must keep "editing the same entry is allowed" and "different days do not conflict" green, and must be understood as an *approximation* (**QA-N5**).
- **UI list ordering** (`OrderBy(e => e.Time)`) is per-day display only; but the *day grouping itself* can be wrong for cross-zone slots (**QA-N6**) — show the zone on the row.
- **Perf**: the real cost is per-candidate DST checks, not zone lookup (**QA-N4**) — memoize per day.
- **Data safety**: do not bump `Settings.Version`; do not let an invalid `TimeZone` value reach the `.unreadable-*` path (**QA-N3**).

---

## 9. QA plan

### 9.1 Scope
- Verify backward compatibility (null zone).
- Verify conversion correctness incl. DST (both directions, **differing-zone** cases) and non-hour offsets.
- Verify both UIs (Windows + macOS) edit/display/persist the zone, and that the id canonicalization is IANA-consistent across a Mac→Windows migration.
- Verify no regression in retry / fallback / sleep-resume (both resume paths — **QA-N1**).

### 9.2 Test matrix

| # | Scenario | Setup | Expected |
|---|---|---|---|
| 1 | Null zone | existing settings | identical to today (byte-identical `At`) |
| 2 | Zone = computer-local | slot zone == OS zone | identical fire time |
| 3 | East offset | `08:00 Europe/Athens`, PC = UTC | fires 06:00 UTC (winter) |
| 4 | DST spring gap | `02:30` zone on jump day | fires once, first valid instant; same *instant* as today |
| 5 | DST fall overlap (**differing-zone**) | `02:30 Europe/Athens`, PC = New York | fires at the earlier/daylight local instant (**QA-B3**) |
| 6 | Non-hour offset | `Asia/Kolkata` +05:30 | correct instant |
| 7 | Cross-midnight day | `Mon 23:00` UTC, PC +2 | local `Tue 01:00`, correct grouping |
| 8 | Invalid stored id | `"Bad/Zone"` in settings | fallback to local + UI flag + log, no crash |
| 9 | Conflict same zone/time | two `08:00` same zone | rejected |
| 10 | Conflict diff zone, same time | `08:00` UTC vs `08:00` local | allowed |
| 11 | Session dedup across DST | ambiguous slot | fires once (needs threaded `localZone`) |
| 12 | Sleep/resume | resume into a zone-shifted slot | reconnects current slot — **verify on macOS (timer-gap) AND Windows (SystemEvents)** |
| 13 | UI round-trip | set zone, save, reload | persists (both apps) |
| 14 | Mac→Windows migration | settings.json with IANA id opened on Windows | id survives; picker shows it; no silent rewrite (**QA-B4**) |
| 15 | Whole-day / midnight gap | Santiago/Beirut/Cuba-style 00:00→01:00 gap | fires on the (correct) shifted day (**QA-N7**) |

### 9.3 Push-back process
QA reports each issue with: **scenario**, **expected vs actual**, **repro steps**, **severity (BLOCKING / NON-BLOCKING)**, and **affected file/line**. Blocking issues must be resolved before implementation begins; non-blocking issues are recorded as follow-ups.

### 9.4 QA findings already folded in
- **QA-B1** `ResolveZone(null)` must return `null` (§4.2/§8).
- **QA-B2** `localZone` injection needs `SpecifyKind(Unspecified)` + 3-arg `ConvertTime` + threading through `ScheduleSession` (§4.3/§5.1).
- **QA-B3** fall-back policy must be explicit `IsAmbiguousTime`/`GetAmbiguousTimeOffsets().Max()`, and needs a differing-zone test (§7.2).
- **QA-B4** Windows returns Windows ids, not IANA; canonicalize at the boundary (§4.5/§6).
- **QA-N1** `Tick()` resume path differs Mac vs Windows (§1.4).
- **QA-N2** existing DST tests already machine-independent (§1.6/§8).
- **QA-N3** do not bump `Settings.Version` (§4.1).
- **QA-N4** cache/perf: zone-cache is not the cost; OS tzdata snapshot is the real staleness (§4.2).
- **QA-N5** conflict rule is id-equality approximation, needs normalization (§4.4).
- **QA-N6** day-tab/UP NEXT can disagree with the slot's zone day (§4.5).
- **QA-N7** gap policy rationale + midnight/whole-day gap sub-cases (§7.1).
- **QA-N8** zone change can suppress catch-up via `current.At < last.At` (§7.8).
- **QA-N9** `Occurrence.At` Kind invariant (§7.9).

---

## 10. Open questions / decisions needed

1. **Per-entry zone vs one schedule-level zone?** — recommend **per-entry** (most flexible, backward-compatible via null default).
2. **Default picker value** — recommend "Local time" (stores `null`).
3. **IANA-only ids, canonicalized at the boundary?** — recommend yes (required by **QA-B4**).
4. **Inject `localZone` into `Evaluate` (and `ScheduleSession`) for tests?** — recommend yes (**QA-B2**).
5. **Show the zone in "UP NEXT"?** — recommended **yes** (required for cross-zone slots, **QA-N6**).
6. **Zone-change handling** — recommend "restart required" for now, documented (**QA-N4/N8**).

---

## 11. Summary brief — the changes to implement

**`DialShift.Core/Models.cs`**
- Add `ScheduleEntry.TimeZone` (string?).
- Add `Scheduler.ResolveZone` (returns null for local) + `ToComputerLocal` (explicit DST per §7.1/§7.2).
- Rework `Scheduler.Evaluate` to convert zoned times to computer-local (null path byte-identical; `SpecifyKind(Unspecified)` + 3-arg `ConvertTime`).
- Rework `Scheduler.Conflicts` to be zone-aware (normalize → compare resolved `Id`).
- Tolerate/validate `TimeZone` in `SettingsStore.Load` (never trigger `.unreadable-*`).
- Add `localZone` parameter to `Evaluate`, threaded through `ScheduleSession.HoldCurrent`/`TakeChange`.
- Add per-day memo for DST checks.

**`DialShift.Tests/Program.cs`**
- Add deterministic timezone/DST/conflict/session-dedup tests (differing-zone ambiguous case included).

**`DialShift.Mac/Dialogs.cs` + `DialShift.Mac/MainWindow.cs`**
- Timezone `ComboBox` in `ScheduleDialog` (IANA-canonicalized); show zone + next fire time per slot; reword helper text.

**`DialShift/Dialogs.cs` + `DialShift/MainWindow.cs` (Windows)**
- Same as macOS, with Windows-id→IANA mapping in the picker.

**`DialShift/SmokeChecks.cs`** *(optional)*
- One zoned-slot integration step.

**`RadioController` (both)** — no schedule-logic change expected; verify only (and remember the resume-path difference, **QA-N1**).

**Docs** — update README "Listen"/"Schedule" wording to mention per-slot timezones and the "restart on zone change" caveat.

---

## 12. Quality gates (added 2026-09-25)

Apply to every step and artifact in this brief:

- **No dead code:** UI changes remove the old single-zone assumptions cleanly — no orphaned helpers, no commented-out fallbacks, no unreferenced timezone code paths; grep for stale references in the same change that removes them.
- **Documentation always up to date:** README schedule/timezone wording, this brief, and the test matrix are updated in the SAME change as the code they describe.
- **Best UI/UX:** the timezone picker is discoverable and readable — "Local time" sentinel as default, zone + next local fire time visible per slot, "(unknown zone)" surfaced for stale ids, helper text reworded to match behavior.
- **Prod-ready test-and-fix:** the loop is test → fix → retest, never test → report. The full §9.2 matrix (both resume paths, QA-N1) must be green, and every BLOCKING defect fixed, before the feature ships.

---

## 13. Implementation notes (2026-09-25)

Implemented in `30cc8b5` (Core), `eef6168` (the `Timezone` suite), `5ef803b` (UI and the `UiTimeZone` suite) and `5b2619b` (picker coverage and search). The code follows §4–§7 except where this section says otherwise. The QA items and the §9.2 rows are tracked, with evidence, in the acceptance matrix §6. Decisions D12, D43, D47 and D48 in `docs/decisions.md` record the choices.

### 13.1 Where it lives

| Plan (§5, §11) | Implemented in |
|---|---|
| `ScheduleEntry.TimeZone` | `DialShift.Core/Models/ScheduleEntry.cs`. A null zone is not written (`JsonIgnore(WhenWritingNull)`), so a schedule without zones saves exactly as before. `Settings.Version` stays 1 (QA-N3). |
| `ResolveZone`, `ToComputerLocal`, `Evaluate`, `Conflicts`, the memo | `DialShift.Core/Scheduling/Scheduler.cs`: `TryResolveZone` (returns `ZoneResolution` Local, Resolved or Unknown), `ResolveZone`, `ToComputerLocal`, `Evaluate(settings, now, localZone)`, `NextFor` (a row's next local start, QA-N6), `Conflicts`, and a per-local-day conversion memo (QA-N4). |
| `Occurrence` | `DialShift.Core/Models/Occurrence.cs`: adds `Zone`, `ZoneWall` (the slot's own wall time) and `ZoneResolution`. `Key` is culture-invariant and names the zone wall time (CF-01, QA-N8). |
| `localZone` through the session | `DialShift.Core/Scheduling/ScheduleSession.cs`. `PlaybackCoordinator` passes its injected zone to every schedule call. |
| `SettingsStore.Load` | Unchanged validation: any `TimeZone` string loads, and an unknown id falls back at evaluation (QA-N3). `"TimeZone": 123` still takes the recovery path (CT-SET-11, pinned as a hazard). |
| Picker, rows, UP NEXT (§4.5, §5.3, §5.4) | `DialShift.App` only: `ViewModels/TimeZoneCatalog.cs`, `TimeZoneOption.cs` (`TimeZoneChoices`), `ScheduleEditorViewModel.cs`, `SchedulePageViewModel.cs` (`SlotRowViewModel`), `UiText.UpNext`/`ZoneName`, and the two views. `AppComposition` routes `schedule.zone_unknown` to the app log (`Scheduler.Log`). |
| Tests (§5.2, §9.2) | `DialShift.Tests/Core/TimezoneTests.cs` (rows 1–11 and 15, every QA item, a coordinator integration check) and `DialShift.Tests/Ui/TimeZoneUiTests.cs` (rows 8–10, 13 and 14 through the real views, QA-B4, QA-N6). Both run on `windows-latest` and `macos-latest`. |

### 13.2 Deviations and corrections

1. **The null path keeps `At.Kind` (D47).** The §4.3 sketch normalizes `now` with `SpecifyKind(Unspecified)` at the top of `Evaluate`. That would change the null path's `At.Kind` from `now.Kind` to `Unspecified`, so the path would no longer be byte-identical (QA-B1, TZ-01). The implementation normalizes only for zoned slots (the `LocalClock` helper computes the instant once, only when a zoned slot needs it). A slot with no zone, or with an id this computer can't resolve, uses `now` exactly as before. QA-N9 holds as written: `At.Kind` is `now.Kind` on the null path and `Unspecified` on the converted path. Row 1 compares the null path against a re-statement of the Phase 1 `Evaluate` over 2026 for four blank spellings and three `Kind`s.
2. **`IsInvalidTime` misses base-offset gaps.** `TimeZoneInfo.IsInvalidTime` and `IsAmbiguousTime` model only daylight-saving adjustment rules. A change of base offset is invisible to them: `Pacific/Apia` skipped 2011-12-30 entirely, yet `IsInvalidTime` reports that day as valid, and the §5.1 sketch would convert it to a wrong instant. The implementation handles ambiguity with `IsAmbiguousTime` as planned (QA-B3). For everything else it converts back: it tries each UTC offset in force within a day of the wall time and keeps the earliest instant whose conversion back gives the same wall time. When none does, the time is in a gap, and a binary search on whole seconds finds the first instant after it (QA-N7). One rule covers DST gaps, midnight gaps (Santiago) and whole-day gaps (Apia).
3. **Dedup rule (QA-N8, CF-02; D48).** §7.8 offered two mitigations. The implementation keys identity on the zone wall time and keeps a narrower age check. `Occurrence.Key` is `{Entry.Id}:yyyy-MM-ddTHH:mm` of `ZoneWall`, plus `@{Zone.Id}` for a zoned slot, so one start keeps one key when the computer's zone changes. `ScheduleSession` fires a current occurrence whose key differs from the last one fired or held when it is not older, compared in the same computer zone. An older occurrence fires only if it became current while the wall clock ran forward: a slot edit, a zone edit, a zone change, or a slot reached after a backward clock correction (CF-02). When the wall clock itself moves backward (a correction, a DST fall-back), the older occurrence that becomes current is adopted silently, so last week's slot is never replayed. The Phase 1 pins CT-SES-08 and "Backward clock jump: Tue 09:00 slot is suppressed" were flipped to the fixed behavior.
4. **Windows picker coverage and legacy ids (QA-B4, UI-D3; D43).** Mapping each Windows zone through `TryConvertWindowsIdToIanaId(id)` (§4.5) gives one IANA id per Windows zone: `GTB Standard Time` becomes `Europe/Bucharest`, so a search for "athens" found nothing on Windows. `TimeZoneCatalog` also asks the region-aware overload once per ISO region the computer's cultures know, so every IANA id CLDR maps to a Windows zone is offered (`Europe/Athens` for GR, `Asia/Nicosia` for CY). CLDR still spells some ids the old way. `TimeZoneCatalog.Renamed` maps those to current tzdata names when the new name resolves (for example `Asia/Calcutta` → `Asia/Kolkata`, `Europe/Kiev` → `Europe/Kyiv`, 14 entries), so Windows and macOS store the same id and meet in a conflict check. On macOS the system list is offered as it is. A stored id that isn't in the list (an alias, a Windows id someone typed, an unknown id) is kept as its own entry and is never rewritten by an untouched save.
5. **Display casing comes from the stored id.** Rows, UP NEXT, the delete question and the editor show the stored id, trimmed (`UiText.ZoneName`), never `TimeZoneInfo.Id`. .NET caches a zone under whichever spelling resolved it first, so a stored `europe/athens` can make `Id` lower-case for the whole process.
6. **Windows zone data lacks Apia 2011.** The Windows time-zone data does not contain Apia's 2011 date-line change. The Row 15 Apia check checks the host's data first and reports SKIP on `windows-latest`. It runs on macOS. The Santiago midnight-gap check runs on both.
7. **WPF is retired: §5.4 is superseded.** The WPF front-end was deleted before Phase 2 (D8), and `DialShift.Mac/` became `DialShift.App/`. The picker, rows and UP NEXT exist once, in `DialShift.App`, for both OSes. §5.3's file names and the optional §5.6 `SmokeChecks.cs` step no longer apply. The native smoke has no zoned-slot step; the headless `UiTimeZone` suite and the coordinator integration check in `TimezoneTests` cover it.
8. **An unknown id is visible, not only logged.** `TryResolveZone` returns `ZoneResolution.Unknown`, and it logs `schedule.zone_unknown` once per id through `Scheduler.Log`, which the composition root points at `dialshift.log`. The row shows "Europe/Foo (unknown zone)" and "Runs on local time" in the amber warning color, UP NEXT adds "(unknown zone)", and the editor keeps the id selected with an explanation. For conflicts an unknown id counts as local time, because that is how it fires.
9. **Conflict comparison.** Resolved ids are compared case-insensitively, because on macOS `FindSystemTimeZoneById("europe/athens")` resolves and reports the spelling it was given.

### 13.3 Open

- **§9.2 row 12 (TZ-12).** The coordinator's wake tests (CT-PB-29..34, both wake sources) and its zoned-slot integration check pass separately. No check yet wakes the coordinator into a slot defined in another zone. The unit variant is TODO (test lane), and the native half is NC-02 (Windows) and NC-08 (macOS).

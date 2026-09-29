using DialShift.Core;
using DialShift.Core.Playback;
using static DialShift.Tests.TestHarness;

namespace DialShift.Tests.Core;

/// <summary>
/// The Add dialog's quick-play preview on the coordinator (docs/acceptance-matrix.md §15 QM-01..09; decision D121): a
/// transient stream started by <see cref="PlaybackCoordinator.PlayPreviewAsync(string, string)"/> that is never stored in
/// <see cref="Settings.Stations"/>, never writes <see cref="Settings.LastStationId"/>, never falls back to
/// <see cref="Settings.FallbackStationId"/>, retries its own URL, stops hard to the idle snapshot (unlike a pause), and is
/// adopted in place by a matching <see cref="PlaybackCoordinator.PlayAsync"/>. Time moves only through the rig's fake
/// clocks (§7.1 <c>Step(n)</c>), so nothing depends on the host.
/// </summary>
public static class PlaybackPreviewTests
{
    private const string PreviewUrl = "https://streams.example.org/kosmos";
    private const string PreviewName = "Kosmos 93.6";
    private const string SecondUrl = "https://streams.example.org/melodia";
    private const string SecondName = "Melodia 99.2";

    public static async Task RunAsync()
    {
        await StartsTheUrlWithoutStoringIt();
        await FailedPreviewRetriesItsOwnUrl();
        await StopPreviewIsAHardClear();
        await MatchingPlayPromotesInPlace();
        await PreviewHoldsTheSlotAndIsSuperseded();
    }

    // ─── QM-01 / QM-08: a transient stream, never stored, never the last-played station ───

    private static async Task StartsTheUrlWithoutStoringIt()
    {
        await using var rig = CoordinatorRig.Create(slots: false);
        Check("QM-08 fixture: three saved stations, no last-played one, nothing started",
            rig.Settings.Stations.Count == 3 && rig.Settings.LastStationId == null && rig.StartCount == 0);

        await rig.Coordinator.PlayPreviewAsync(PreviewUrl, PreviewName);

        Check("QM-01 QM-08 PlayPreviewAsync starts exactly one engine session on the given URL, under the dialog's display name",
            rig.StartCount == 1 && rig.LastStart.Source.Url == new Uri(PreviewUrl) && rig.LastStart.Source.DisplayName == PreviewName);
        Check("QM-08 the transient station is never added to Settings (the three saved stations are untouched)",
            rig.Settings.Stations.Count == 3 && rig.Settings.Stations.All(s => s.Url != PreviewUrl));
        Check("QM-08 PlayPreviewAsync writes no LastStationId", rig.Settings.LastStationId == null);
        Check("QM-01 the snapshot is an ordinary Connecting start of the preview's own (unsaved) station id, not a fallback",
            rig.Status == PlaybackStatus.Connecting && rig.S.CurrentStationName == PreviewName && rig.S.DesiredStationName == PreviewName
            && rig.S.DesiredStationId is { } id && rig.Settings.Stations.All(s => s.Id != id) && !rig.S.IsFallback && rig.S.RetryInSeconds is null);
        rig.Playing();
        Check("QM-01 the engine playing for the preview's session gives the ordinary live snapshot (the transient station carries the default tag)",
            rig.Status == PlaybackStatus.Playing && rig.S.IsPlaying && rig.S.StatusText == Texts.Live && rig.S.TrackText == "Internet radio");
    }

    // ─── QM-08: a failed preview retries the same URL, never the fallback ───

    private static async Task FailedPreviewRetriesItsOwnUrl()
    {
        await using var rig = CoordinatorRig.Create(slots: false);
        rig.Settings.FallbackStationId = rig.B.Id;
        await rig.Coordinator.PlayPreviewAsync(PreviewUrl, PreviewName);

        var waits = new[] { 3, 6, 30 };
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            rig.Fail();
            var wait = waits[attempt - 1];
            Check($"QM-08 preview failure #{attempt}: Failed with the normal {wait} s backoff, still not a fallback, LastStationId still unset",
                rig.Status == PlaybackStatus.Failed && rig.S.RetryInSeconds == wait && rig.S.StatusText == Texts.Retry(wait) && rig.S.TrackText == Texts.RetryTrack
                && !rig.S.IsFallback && rig.Settings.LastStationId == null);
            await rig.Step(wait);
            Check($"QM-08 preview failure #{attempt}: the retry after {wait} s re-attempts the same URL, never the fallback station, still writing no LastStationId",
                rig.StartCount == attempt + 1 && rig.LastStart.Source.Url == new Uri(PreviewUrl) && rig.LastStart.Source.DisplayName == PreviewName
                && !rig.S.IsFallback && rig.Status == PlaybackStatus.Reconnecting
                && rig.S.CurrentStationId is { } current && current != rig.B.Id && rig.Settings.LastStationId == null);
        }

        Check("QM-08 three failures in, four attempts, not one of them the configured fallback B, and no LastStationId ever written",
            rig.StartCount == 4 && rig.Log.Entries.All(e => e.EventName != "playback.fallback") && rig.Settings.LastStationId == null);
    }

    // ─── QM-03 / QM-06: stopping a preview is a hard clear, not a pause ───

    private static async Task StopPreviewIsAHardClear()
    {
        await using var rig = CoordinatorRig.Create(slots: false);
        await rig.Coordinator.PlayPreviewAsync(PreviewUrl, PreviewName);
        rig.Playing();
        Check("QM-03 fixture: the preview is playing (active, with a station)",
            rig.Status == PlaybackStatus.Playing && rig.S.IsActive && rig.S.CurrentStationId != null);

        await rig.Coordinator.StopAsync();

        Check("QM-03 QM-06 stopping a preview fully clears it: Stopped with no desired or current station at all, active false",
            rig.Status == PlaybackStatus.Stopped && !rig.S.IsActive && rig.S.DesiredStationId is null && rig.S.DesiredStationName is null
            && rig.S.CurrentStationId is null && rig.S.CurrentStationName is null);
        Check("QM-03 … with the first-launch idle texts (\"Ready when you are\" / \"Choose a station and make yourself at home.\"), not the Paused ones",
            rig.S.StatusText == Texts.Ready && rig.S.TrackText == Texts.ReadyTrack);
        Check("QM-03 QM-08 the engine session is stopped, no station was added and no LastStationId was written",
            rig.Engine.ActiveSessionId is null && rig.Settings.Stations.Count == 3 && rig.Settings.LastStationId == null);

        // Contrast: stopping a normal play pauses with the station kept, which is exactly what a stopped preview must not be.
        await rig.Coordinator.PlayAsync(rig.A.Id);
        rig.Playing();
        await rig.Coordinator.StopAsync();
        Check("QM-03 stopping a normal play pauses (\"Paused\" / \"Press play to return to the live broadcast.\", the station kept), unlike a preview",
            rig.Status == PlaybackStatus.Stopped && rig.S.StatusText == Texts.Paused && rig.S.TrackText == Texts.PausedTrack
            && rig.S.DesiredStationId == rig.A.Id && rig.S.CurrentStationId == rig.A.Id && rig.Settings.LastStationId == rig.A.Id);
    }

    // ─── QM-04: a matching PlayAsync adopts the preview in place; a different URL does not ───

    private static async Task MatchingPlayPromotesInPlace()
    {
        await using var rig = CoordinatorRig.Create(slots: false, urls: [null!, null!, PreviewUrl]);
        Check("QM-04 fixture: station C's stream URL is the preview URL", rig.C.Url == PreviewUrl);

        await rig.Coordinator.PlayPreviewAsync(PreviewUrl, PreviewName);
        rig.Playing();
        var session = rig.Session;
        var starts = rig.StartCount;

        await rig.Coordinator.PlayAsync(rig.C.Id);

        Check("QM-04 PlayAsync for the previewed URL adopts the station in place: the engine sees no new session, the running stream keeps playing",
            rig.StartCount == starts && rig.Session == session && rig.Engine.ActiveSessionId == session
            && rig.Engine.CallLog.Count(c => c.StartsWith("start:", StringComparison.Ordinal)) == 1);
        Check("QM-04 … the snapshot is the real station now (Desired = Current = C, Playing, not a fallback, no retry) and LastStationId is written",
            rig.Status == PlaybackStatus.Playing && rig.S.DesiredStationId == rig.C.Id && rig.S.CurrentStationId == rig.C.Id
            && rig.S.CurrentStationName == "Charlie" && !rig.S.IsFallback && rig.S.RetryInSeconds is null
            && rig.Settings.LastStationId == rig.C.Id);
        await rig.Step(1);
        Check("QM-04 a tick on the promoted station refreshes the track line to its tag (\"Tag C\"), like any playing station",
            rig.S.TrackText == "Tag C" && rig.Status == PlaybackStatus.Playing);

        await rig.Coordinator.StopAsync();
        Check("QM-04 after the promote it is no longer a preview: stopping the adopted station pauses and keeps it, it does not clear to idle",
            rig.S.StatusText == Texts.Paused && rig.S.DesiredStationId == rig.C.Id && rig.S.CurrentStationId == rig.C.Id && rig.Settings.LastStationId == rig.C.Id);

        // A PlayAsync whose URL is not the preview's is an ordinary play: the preview is superseded by a new session.
        await rig.Coordinator.PlayPreviewAsync(PreviewUrl, PreviewName);
        starts = rig.StartCount;
        await rig.Coordinator.PlayAsync(rig.A.Id);
        Check("QM-04 a PlayAsync for a different URL does not promote: a new engine session starts, the preview superseded",
            rig.StartCount == starts + 1 && rig.LastStart.Source.Url == new Uri(rig.A.Url) && rig.LastStart.Source.DisplayName == "Alpha"
            && rig.Status == PlaybackStatus.Connecting && rig.S.DesiredStationId == rig.A.Id && rig.S.CurrentStationId == rig.A.Id
            && rig.Settings.LastStationId == rig.A.Id && !rig.S.IsFallback);
    }

    // ─── QM-09: a preview holds the current occurrence and is superseded by StopAsync, PlayAsync, a second preview and a slot change ───

    private static async Task PreviewHoldsTheSlotAndIsSuperseded()
    {
        await using var rig = CoordinatorRig.Create(schedule: true);
        await rig.Coordinator.StartScheduleAsync();
        rig.Playing();
        Check("QM-09 fixture: slot A (09:00) is playing", rig.S.DesiredStationId == rig.A.Id && rig.StartCount == 1 && rig.Settings.LastStationId == rig.A.Id);

        await rig.Coordinator.PlayPreviewAsync(PreviewUrl, PreviewName);
        rig.Playing();
        Check("QM-09 a preview holds the current occurrence like a manual play: it plays from its own URL instead of a schedule start",
            rig.StartCount == 2 && rig.LastStart.Source.Url == new Uri(PreviewUrl) && rig.S.CurrentStationName == PreviewName && rig.S.IsPlaying);
        await rig.Step(60);
        Check("QM-09 the schedule does not interrupt the preview within the slot (60 ticks: still the preview, no extra start)",
            rig.StartCount == 2 && rig.S.CurrentStationName == PreviewName && rig.Status == PlaybackStatus.Playing);

        await rig.Coordinator.PlayPreviewAsync(SecondUrl, SecondName);
        Check("QM-09 a second preview supersedes the first: a new session on the new URL, LastStationId still the schedule's A",
            rig.StartCount == 3 && rig.LastStart.Source.Url == new Uri(SecondUrl) && rig.S.CurrentStationName == SecondName && rig.Settings.LastStationId == rig.A.Id);

        await rig.Coordinator.PlayAsync(rig.C.Id);
        Check("QM-09 PlayAsync supersedes the preview: an ordinary start of C, LastStationId written",
            rig.StartCount == 4 && rig.LastStart.Source.DisplayName == "Charlie" && rig.S.DesiredStationId == rig.C.Id && rig.Settings.LastStationId == rig.C.Id);

        await rig.Coordinator.PlayPreviewAsync(PreviewUrl, PreviewName);
        Check("QM-09 fixture: a fresh preview is playing before the slot change",
            rig.S.CurrentStationName == PreviewName && rig.Settings.LastStationId == rig.C.Id);
        rig.SetLocal(new DateTime(2026, 9, 14, 10, 59, 59));
        await rig.Step(1);
        Check("QM-09 the next schedule occurrence (B 11:00) supersedes the preview: the schedule starts B, never the transient station",
            rig.StartCount == 6 && rig.LastStart.Source.DisplayName == "Bravo" && rig.S.DesiredStationId == rig.B.Id
            && rig.S.CurrentStationId == rig.B.Id && rig.Settings.LastStationId == rig.B.Id);
        Check("QM-09 the preview never became the schedule's target: the three saved stations are exactly what they were",
            rig.Settings.Stations.Count == 3 && rig.Settings.Stations.All(s => s.Url != PreviewUrl));
    }
}

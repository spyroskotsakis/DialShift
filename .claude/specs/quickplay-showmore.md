# Spec — Add-station dialog: "Show more" + quick-play preview

Two UI/UX features in the Add-station dialog (`StationEditorDialog` + `StationEditorViewModel`).
Source of truth for the delegated lanes. Implement exactly; where a lane is named, it owns that file.

---

## Feature 1 — "Show more" in the results list

**Context.** `ApplySearch` calls `StationCatalogQuery.Search(catalog, text, filters)` with the default
cap (`StationCatalogQuery.DefaultCap = 50`) and gets `CatalogSearchResult { Items, TotalCount }`. When
`TotalCount > 50` the list is truncated and the footer says "Top 50 of N" / "Showing 50 of M matches".

**Required behavior.**
1. When `TotalCount > Results.Count`, show a **"Show more"** control in the results footer. Activating it
   loads the next page of matches and **appends** them to the existing list — without resetting
   `HighlightedResult` and without moving the user's scroll position; the overlay stays open.
2. Mechanism: re-run `StationCatalogQuery.Search` with a **larger `cap`** (page size 50 → 100 → 150 …),
   and append only the newly returned rows. Ranking is deterministic and total-stable, so the first N rows
   are identical and appending is correct. (Do **not** change `StationCatalogQuery` itself; it already takes `cap`.)
3. A new query (search-text or filter change) **resets the cap back to 50** — a fresh query starts at "top 50".
4. The footer count text must track the larger shown count (reuse/derive from `UiText.BrowseCount` /
   `UiText.ResultCount`), and the "Show more" control must **disappear** once `Results.Count == TotalCount`.
5. When appending, load logos **only for the new rows** (do not re-fetch logos already loaded).

**Files (ui-engineer owns):**
- `DialShift.App/ViewModels/StationEditorViewModel.cs` — track `shownCap` (reset on new query), `HasMoreResults`,
  `ShowMoreCommand`, and an apply path that appends without resetting highlight/scroll.
- `DialShift.App/Views/Dialogs/StationEditorDialog.axaml` — the "Show more" control in the results footer
  (`ResultsOverlay` docked-bottom `Border`, ~line 324), visible only when `HasMoreResults`.
- `DialShift.App/Views/Dialogs/StationEditorDialog.axaml.cs` — only if keyboard/tap wiring for the button is needed.
- `DialShift.App/ViewModels/UiText.cs` — a "Show more" label (and any count-phrasing tweak).
- Automation name for the button: **`ShowMore`** (keep headless tests deterministic).

**Acceptance.** At 780×650, with a filtered query returning >50 matches: the footer shows "Showing 50 of M";
clicking "Show more" appends the next page, keeps the current highlight and scroll, updates the count, and
hides itself when all M are shown. No dead code; docs updated in the same change.

---

## Feature 2 — Quick-play preview (play/stop) per station

**Context.** Catalog `StationCatalogEntry` has no `Guid` and is not in `settings.Stations`, so
`IPlaybackCoordinator.PlayAsync(Guid)` cannot preview it. The engine is coordinator-owned (D17) — a preview
must go through the coordinator, never a second engine.

### Core (core-engineer owns)

Add one public method to `IPlaybackCoordinator`:

```csharp
/// <summary>Plays a transient stream for the Add-station dialog's "quick listen" preview. The URL is
/// played directly: it is never added to <see cref="Settings"/>, never writes <see cref="Settings.LastStationId"/>,
/// never falls back to <see cref="Settings.FallbackStationId"/>, and is superseded by a later
/// <see cref="PlayAsync"/>, <see cref="StopAsync"/>, schedule change, or another preview. Retry re-attempts this
/// same URL. Stopping a preview clears it fully (idle state), unlike a normal pause.</summary>
Task PlayPreviewAsync(string url, string displayName);
```

**Required behavior (observable):**
1. `PlayPreviewAsync(url, name)` starts playback of `url` under `displayName`, with a transient `Station`
   (`Id = Guid.NewGuid()`, `Name = displayName`, `Url = url`, `VpnRegion = null`). It must **not** write
   `settings.LastStationId` and must **not** trigger `FallbackStationId` on failure.
2. **Retry**: a failed preview retries the **same transient URL** (normal backoff via `RetryPolicy`); it never
   selects the fallback station. Keep `RetryPolicy`'s public shape if possible; special-case previews at the
   coordinator's call site if needed.
3. **Stop is hard for a preview**: stopping an active preview (via `StopAsync`) fully clears `desired`/`current`/
   `desiredUrl` and the preview flag, returning the snapshot to the idle/stopped state with no station — NOT the
   normal "Paused · <station>, resume later" state.
4. **Seamless promote**: when `PlayAsync(Guid)` is called for a station whose `Url` equals the URL of the
   currently-active preview, adopt the real station as `desired`/`current` (write `settings.LastStationId`,
   set fallback/retry as a normal play would) **without restarting the engine stream**. This is what makes
   "continue after Add" glitch-free.
5. A preview **holds the current schedule occurrence** like a manual play (the schedule must not interrupt
   mid-preview within the current slot) and never becomes the schedule's target. `StopAsync()` / `PlayAsync` /
   a second preview / a schedule change each supersede a preview.

**Files (core-engineer owns):** `DialShift.Core/Playback/Contracts/IPlaybackCoordinator.cs`,
`DialShift.Core/Playback/PlaybackCoordinator.cs`, `DialShift.Core/Playback/RetryPolicy.cs` (only if required).
**Compile-only wiring (core-engineer also owns, keep the build green):** every `IPlaybackCoordinator`
implementer in tests — `DialShift.Tests/Ui/UiFakes.cs` (`FakeCoordinator`) and
`DialShift.Tests/Ui/PlaybackLoopTests.cs` (`CountingCoordinator`, which forwards to `inner`). Add the method
(journaled for `FakeCoordinator`, forwarded for `CountingCoordinator`); do **not** write assertions yet —
`test-engineer` does that in a later phase.

### UI (ui-engineer owns)

**Constructor.** `StationEditorViewModel` gains an `IPlaybackCoordinator coordinator` parameter (place it after
`IUiDispatcher dispatcher`, before `TimeSpan searchDelay`). Update its **only call site**,
`StationsPageViewModel.EditAsync`, to pass `Services.Coordinator`.

**Preview state + commands (VM):**
- `string? PreviewingUrl` — the URL currently being previewed, else null.
- `bool IsPreviewing => PreviewingUrl != null`.
- A toggle that, given a row or the detail entry, does: if `PreviewingUrl == target.Url` → `StopAsync()` and
  clear `PreviewingUrl`; else → `PreviewingUrl = target.Url` and `PlayPreviewAsync(target.Url, target.Name)`.
- Per-row toggle uses `row.Entry.StreamUrl` / `row.Entry.Name` (both already on `CatalogResultRow.Entry`);
  the detail toggle uses `SelectedEntry.StreamUrl` / `SelectedEntry.Name`.
- No `SnapshotChanged` subscription is needed: the dialog is a modal window, so the dialog VM is the sole
  driver of the preview while open; a local `PreviewingUrl` flag is sufficient to render play vs stop.

**Lifecycle (VM + caller):**
- `Save()` (Add mode only, `Original == null`) continues to `settings.Stations.Add(station)` and expose the
  added station as `Station? AddedStation { get; }` (null in edit mode). It does **not** start playback itself.
- `CloseRequested` wiring (currently `CloseRequested += (_, _) => Shutdown();`, line 105) becomes
  `CloseRequested += (_, result) => { Shutdown(); if (result != EditorResult.Saved) StopPreview(); }` —
  i.e. **Cancel or Delete stops the preview; Save leaves it playing** for the caller to promote.
- Caller `StationsPageViewModel.EditAsync`, in `case EditorResult.Saved`, after
  `CommitAsync(SettingsChange.Stations)`: if `editor.AddedStation is { } added`, then
  `await Services.Coordinator.PlayAsync(added.Id); await Services.Settings.SaveAsync();`
  (mirrors `ListenAsync`). This both **continues** a live preview (seamless promote, same URL) and
  **auto-starts** when the user never pressed play. Edit mode (`AddedStation == null`) plays nothing.

**View (XAML):** a play/stop `PathIcon` button, added in **two places**, both wired to the same toggle:
1. **Result row** (`CatalogResult` `DataTemplate`, ~line 141): a small play/stop button visible on the row
   hover (and on the highlighted row), in the row's right area next to `resultFrequency`.
2. **Detail pane** (`<Border Classes="detail">`, ~line 261): a persistent play/stop button in the detail
   pane's **top-right corner** (a small header row / docked top-right), shown while a station is selected.
- Icon = play when not previewing that row/entry, stop when `PreviewingUrl == thatUrl`.
- **No icon library exists** — use Avalonia `PathIcon` with inline SVG-path geometry:
  play `M8,5v14l11,-7z`, stop `M6,6h12v12H6z` (~16px). Match the app's accent/secondary brush tokens and the
  existing chip/tile sizing; do not add a package or an icon font.
- Automation names: **`Listen <name>`** when showing play, **`Stop <name>`** when showing stop (row and detail),
  so headless tests can assert state deterministically.

**Files (ui-engineer owns):** `DialShift.App/ViewModels/StationEditorViewModel.cs`,
`DialShift.App/ViewModels/StationsPageViewModel.cs`, `DialShift.App/Views/Dialogs/StationEditorDialog.axaml`,
`DialShift.App/Views/Dialogs/StationEditorDialog.axaml.cs` (if hover/tap wiring needed),
`DialShift.App/ViewModels/UiText.cs` (any label). No test assertions in this phase.

**Acceptance.**
- Hovering a result row shows a play button; clicking it plays that stream (stop icon appears).
- Clicking a row shows the detail pane with a persistent play/stop button in its top-right corner.
- Clicking stop (row or detail) stops the preview and both buttons return to play.
- Add with preview playing → station **keeps playing** without a restart (seamless promote).
- Add with no preview → station **auto-starts** playing.
- Cancel / window close → the preview **stops** (and the now-playing card returns to idle).
- At 780×650 nothing clips; the new button does not collide with the frequency column or the detail header.

---

## Lane order (no two lanes own the same file in the same phase)

1. **core-engineer** (Core preview + compile-only fake wiring; `dotnet build` + existing `Check` green) —
   parallel with **docs-engineer** (decisions/acceptance-matrix/README from this spec).
2. **ui-engineer** (both features) — gated on core being green.
3. **design-reviewer** (read-only UX audit) — parallel with **test-engineer** (real tests).
4. **qa-auditor** (adversarial review of the whole diff).

**Repo rules always:** `DialShift.Core` pure (no JSON attributes, no UI/OS refs); `Settings.Version` and
`schema_version` stay 1; no dead code; docs in the same change; push only the `private` remote; commit messages
end with `Co-Authored-By: Claude Code <noreply@anthropic.com>`.

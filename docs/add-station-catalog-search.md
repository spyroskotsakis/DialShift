# Add-Station Catalog Search — Searchable Station Catalog in the Add-Station Dialog

> **Status: implemented (2026-09-26, branch `feature/add-station-catalog-search`, prepared for release as `v0.4.0`:
> CHANGELOG `[0.4.0]`, not tagged yet; a full release before the by-hand checks, D92).** Phases 0–6 are done: the Add dialog searches a generated, checked-in `data/output/app-catalog.json`
> (8,270 stations at `8e38215`) that both packages carry, with five filters, a detail pane with notes, and manual entry
> kept. Acceptance: `docs/acceptance-matrix.md` §11, **15 GREEN and 3 NATIVE-PENDING** since public CI `36219966368`
> at `cceff38` passed on `windows-latest` and `macos-latest` (after D91); CAT-03, CAT-12 and CAT-14 wait only for
> by-hand checks: NC-18, NC-01's smoke from the extracted zip and its step (4b), NC-17 step (6b)
> (`docs/open-items.md` §2.11–§2.13). Where the build differs from this
> brief, the decisions win: D69 (the catalog is about 8,300 stations, not ~1,400; budget ≤ 10,000), D70 (`Search` takes
> a pre-folded `StationCatalogIndex`), D83 and D85 (the dialog's layout: the detail pane beside the form, the results
> over the form column), D84 (languages as single names) and the rest of D59–D92 in `docs/decisions.md`. The text below
> is the brief as frozen, kept as written.
>
> **Spec frozen 2026-09-25.** This is brief 3, after
> `single-codebase-refactor.md` (brief 1) and `schedule-timezone-research.md` (brief 2). Execution per §12
> by the orchestrator (goal prompt: `docs/add-station-catalog-search-goal.md`, ≤4,000 chars, same discipline
> as `claude-goal-execution.md`). All decisions taken during execution are recorded as D59+ (D58 is the local backup release path) in `docs/decisions.md`.
>
> **Phase 0 done (2026-09-25):** the six new agent files are in `.claude/agents/`, the contracts are frozen in
> `docs/catalog-contracts.md`, the CAT rows are in `docs/acceptance-matrix.md` §11, and §11's defaults are D59–D68.
> Phase 0 measured the real data and resolved these points of this brief (D69–D76): the catalog has 8,281 Working
> stations, not ~1,400, so the budget is ≤ 10,000 entries (D69); `Search` takes a pre-folded `StationCatalogIndex`
> instead of the entry list, because per-call `CompareInfo` matching measured over 10 ms (D70); the export dedupes per
> country and applies the app's 2,048-character URL limit (D71); `LatestValueDispatcher` only marshals, so the 200 ms
> debounce is a separate delay (D72); notes are kept only while the picked URL is kept (D73); the provider's degraded
> rules (D74); the local-macOS evidence gate while Actions are refused (D75); catalog-data notices (D76).

## 1. Context

Today adding a station means **manually copying three fields from a spreadsheet**:

- The Add dialog (`DialShift.App/Views/Dialogs/StationEditorDialog.axaml` + `StationEditorViewModel.cs`,
  "Add a frequency") has exactly three text fields — Name, Description/Genre, Stream URL — per BHV-52/53.
- The station catalog lives in `data/` (brief: `docs/radio-data-plan.md`, implemented): YAML
  (`countries/*.yaml`, `collections/*.yaml`) is the ONLY source of truth; one generic pipeline
  (`data/build/build_all.py`) merges curated facts + Wikipedia + radio-browser into
  `data/canonical/*.csv` and a multi-tab `data/output/dialshift-radio-catalog.xlsx`.
- The handoff to the app is the XLSX "Import Ready" tab: the user opens Excel/Numbers, finds a station,
  and copy-pastes Station name / Description / Stream URL into the dialog. Clunky, and the app itself
  knows nothing about the ~1,400 stations we already scraped, verified and curated.

The catalog already carries everything the dialog needs: `name`, `name_local`, `city`, `region`,
`frequency_fm`, `type`, `genre`, `language`, `political_leaning`, `internet_only`, `stream_url`, `codec`,
`bitrate`, `stream_status` (Working/Down), `votes`, `notes`, `logo`, `source`.

## 2. Goal

A **polished, searchable catalog experience inside the Add-station dialog**: the user types a station
name (or its frequency, city…) and picks from the catalog instead of copy-pasting. Concretely:

1. **Search dropdown** showing station name · city · FM frequency · country.
2. **Filter by country, city, type, genre, language** — dropdowns whose values come from the data itself.
3. **Notes shown in the UI** (detail pane), plus votes, language, and the logo when available.
4. **Data stays dynamic**: the app gets its catalog from `data/` **at build time** — never from
   hand-copied C# constants. Refresh = re-run the existing pipeline; zero app-code changes for new data.
5. Manual entry **remains fully supported** (the existing three fields stay; the catalog is additive).
   No new NuGet dependencies, no settings-version bump, both platforms, same quality gates as always.

## 3. What is already good (keep it)

- **YAML-only data** (`.claude/rules/data-catalog-only.md`): all station facts live in `data/countries/`
  and `data/collections/`; the Python is one generic pipeline. Adding a country = one YAML file, zero code.
- **Canonical CSVs** (`data/canonical/*.csv`, 18 frozen columns, checked in) are already app-ready rows;
  `stream_status == 'Working'` + non-empty `stream_url` already gate the "Import Ready" tab, and
  `app_tag()` in `build_all.py` already computes the exact Description/Genre text the dialog wants.
- **MVVM editor scaffolding**: `EditorViewModel` base (title/description/error/save/close/focus),
  `IDialogService`, the HS-02 headless harness, BHV-52/53 behavior checks, automation names on every field.
- **`LatestValueDispatcher`** (ViewModels) — the existing debounce primitive for search input.
- **Settings discipline**: the nullable `[JsonIgnore(Condition = WhenWritingNull)]` pattern on
  `ScheduleEntry.TimeZone` (QA-N3) is the proven recipe for adding an optional field without bumping
  `Settings.Version` (which is **never** bumped, per AGENTS.md).

## 4. Main improvements

1. **App-catalog snapshot** — the pipeline additionally emits `data/output/app-catalog.json` (one
   compact JSON, checked in like the XLSX). Schema in §5.
2. **Build wiring** — `DialShift.App.csproj` copies that file into the build/publish output
   (MSBuild `Content` item, no code, no new packages). `dotnet build` on a fresh clone therefore
   always has the latest checked-in catalog; the `.app`/zip scripts already consume the publish
   output, so both artifacts pick it up automatically.
3. **Core catalog engine (pure)** — `StationCatalogEntry` + a pure `StationCatalogQuery`
   (filter/rank, no I/O) in `DialShift.Core`, unit-testable like the scheduler.
4. **App catalog provider** — one loader (`ICatalogProvider`) that reads the JSON next to the apphost,
   with graceful degradation (missing/corrupt file → "catalog unavailable", manual entry still works).
5. **Dialog UX rebuild** — search + filters + results + detail pane (with notes) inside the Add dialog,
   per §6; edit mode stays the plain three-field form (BHV-53 unchanged).
6. **Optional `Station.Notes`** — shown in the detail pane and persisted with the station when it came
   from the catalog (nullable, version-safe).

## 5. Data contract (non-negotiable)

### 5.1 `data/output/app-catalog.json` schema

```json
{
  "schema_version": 1,
  "generated_utc": "2026-09-25T12:00:00Z",
  "stations": [
    {
      "name": "Antenne Bayern",
      "name_local": "Antenne Bayern",
      "country": "DE",
      "country_label": "Germany",
      "city": "Ismaning (Munich)",
      "region": "Bavaria",
      "frequency_fm": "101.5",
      "type": "Music",
      "genre": "Pop / Schlager",
      "language": "German",
      "internet_only": false,
      "stream_url": "https://…",
      "codec": "MP3",
      "bitrate": 128,
      "votes": 4210,
      "notes": "Bavaria's biggest private station.",
      "logo": "https://…"
    }
  ]
}
```

- `tag` (the app's Description/Genre text) is **precomputed by the pipeline** with the existing
  `app_tag()` logic and shipped as a field — the app never recomputes it. (Field added to the schema;
  the brief's exact field list above is the minimum, the pipeline may add `tag` and nothing else.)
- **Inclusion rules:** only `stream_status == 'Working'` with a non-empty http(s) `stream_url`.
  Collections included with `country == 'Internet'`, `country_label == 'Internet (collections)'`.
  Dedupe key identical to the pipeline's final dedupe (name + city + stream URL); deterministic order
  (country, then votes desc, then name).
- **Generated, never hand-edited; checked into git** like the XLSX (a fresh `dotnet build` needs no
  Python). `generated_utc` changes only when the pipeline runs — the app can show "catalog updated …".
- **Validation in the pipeline** (same run, hard fail): every entry has non-empty `name` + valid
  `stream_url`; no duplicate `(name, country, stream_url)`; `schema_version == 1`; count ==
  Working-stream count in the canonical CSVs (log both).
- The 18-column canonical CSVs and the XLSX are **unchanged** except: the XLSX README tab's
  "How to add a station to the DialShift app" section is rewritten to point at the in-app picker
  (manual copy remains as the fallback).

### 5.2 App-side loading rules

- Resolved relative to **`AppContext.BaseDirectory`** (works from `dotnet run`, the publish folder,
  and inside `Contents/Resources/app` of the macOS bundle — the bundle script copies the publish
  output verbatim, `scripts/build-mac-app.sh:100`).
- `DIALSHIFT_CATALOG_PATH=<absolute path>` overrides the location (dev iteration on `data/output/…`
  without rebuilding; tests point it at fixtures). Empty/unset → default location.
- Load: once per process, async, off the UI thread. Missing, unreadable or schema-mismatched file ⇒
  **degraded mode**: the catalog panel shows a muted honest message ("Catalog unavailable — enter
  stream details manually"), a warning goes to `FileAppLog`, and everything else works. **Never crash.**
- Budget: ≤ 5,000 entries in memory (current data is ~1,400); load < 50 ms; a filtered search < 10 ms
  (measured by the perf check, §9).

## 6. Dialog UX spec (non-negotiable behaviors)

Dialog width 680 (MinWidth 620), `SizeToContent="Height"`, content height ≤ ~620 so it fits 1366×768.

**Add mode** (top → bottom):

1. **Search box** (`AutomationProperties.Name="Search stations"`) — focused on open (this *amends*
   BHV-52's "name field focused on open": the intent — focus where typing starts — moves to search;
   the matrix row and the HS-02 check are updated in the same change, justification recorded).
   Placeholder: "Search by name, frequency or city…". Debounced via `LatestValueDispatcher`
   (~200 ms) so typing never blocks.
2. **Filter row** — five ComboBoxes: Country, City, Type, Genre, Language; each starts at "All" and
   its items are the **distinct values from the loaded catalog** (so new data automatically brings
   new filters). Filters AND with the search text. A "Clear" affordance resets everything.
3. **Results dropdown/list** (max height ~230, virtualized, overlay-style under the search box):
   each row shows `Logo-or-monogram · Name · "City · 101.5 FM · Country"` with a muted second line
   `Type · Genre`. Matches from the query engine (§7.2). Footer line: "Showing 50 of 214 matches".
4. **Detail pane** (visible while a result is selected or hovered): name, city/region, frequency,
   type · genre, language, votes, **notes (wrapped, full text — notes are part of the UX, not a
   tooltip)**, and the remote logo (loaded async, failure → monogram = first letter, same rule the
   station icons use).
5. **Manual form** (unchanged fields Name / Description-Or-Genre / Stream URL + hint) below a subtle
   separator — "Or enter stream details manually".
6. Selecting a result **fills Name, Description/Genre and Stream URL** (and sets `Station.Notes` for
   a new station). Fields stay editable so the user can tweak before saving. Cancel/Save/Delete and
   the error line unchanged (BHV-52/53).

**Edit mode**: exactly today's form (no catalog panel) — editing an existing station must not
regress. **Empty/no-match states** are honest ("No stations match — adjust filters or enter the
stream manually"), never dead ends. **Keyboard**: Up/Down walk results, Enter selects, Esc closes
(IsCancel behavior preserved), Tab order search → filters → fields → Save. **QG-03** (quality gate):
DialShift theme + palette, automation names on every input, visible focus, no clipped text at
780×650, dark ink on accent buttons. Headless HS-02 checks stay green (automation names added where
needed, VM property names `Name`/`Tag`/`Url` unchanged so `Field()` mapping keeps working).

## 7. Revised target structure

```text
DialShift.Core/
  Models/Station.cs                    + public string? Notes { get; set; }        (nullable,
                                         [JsonIgnore(Condition=WhenWritingNull)], Version stays 1)
  Catalog/StationCatalogEntry.cs       NEW — plain record/class mirroring §5.1 (no I/O)
  Catalog/StationCatalogQuery.cs       NEW — pure filter/rank engine (§7.2, no I/O)
DialShift.App/
  Services/ICatalogProvider.cs         NEW — GetCatalogAsync() + CatalogAvailable/load state
  Services/CatalogProvider.cs          NEW — file I/O + JSON parse + degraded mode (§5.2)
  ViewModels/StationEditorViewModel.cs catalog section: SearchText, filters, Results, SelectedEntry,
                                         detail data, Fill-From-Catalog; picks up ICatalogProvider via DI
  Views/Dialogs/StationEditorDialog.axaml  search + filters + results + detail pane + manual form (§6)
  DialShift.App.csproj                 + MSBuild item copying ../data/output/app-catalog.json
                                         (Content, CopyToOutputDirectory=PreserveNewest,
                                          CopyToPublishDirectory=PreserveNewest, Condition=Exists;
                                          NOT an AvaloniaResource — a loose file next to the apphost)
data/
  build/build_all.py                   + emit + validate data/output/app-catalog.json (§5.1)
  output/app-catalog.json              NEW — generated, checked in
DialShift.Tests/                       + Catalog suite + UiViewModels CAT checks (§8)
```

- **Core purity** (absolute): the query engine takes entries as input; it never touches files,
  paths, JSON, or clocks. All I/O is in `CatalogProvider` (App layer). No new NuGet packages —
  `System.Text.Json` is already referenced.
- **DI**: `AppComposition` registers `ICatalogProvider` (singleton). Playback, tray, scheduler and
  the other engines are untouched by this feature.

### 7.2 Query engine (pure, Core)

`StationCatalogQuery.Search(IReadOnlyList<StationCatalogEntry>, string text, CatalogFilters filters, int cap=50)`:

- Text match: **case-insensitive AND diacritic-insensitive** substring (`CompareOptions.IgnoreCase |
  IgnoreNonSpace`) over `name`, `name_local`, `city`; **frequency match**: normalized digits —
  typing `1015` or `101.5` matches `frequency_fm == "101.5"`. Empty text = all entries.
- Filters: exact equality on `country`/`city`/`type`/`genre`/`language`, all ANDed; `null` filter =
  "All". (Distinct filter lists come from the same entry set — helper `AvailableValues(field)`.)
- Ranking: prefix match on `name` > substring in `name` > match in `name_local`/`city` > frequency
  match; ties by `votes` desc, then `name`. Cap 50; the caller shows the total count.
- Pure, deterministic, allocation-aware — this is the unit-test surface.

## 8. Tests (DialShift.Tests — deterministic console checks, no framework)

New `Catalog` suite + `UiViewModels` additions (existing 24 suites stay green; `-- --filter Catalog`
runs the new one):

- Parse a valid fixture (all fields, collections, `tag`); malformed/missing/wrong-version →
  degraded mode, no throw.
- Query: diacritic-insensitive case-insensitive substring; frequency digits; each filter alone and
  ANDed; ranking order (prefix > substring > votes); cap 50 with correct total.
- Export contract: fixture with Down/empty-URL rows → excluded; duplicates → deduped.
- Settings: `Station.Notes` roundtrip — null never written, string written, **Version stays 1**,
  an old settings file without `notes` loads untouched.
- View-model: selecting a result fills Name/Tag/Url (+ Notes); filters populate distinct values
  from the catalog; no-catalog state keeps manual add working; BHV-52 validation messages unchanged.

## 9. Risks & gotchas

- **Settings version** is sacred — never bump; the nullable pattern + `SettingsStore` validation
  (name/URL only) already tolerates `Notes`. A `Notes` that is null must serialize away byte-for-byte
  for stations added manually (QA-N3 precedent).
- **Bundle containment**: the macOS `.app` only carries the publish output
  (`scripts/build-mac-app.sh:100`), so the csproj `CopyToPublishDirectory` is what makes the JSON
  reach the bundle; the Windows `build.ps1` likewise consumes publish output. The smoke tests run
  from the bundle — the JSON must resolve via `AppContext.BaseDirectory`, never `cwd`.
- **AvaloniaResource trap**: embedding the JSON as an Avalonia resource would compile it into the
  binary (XAML compilation) — not the goal; we want a **loose, replaceable file**.
- **Compiled bindings**: the dialog uses `x:DataType` — every new binding needs real properties or
  the XAML fails to compile; keep the VM property names `Name`/`Tag`/`Url` stable (HS-02 + `Field()`).
- **No dead code**: the old XLSX-import instructions live in generated docs too (XLSX README tab) —
  update them in the same change, and grep for stale "copy from the workbook" references.
- **Staleness**: radio-browser facts age; refreshing the catalog = `data/.venv/bin/python
  data/build/build_all.py --refresh` + commit of the regenerated JSON. The app displays the
  `generated_utc` so staleness is visible.
- **Logo URLs** are remote and frequently dead (catalog skill pitfalls) — async load with timeout,
  monogram fallback, never block the dialog.
- **Performance**: ~1,400 rows is trivial, but the UI must not filter on every keystroke on the UI
  thread (debounce + off-thread search via the query engine); cap at 50 results.

## 10. Definition of done + acceptance matrix (CAT rows)

**Definition of done:** the Add dialog ships a debounced, keyboard-friendly catalog search with
five dynamic filters and a notes detail pane; the catalog is a generated, checked-in JSON that the
MSBuild target copies into both platform artifacts; manual entry and BHV-52/53 behavior are intact;
no Settings.Version bump; the 24 existing suites plus the new `Catalog` suite pass on both CI
OSes; the four standing quality gates hold (no dead code; docs current in the same change;
QG-03 UI/UX polish; test→fix→retest until green).

| ID | Behavior | Evidence |
|---|---|---|
| CAT-01 | Pipeline emits `data/output/app-catalog.json` per §5.1: Working-only, deduped, `tag` precomputed, collections included; hard validation; count == canonical Working count | `Catalog` suite + pipeline log |
| CAT-02 | `dotnet build` (Debug+Release) and `dotnet publish` (win-x64, osx-arm64) copy the JSON into the output | CI matrix |
| CAT-03 | macOS `.app` and Windows zip contain a parseable `app-catalog.json` next to the apphost | verify scripts + bundle smoke |
| CAT-04 | Runtime load off-thread from `AppContext.BaseDirectory`; missing/corrupt file → degraded mode (muted message + `FileAppLog` warning), no crash | `Catalog` suite + smoke |
| CAT-05 | `DIALSHIFT_CATALOG_PATH` override works (fixtures) | `Catalog` suite |
| CAT-06 | Search matches name/city case- and diacritic-insensitively | `Catalog` suite |
| CAT-07 | Frequency search: `1015` and `101.5` match FM 101.5 | `Catalog` suite |
| CAT-08 | Country/City/Type/Genre/Language filters: dynamic distinct values, ANDed with text | `Catalog` + `UiViewModels` |
| CAT-09 | Ranking (prefix > substring > votes), cap 50 + "showing 50 of N" | `Catalog` suite |
| CAT-10 | Selecting a result fills Name/Tag/URL (+Notes); detail pane shows notes/votes/freq/language | `UiViewModels` + HS checks |
| CAT-11 | Manual entry unchanged — BHV-52/53 green, HS-02 green, edit mode has no catalog panel | HS-02 |
| CAT-12 | Keyboard: focus on search in Add mode, Up/Down/Enter select, Esc cancels | HS checks |
| CAT-13 | Empty/no-match/no-catalog states are honest and never dead-end | `UiViewModels` + design review |
| CAT-14 | QG-03: theme, automation names, no clipped text at 780×650, no typing jank | design review + HS |
| CAT-15 | `Station.Notes` persists only when set; null writes nothing; Settings.Version stays 1; old files load | `Catalog`/settings checks |
| CAT-16 | Perf budget: ≤5,000 entries, load <50 ms, search <10 ms (measured) | perf check |
| CAT-17 | Zero station facts in C#; refresh = re-run pipeline; `generated_utc` shown in UI | review + docs |
| CAT-18 | Docs current in the same change: README, data/README, matrix, decisions D59+, XLSX README tab | docs review |

## 11. Open questions (pre-seeded defaults — record as decisions, do not stop to ask)

| # | Question | Default (adopt) |
|---|---|---|
| 1 | JSON snapshot vs CSV vs embedded resource | JSON snapshot, checked in (reproducible builds, readable, no new parser) |
| 2 | Where the file sits at runtime | Next to the apphost (output root), name `app-catalog.json` |
| 3 | Persist Notes on Station? | Yes — optional nullable field, never written when null, Version stays 1 |
| 4 | Search in Add mode only? | Yes — edit mode keeps the plain form (BHV-53 untouched) |
| 5 | Search control | TextBox + overlay ListBox (explicit controls beat AutoCompleteBox for custom ranking + filters) |
| 6 | Matching semantics | Diacritic-insensitive substring + frequency digits + ranking per §7.2 |
| 7 | Catalog refresh cadence | Manual (`build_all.py --refresh`), documented in data/README |
| 8 | Logo rendering | Async remote load, monogram fallback, timeout, never blocking |
| 9 | CI freshness check | None (presence+parse only); refresh procedure documented |
| 10 | BHV-52 focus rule | Amended: search focused in Add mode; row + HS-02 updated in the same change |

## 12. Execution order (phases, orchestrator-run, roster of 11 agents)

The main agent **only orchestrates** (never edits code/docs): delegates via the Task tool to the
`.claude/agents/` roster, reviews diffs + real build/test output, rejects and re-delegates until
green. Roster: the 7 existing agents where relevant (`spec-architect`, `core-engineer`,
`ui-engineer`, `release-engineer`, `test-engineer`; `playback-engineer` and `platform-engineer` are
**off-mission**) + 6 new agent files created in Phase 0: `data-engineer`, `app-integration-engineer`,
`docs-engineer`, `qa-auditor`, `design-reviewer`, `perf-auditor` (frontmatter style of the existing
files: `name`, one-sentence `description`, `tools`, body = role + deliverables + file ownership;
**no `model:` key** — inherit the session model).

1. **Phase 0 — spec (spec-architect only):** write the 6 new agent files; CAT-01..18 rows into
   `docs/acceptance-matrix.md`; freeze the contracts (`StationCatalogEntry`, `StationCatalogQuery`,
   `ICatalogProvider`). Gate: existing 24 suites still green (baseline).
2. **Parallel lanes (non-overlapping file ownership):**
   - **Phase 1 — data (data-engineer):** §5 pipeline export + validation + XLSX README-tab rewrite +
     `data/README` refresh. Gate: pipeline run green, JSON validates, counts match.
   - **Phase 2 — core (core-engineer):** `StationCatalogEntry`, `StationCatalogQuery`, `Station.Notes`
     (version-safe). Gate: Core builds `-warnaserror`, new `Catalog` suite green (with test-engineer).
   - **Phase 3 — integration (app-integration-engineer):** csproj copy + `CatalogProvider` +
     DI + verify publish/bundle/zip contain the JSON. Gate: publish on both RIDs + zip/.app checks.
   - **Phase 4 — UI (ui-engineer):** §6 dialog rebuild. Gate: build green, HS-02 + new HS checks green.
3. **Phase 5 — verification wave (test-engineer + qa-auditor + design-reviewer + perf-auditor,**
   all fresh-context on the diff only): full suite on both CI OSes, CAT-01..18 all GREEN, QG-03
   audit, perf budgets measured, dead-code grep, Settings.Version audit, correctness-only findings
   (Critical blocks, ordered, each with the concrete fix). Fix loop: max 3 rounds, then escalate to
   the user.
4. **Phase 6 — docs + release (docs-engineer + release-engineer):** README, matrix status banner,
   decisions D59+, release notes NOT yet (release-time); CI green with the JSON in both artifacts;
   final report (per-agent summaries, real build/test/publish excerpts, matrix status, remaining
   native checks — Windows smoke/clean-machine can't run on this macOS box — decisions, `git log --stat`).

Global guardrails for every agent: branch `feature/add-station-catalog-search` off `main`
(created if missing), commit freely, push ONLY the `private`
remote (`git@github.com:spyroskotsakis/dialshift-dev.git`); never `origin`/`upstream`
(tsiger/DialShift, read-only); no PRs; never bump `Settings.Version`; no station facts in C#; no
dead code; docs in the same change as code; verify with real command output, never invented results.

## 13. Decision

**Proceed** with this design once the user approves the brief. Execution follows §12 with the
orchestrator-only main agent and the goal prompt in `docs/add-station-catalog-search-goal.md`.

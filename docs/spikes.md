# §7.3 spikes: results (brief 1, step 3 of §12)

Run 2026-09-25 on the dev box: Apple Silicon (arm64), macOS 26.5.2 (25F84), .NET SDK 10.0.401, runtime 10.0.12.
Spike code is throwaway and lives outside the repo (session scratchpad):
`/private/tmp/claude-501/-Users-spyroskotsakis-Desktop-RadioTsigerProject-DialShift/3302bd9d-9497-4c9f-9dc7-72444abeb9a7/scratchpad/spikes/`
(`avplayer/`, `systemevents/`, `nswake/`, `clocks/`, `corpus/server.py`, raw logs `out-*.txt`). Every number below is from real output of those runs.

## Summary of verdicts

| Spike | Checkpoint / criterion | Verdict |
|---|---|---|
| AVPlayer | A: feasibility (osx-arm64, MP3/AAC/HLS play, stop, volume) | **PASS** (5 of 5 checks, in 5 separate runs, including the published arm64 binary inside a signed `.app`) |
| AVPlayer | B: reliability, the parts a spike can run | **PASS**: 20 and 120 rapid source changes, stop-while-connecting ×4, start/dispose ×10, clean and dirty exit, error normalization |
| AVPlayer | B: wake/reconnect, second-instance activation, clean-machine `.app` install | **DEFERRED** to the integrated app (not faked) |
| SystemEvents | Compile level: resolves in `net10.0` with no `-windows` TFM, CA1416 clean | **PASS** (runtime behaviour: Windows native/CI pending) |
| NSWorkspace wake | (1) builds without making the app macOS-only | **PASS** |
| NSWorkspace wake | (2) fires after lid-close / normal sleep-wake | **PARTIAL**: the callback path is proven, and system notification delivery is proven on the main run loop. Real sleep/wake is a native manual check (NC-08). |
| NSWorkspace wake | (3) no leaked observer handles after dispose | **PASS** |
| NSWorkspace wake | (4) isolatable behind `MacPowerEvents : ISystemPowerEvents` | **PASS** |
| NSWorkspace wake | (5) registration failure leaves timer-gap working | **PASS**, with one caveat: the gap must be measured on a sleep-inclusive clock (see plan change 1) |
| NSWorkspace wake | Overall | **ADOPT**, conditional on NC-08 lid-close confirmation. The timer-gap fallback stays. |

### Findings that change the plan

1. **The timer-gap wake fallback cannot use `Stopwatch` on macOS (this affects §4.1a, D3 and `StopwatchMonotonicClock`).** On macOS, `Stopwatch.GetTimestamp()` and `Environment.TickCount64` both read `CLOCK_UPTIME_RAW`, and that clock **stops while the Mac sleeps**. Measured on this box (booted Sep 15, many sleeps since):
   ```
   Stopwatch seconds             =     259905.063
   Environment.TickCount64 (s)   =     259905.063
   CLOCK_UPTIME_RAW (excl sleep) =     259905.063
   CLOCK_MONOTONIC_RAW (incl)    =     808008.759
   => total sleep since boot ~ 152.25 h; Stopwatch-UPTIME diff -0.000s; Stopwatch-MONOTONIC_RAW diff -548103.695s
   ```
   A tick before sleep and a tick after wake are about 1 s apart on the `Stopwatch` clock, so a 15 s gap is **never** detected. `DialShift.Core/Playback/Contracts/IMonotonicClock.cs` currently ships `StopwatchMonotonicClock` as production. That would silently break the macOS fallback, which is exactly the path §4.4 relies on when native wake is unavailable. The current Mac app detects wake only because it uses `DateTime.UtcNow`.
   **Required change:** the wake-gap check needs a clock that is monotonic **and** keeps counting through sleep.
   - macOS: `clock_gettime_nsec_np(CLOCK_MONOTONIC_RAW)` (clock id 4). This is the same as `mach_continuous_time`. It is sleep-inclusive and unaffected by wall-clock or NTP changes.
   - Windows: whether `Stopwatch`/QPC counts through sleep is **unverified**. `GetTickCount64`/`Environment.TickCount64` is interrupt-time based and documented as sleep-biased (unlike `QueryUnbiasedInterruptTime`). Add a native Windows check.

   Keep the P/Invoke in `DialShift.App` platform code and inject it through `IMonotonicClock` (or a dedicated `IWakeGapClock`). Retry backoff and the stall watchdog may keep using `Stopwatch`. CT-PB-30 stays valid because it runs on a fake clock.
2. **The macOS `.app` Info.plist MUST contain `NSAppTransportSecurity` / `NSAllowsArbitraryLoadsForMedia = true`.** Inside a bundle without it, every cleartext `http://` stream fails at once. `dotnet run` binaries have no bundle and do not enforce ATS, which hides the problem. 3,570 of the 8,307 `stream_url` values in `data/canonical/*.csv` (43 %) are `http://`. Upstream's `package-macos.py` sets this key. **Resolved (D32):** `scripts/build-mac-app.sh` now writes `NSAppTransportSecurity` → `NSAllowsArbitraryLoadsForMedia` = true (media only, never `NSAllowsArbitraryLoads`), and `scripts/verify-mac-app.sh` asserts both in CI on the downloadable zip. Measured with the same published arm64 binary in two signed bundles:
   ```
   === ats-default
   INFO U1  http://ice1.somafm.com/groovesalad-128-mp3  FAILS after 0.10s -> TlsFailure; AVFoundationErrorDomain -11800 "The operation could not be completed" <- NSOSStatusErrorDomain -1022
   INFO U2  http://st01.dlf.de/dlf/01/128/mp3/stream.mp3  FAILS after 0.10s -> TlsFailure; NSURLErrorDomain -1022 "The resource could not be loaded because the App Transport Security policy requires the use of a secure connection."
   PASS U5  https://ice1.somafm.com/groovesalad-128-mp3  PLAYS
   === ats-media
   PASS U1  http://ice1.somafm.com/groovesalad-128-mp3  PLAYS (Playing after 1.11s, +2.1s)
   PASS U2  http://st01.dlf.de/dlf/01/128/mp3/stream.mp3  PLAYS (Playing after 1.15s, +2.0s)
   ```
   Loopback (`127.0.0.1`) is exempt from ATS. The packaging assertion is in place (above). A native check that plays one `http://` stream from the **bundled** app is still open: NC-07 and NC-17 in the acceptance matrix.
3. **AVPlayer and NSWorkspace both need the process main run loop to be serviced.** With the main thread blocked, AVPlayer never leaves `Unknown`: no playback and **no errors** (connection refused goes unreported for 15 s and more). System NSWorkspace notifications are never delivered either. The Avalonia app satisfies this, because the NSApplication loop runs on the main thread. Implications:
   - A frozen UI thread freezes AVPlayer state reporting.
   - Adapter integration tests cannot run as ordinary xunit tests on pool threads. They need a harness that pumps the main CFRunLoop (as the spike does), or they must run inside the app's native smoke.
4. **URL path extension beats Content-Type in AVFoundation.** An MP3 stream served at a path ending in `.pls` or `.m3u` (`Content-Type: audio/mpeg`) fails with `CoreMediaErrorDomain -12646` after 3–11 s. AVFoundation parses it as a playlist. `AVURLAssetOverrideMIMETypeKey = audio/mpeg` does **not** help (tested). The same server at `/;` plays. This affects some of the about 27 catalog URLs with `.pls`/`.m3u` paths (real PLS/M3U playlist files play fine). The fix belongs in the catalog pipeline: probe the Content-Type and store a playable URL or a per-engine flag. It does not belong in the adapter.
5. **More formats than the brief assumed.** On macOS 26.5, AVPlayer also played Ogg Vorbis, Opus (after a 302), FLAC-in-Ogg, `.aacp`, Shoutcast v1 `ICY 200 OK` and v2 `/;` streams, and real PLS/M3U playlist files. The brief (§4.3) says "MP3/AAC/HLS covered". This is **only verified on macOS 26.5**. Before advertising it, re-run the corpus on the minimum supported macOS. **D22** set `LSMinimumSystemVersion` to 14.0 (as upstream), and the macOS 14 corpus run is native check NC-16.

---

## Spike 1: Apple Silicon AVPlayer

**Goal (§4.3, §7.3, §7.8, §12 step 3).** Adapt upstream v0.2.0 `MacAudioSession.cs` into an arm64-ABI-correct AVPlayer core and verify it on an M-series Mac, in two checkpoints. "Audio came out once" is not a pass.

**What was built.** `spikes/avplayer` is a net10.0 console app, `RuntimeIdentifier=osx-arm64`, with no third-party packages. It has three parts:
- `shared/ObjC.cs`: `[LibraryImport]` source-generated stubs, one `objc_msgSend` entry point per native prototype (never varargs).
- `AvEngine.cs`: a prototype of the `MacAvPlayerPlaybackEngine` native core.
- `Executor.cs`: one serial native thread with an autorelease pool per batch, in three modes:
  - `main`: calls run on the process main thread, which also pumps CFRunLoop.
  - `worker`: calls run on a dedicated thread while the main thread pumps CFRunLoop.
  - `nopump`: calls run on a dedicated thread and the main run loop never runs.

Nothing uses a UI run loop. State is observed by polling at 100–250 ms. In addition, `AVPlayerItem` notifications go to a runtime-created ObjC observer class (no blocks).

Evidence beyond AVPlayer's own properties:
- **CoreAudio per-process output state** (`kAudioHardwarePropertyTranslatePIDToProcessObject` + `kAudioProcessPropertyIsRunningOutput`, macOS 14+) proves this process was really rendering to the output device.
- **`heap <pid>`** counts live `AVPlayer`/`AVPlayerItem`/`AVURLAsset`/observer instances.
- **`leaks <pid>`** at exit.

A local server (`corpus/server.py`) provides deterministic transport cases: redirect, redirect loop, 401 Basic, captive HTML, hang, EOS, stall, ICY v1, garbage bytes, 403/500, and MP3 under `.pls`/`.m3u` names.

**Published binary (for `file` verification):**
`/private/tmp/claude-501/-Users-spyroskotsakis-Desktop-RadioTsigerProject-DialShift/3302bd9d-9497-4c9f-9dc7-72444abeb9a7/scratchpad/spikes/publish/avplayer-osx-arm64/avplayer-spike`
```
avplayer-spike: Mach-O 64-bit executable arm64      (lipo -archs: arm64; all 13 bundled .dylib: arm64; 0 files matching *vlc*)
```
The same binary is inside the signed bundles `spikes/bundles/ats-media/AvSpike.app` and `spikes/bundles/ats-default/AvSpike.app`.

### Checkpoint A (feasibility): PASS

Command: `dotnet run -c Release -r osx-arm64 -- --mode worker --suite all`, from `out-all-worker.txt`.
```
# AVPlayer spike  arch=Arm64 os=macOS 26.5.2 runtime=.NET 10.0.12
PASS A1  MP3 progressive (HTTPS Icecast)  Playing after 1.62s; currentTime 0.00->3.21 (+3.21s over 3.77s wall); rawCMTime={value=3208900368, timescale=1000000000, flags=0x1, epoch=0}; nonPlayingPolls=0; CoreAudio process output running=True
PASS A2  AAC progressive (HTTPS Icecast)  Playing after 2.02s; currentTime 0.00->3.21 (+3.21s over 3.52s wall); ... CoreAudio process output running=True
PASS A3  HLS audio (BBC World Service)    Playing after 1.62s; currentTime 12.80->15.82 (+3.01s over 3.01s wall); ... CoreAudio process output running=True
PASS A4  stop: pause -> Paused, clock frozen; item dropped  before=Playing; after pause tcs=Paused itemTime 15.820->15.820 over 2s; after stop tcs=Paused currentItem=nil t=NaN; CoreAudio process output: IO stopped 2.1s after stop
PASS A5  setVolume:(float) / volume readback  0.37->0.37/0.37, 0.00->0.00/0.00, 1.00->1.00/1.00, 0.15->0.15/0.15
```
- Test URLs: MP3 `https://ice1.somafm.com/groovesalad-128-mp3`; AAC `https://ice5.somafm.com/groovesalad-128-aac`; HLS: the BBC World Service URL suggested in the brief (`.../bbc_world_service.m3u8`) worked. Two more public HLS audio streams also worked (Radio France, radios.bzh; see the corpus).
- The same 5 checks passed in 3 more worker-mode runs, and in `--mode main` from the **published binary inside `AvSpike.app`** (`out-a-main-bundle.txt`, process exit 0).
- Time to Playing: 0.5–2.7 s across runs.
- The CMTime struct return is correct on arm64. A plain `objc_msgSend` returns `{value, timescale=1e9, flags=valid}` values that agree with `CMTimeGetSeconds`. A wrong ABI would produce garbage.
- **Harness defect found and fixed; it is also an adapter rule.** Immediately after `replaceCurrentItemWithPlayerItem:`, `timeControlStatus` still reads `Playing` (the player rate is still 1) while the new item is `Unknown`. The first run falsely reported "Playing after 0.00s". **"Playing" must require `currentItem == ourItem && item.status == ReadyToPlay && timeControlStatus == Playing`**, and preferably also `currentTime` advancing.
- **Volume anomaly: observed once, not reproduced.** In one early run, `setVolume:0.15` read back 1.00 immediately after the sequence 0.37 → 0 → 1.0. It did not recur in about 90 further set/readback checks: 60 in the exact A5 shape, 22 with delays, and 4 runs of A5 with an immediate and a 500 ms readback. Mitigation: the adapter keeps the desired volume, re-applies `setVolume:` after every `replaceCurrentItemWithPlayerItem:` and on each transition to Playing, and logs drift found at poll time.

### Checkpoint B (reliability): PASS for everything a spike can run; the rest is deferred

All of B ran in one process, in worker mode, and was then repeated (`out-b-worker.txt`, `out-all-worker.txt`).

| # | Case | Result |
|---|---|---|
| B1 | 20 rapid source changes on **one AVPlayer** via `replaceCurrentItemWithPlayerItem:` (MP3/AAC/HLS/http-MP3 cycled, 0–300 ms apart) | PASS. Loop 2.4 s, no crash; the final source Playing after 1.5 s and advancing. `heap` while playing: `AVPlayer=1 AVPlayerItem=1 AVURLAsset=1`; after stop: `AVPlayerItem=0 AVURLAsset=0` |
| B1 memory | GC + `WorkingSet64`, before and after | Run 1: 72.4 → 89.9 MB (+17.5 MB). Run 2: 62.8 → 76.8 MB (+14.0 MB). The first 20 changes include first-time HLS/AAC decoder and framework warm-up. Managed heap 200 → 369 KB |
| B1b | +100 more rapid changes (trend) | Flat: +0.8 MB (run 1), −4.4 MB (run 2). AV object counts back to 0 items/assets after stop. **No per-change leak.** |
| B2 | stop-while-connecting: MP3 at 0 ms and 50 ms, HLS at 150 ms, hanging server at 500 ms | PASS ×4. 5 s after stop: `tcs=Paused currentItem=nil revived=False`, and CoreAudio output IO not running |
| B3 | start → dispose → start ×10 (**new AVPlayer each time**) | PASS 10/10 Playing; time to playing 1.82–3.54 s. After all were disposed: `AVPlayer=0 AVPlayerItem=0 AVURLAsset=0 DialShiftAVPlayerObserver=0` |
| B4 | connection refused `http://127.0.0.1:1/unavailable` | Failed after **0.10 s**: `NSURLErrorDomain -1004` → NetworkUnavailable |
| B4b | DNS failure `stream.nonexistent.invalid` | Failed after 0.10–0.40 s: `NSURLErrorDomain -1003` → NetworkUnavailable |
| B5 | malformed strings (`not a url`, `http://`, `ht tp://…`, `ftp://…`, `file:///…`, `""`) | Rejected synchronously by managed `Uri` validation (≤1 ms) → InvalidUrl. Passed raw to NSURL: `initWithString:` returns nil for strings with spaces; `not a url` and `foo://` give `NSURLErrorDomain -1002` in 0.1 s; `http://` gives `-1004`. **Validate in managed code first.** |
| B6 | HTTP 404 `https://ice5.somafm.com/does-not-exist-xyz` | Failed after 0.9–1.3 s: `NSURLErrorDomain -1100 <- CoreMediaErrorDomain -12938 (HTTP 404)` → HttpError |
| B6b–d | 403 / 500 / HLS playlist 404 | 0.10 s `-1102 <- OSStatus -12660`; 0.10 s `-1008 <- CoreMedia -16847 (HTTP 500)`; 0.3–0.4 s `-1100 <- -12938` → HttpError |
| B7a–d | TLS: expired / self-signed / wrong host / untrusted root (badssl.com) | Failed after 0.7–4.9 s: all `NSURLErrorDomain -1202` → TlsFailure |
| B8 | redirects: local http→https 302; DLF https→https 302 with token; DLF http→http 302 | All PLAY (1.0–2.1 s). `http://ice5.somafm.com/...` does not redirect (serves 200), so a local 302 covers http→https |
| B8d | redirect loop | 0.10 s `-1007 too many HTTP redirects` → HttpError |
| B9 | captive-portal style, local `200 text/html` over HTTP/1.0 | **No failure within 30 s** (`tcs=Waiting item=Unknown`). The coordinator watchdog must turn this into Stalled |
| B9b | captive-portal style, `https://example.com/` (HTML) | 0.3–0.7 s `AVFoundationErrorDomain -11828 Cannot Open <- OSStatus -12847` → UnsupportedFormat |
| B9c | random bytes served as `audio/mpeg` | Failed after **16 s**: `CoreMediaErrorDomain -16830` → Unknown (retryable) |
| B10 | 401 Basic, no credentials | 0.10 s `-1013 <- CoreMedia -16840 (HTTP 401)` → HttpError |
| B10b | Basic credentials in URL userinfo (`user:pass@host`) | PLAYS. AVFoundation answers the challenge from the URL credentials |
| B11 | Shoutcast v1 `ICY 200 OK` status line | PLAYS |
| B12 | cleartext `http://` (console binary, no bundle) | PLAYS. **In a `.app` it needs the ATS key** (plan change 2) |
| B13 | server closes the stream (EOS) | `AVPlayerItemDidPlayToEndTimeNotification` **on the main thread**; `tcs=Paused`, item still `ReadyToPlay`, no error → EndOfStream |
| B14 | server stops sending and keeps the socket open | `AVPlayerItemPlaybackStalledNotification` on the main thread; `tcs=Waiting`, reason `AVPlayerWaitingToMinimizeStallsReason`; no error ever → Buffering / Stalled |
| B15 | server accepts but never answers | No failure within 45 s (`reason=WithNoItemToPlay`) → the coordinator's connect/stall watchdog must fire |
| Clean exit | all engines disposed, then `heap` and `leaks` | `AVPlayer=0 AVPlayerItem=0 AVURLAsset=0 DialShiftAVPlayerObserver=0`. `leaks`: **0 leaks** (B-only run). After the full corpus: 2 × 80-byte `CFString` root leaks whose allocation stack is entirely Apple-internal (`CFNetwork task:didCompleteWithError:` → MediaToolbox → `CFURLCreateWithBytes`), with no DialShift frames. Process exit 0 |
| Dirty exit | exit while Playing, engine not disposed, observers still registered | Exit status 0 in 3/3 runs, worker and main modes. No crash reports |

The two harness "FAIL" labels in the raw log are B9 and B9c. They record that my expected kind was wrong, not an engine defect. AVPlayer surfaces no fast error for these cases, and the coordinator watchdog covers them (see the normalization table).

**Observer class name for leak checks.** The spike's runtime classes were `DialShiftAVPlayerObserver` (this spike) and `DialShiftWakeObserver` (spike 3). Production uses **one** process-global class, **`DialShiftNotificationObserver`** (`DialShift.App/Interop/NotificationObserver.cs`, D27). It is shared by `MacAvPlayerPlaybackEngine` and `MacPowerEvents`. Future `heap <pid>` leak checks must count `DialShiftNotificationObserver`. Expect one instance per live engine plus one per started `MacPowerEvents`, and 0 after quit.

**Replace the item or create a new player?** Both were exercised: B1/B1b used 120 replaces on one player, and B3 used 10 new players. **Recommendation:** one `AVPlayer` per engine instance, plus a new `AVPlayerItem` per `StartAsync` via `replaceCurrentItemWithPlayerItem:`.
- Observers stay registered exactly once for the engine's lifetime.
- Volume persists, and fewer native objects are churned.
- Memory was flat over 120 changes.

The cost is the stale-`Playing` rule above, which item identity plus status handles. A new player per session (upstream) would also work, but it moves observer registration and removal into the per-session path.

**Threading (measured).** The same Checkpoint A threads subset ran in three modes (`out-threads-*.txt`):
```
worker  (main run loop pumped): PASS A1/A4; conn-refused Failed after 0.10s; EOS note DidPlayToEndTime@main
main    (calls on main thread): PASS A1/A4; conn-refused Failed after 0.11s; EOS note DidPlayToEndTime@main
nopump  (main run loop NOT running):
  FAIL A1 MP3 ... not Playing after 20.1s: tcs=Waiting item=Unknown player=Unknown t=0.00s ... wait=WhileEvaluatingBufferingRate
  INFO T2 connection refused ... no failure surfaced within 15s
```
AVPlayer needs the main dispatch queue and run loop. Its notifications arrive on the main thread. In the Avalonia app, the NSApplication loop on the main thread provides this.

### Deferred to the integrated app (not faked)

- Wake/reconnect through `ResumeFromSleepAsync`. The spike showed that a new item after a stall or EOS plays again (B1, B3). Real sleep is NC-08.
- Second-instance activation.
- Clean-machine `.app` install with no Rosetta and no dev tools. A signed bundle ran on this dev box, but that is not a clean machine.
- Repeated sleep/wake cycles, and exit while a reconnect is in flight.
- The corpus on the minimum supported macOS version.

### Production guidance: `MacAvPlayerPlaybackEngine`

**Loading.** `NativeLibrary.Load("/System/Library/Frameworks/AVFoundation.framework/AVFoundation")` once, kept for the process lifetime. Load Foundation and CoreMedia the same way for their constants and functions. Guard with `OperatingSystem.IsMacOS()` at composition time and mark the types `[SupportedOSPlatform("macos")]`. Use `[LibraryImport]` (source-generated, AOT/trim friendly) with `internal` visibility (CA1401).

**Selectors and signatures (all verified on arm64 in the spike).** Each row is its own `objc_msgSend` entry point typed to the exact prototype.

| Receiver | Selector | C prototype after `(id self, SEL _cmd, …)` | Ownership |
|---|---|---|---|
| any class | `alloc` | `id` | +1, paired with `init…` |
| `NSString` | `initWithUTF8String:` | `id (const char*)` | +1, release after use |
| `NSString` | `UTF8String` | `const char*` | inner pointer: copy right away, inside the pool |
| `NSURL` | `initWithString:` | `id (NSString*)`, **nil** on failure | +1; do not release on nil (`init` already freed it) |
| `AVURLAsset` | `initWithURL:options:` | `id (NSURL*, NSDictionary*)` (options nil) | +1; release after the item is created |
| `AVPlayerItem` | `initWithAsset:` | `id (AVAsset*)` | +1; engine keeps it until replaced or stopped |
| `AVPlayer` | `init` | `id` | +1; released in `DisposeAsync` |
| `AVPlayer` | `replaceCurrentItemWithPlayerItem:` | `void (AVPlayerItem*)` (nil clears) | player retains the new item and releases the old |
| `AVPlayer` | `play`, `pause` | `void` | |
| `AVPlayer` | `setVolume:` | `void (float)` (**float in s0**, not double) | |
| `AVPlayer` | `volume` | `float` | |
| `AVPlayer` | `timeControlStatus` | `NSInteger` (0 Paused, 1 WaitingToPlayAtSpecifiedRate, 2 Playing) | |
| `AVPlayer` | `status` | `NSInteger` (0 Unknown, 1 ReadyToPlay, 2 Failed) | |
| `AVPlayer` | `currentItem`, `error`, `reasonForWaitingToPlay` | `id` | +0, never release |
| `AVPlayer` / `AVPlayerItem` | `currentTime` | `CMTime` (24-byte struct: arm64 indirect return in **x8** with plain `objc_msgSend`; x86_64 needs `objc_msgSend_stret`) | value |
| `AVPlayerItem` | `status` | `NSInteger` | |
| `AVPlayerItem` | `error` | `NSError*` | +0 |
| `AVPlayerItem` | `isPlaybackLikelyToKeepUp`, `isPlaybackBufferEmpty` | `BOOL` (1-byte C `bool` on arm64) | |
| `AVPlayerItem` | `errorLog` → `events` → `lastObject` → `errorStatusCode` / `errorDomain` / `errorComment` | `id` / `NSInteger` / `NSString*` | +0 |
| `NSError` | `domain`, `code`, `localizedDescription`, `userInfo` → `objectForKey:` (`NSUnderlyingErrorKey`) | `id` / `NSInteger` | +0 |
| `NSNotificationCenter` | `defaultCenter` | `id` | singleton |
| `NSNotificationCenter` | `addObserver:selector:name:object:` | `void (id, SEL, NSString*, id)` | observer is not retained; remove before release |
| `NSNotificationCenter` | `removeObserver:` | `void (id)` | |
| `NSNotification` | `name`, `object`, `userInfo` | `id` | +0 |
| CoreMedia | `CMTimeGetSeconds(CMTime)` | `double` (struct by value) | |
| libobjc | `objc_getClass`, `objc_lookUpClass`, `sel_registerName`, `objc_autoreleasePoolPush/Pop`, `objc_allocateClassPair`, `class_addMethod` (`v@:@`), `objc_registerClassPair` | | class registered **once per process**; `objc_lookUpClass` first |

Constants are read with `NativeLibrary.GetExport` + `Marshal.ReadIntPtr`:
- From AVFoundation: `AVPlayerItemDidPlayToEndTimeNotification`, `AVPlayerItemFailedToPlayToEndTimeNotification` (error in `AVPlayerItemFailedToPlayToEndTimeErrorKey`), `AVPlayerItemPlaybackStalledNotification`, `AVPlayerItemNewErrorLogEntryNotification`.
- From Foundation: `NSUnderlyingErrorKey`.

**Ownership rules.**
- Every `alloc/init…` result is released exactly once.
- Getter results are +0: never released, and only read inside an autorelease pool (`objc_autoreleasePoolPush/Pop` around every native batch).
- The engine holds exactly two long-lived references: the player (+1) and the current item (+1).
- `StartAsync` builds `NSString → NSURL → AVURLAsset → AVPlayerItem` and releases each intermediate as soon as the next object retains it. It then calls `replaceCurrentItemWithPlayerItem:` and releases the previous item.
- `StopAsync` = `pause` + `replaceCurrentItemWithPlayerItem:nil` + release the item. Dropping the item closes the network connection so live radio never resumes from a stale buffer.
- `DisposeAsync` = stop + `removeObserver:` + release the observer + release the player. `heap` showed zero AV objects after this path.

**Threading.**
- Marshal every native call to the **UI (main) thread**. The adapter takes a small injected main-thread invoker, so it does not reference Avalonia types.
- Polling (250–500 ms) also runs there; each tick is a few getter calls inside one pool.
- Never block the main thread while waiting for AVPlayer.
- Notifications arrive on the main thread. The observer callback (`[UnmanagedCallersOnly]`, try/catch, never throws into ObjC) only enqueues. The adapter raises `StateChanged`/`Failed` after the native call returns, never inside `StartAsync`/`StopAsync` stacks, and never while holding a lock.

**State mapping (to `PlaybackEngineState`).**
- `StartAsync` → `Opening`.
- `Playing` when `currentItem == ours && item.status == ReadyToPlay && timeControlStatus == 2` (+ currentTime advancing).
- `Buffering` when `timeControlStatus == 1`, or on `PlaybackStalled`.
- `Ended` + `Failed(EndOfStream)` on `DidPlayToEndTime`, or on `timeControlStatus == 0` without a requested pause while the item is `ReadyToPlay`.
- `Stopped` after `StopAsync`.
- Session id and generation are checked on every event: item identity is only compared while the engine still holds its +1 on the item.

**Error normalization (observed domains and codes → `PlaybackFailureKind`).** Walk the `NSError` chain (outer → `NSUnderlyingErrorKey`) together with `errorLog.lastObject.errorStatusCode`; the first match wins. Check ATS first.

| Observed (this spike) | Kind |
|---|---|
| `-1022` in `NSURLErrorDomain`, or `NSOSStatusErrorDomain` under `AVFoundationErrorDomain -11800` (ATS blocked cleartext) | TlsFailure. Log diagnostic "ATS: missing NSAllowsArbitraryLoadsForMedia" (a packaging defect) |
| `NSURLErrorDomain -1202` (expired, self-signed, wrong host, untrusted root); -1200…-1206 | TlsFailure |
| `NSURLErrorDomain -1004` (refused), `-1003` (DNS); also -1001, -1005, -1006, -1009, -1018, -1020 | NetworkUnavailable |
| `-1100 <- CoreMedia -12938` (404); `-1102 <- OSStatus -12660` (403); `-1008 <- CoreMedia -16847` (500); `-1013 <- CoreMedia -16840` (401); `-1007` (redirect loop); also -1010, -1011, -1017; `errorLog.errorStatusCode >= 400` | HttpError. The HTTP status can be logged from the CoreMedia text; no credentials or query strings |
| managed `Uri` validation failure (non-absolute, non-http(s)); `NSURL initWithString:` nil; `NSURLErrorDomain -1002`/`-1000` | InvalidUrl |
| `AVFoundationErrorDomain -11828 <- OSStatus -12847` (HTML over https); `CoreMediaErrorDomain -12646` (playlist-by-extension); AVFoundation -11829/-11821/-11833/-11838; NSURL -1015/-1016 | UnsupportedFormat |
| `AVPlayerItemPlaybackStalledNotification`, `timeControlStatus == Waiting` after having played | not a failure: report `Buffering`. The coordinator's 25 s watchdog decides |
| never `ReadyToPlay` (hanging server B15, HTML over HTTP/1.0 B9) | nothing native. The coordinator's connect/stall watchdog produces the failure (the contract already assigns it to the coordinator) |
| `AVPlayerItemDidPlayToEndTimeNotification`, or an unrequested pause with the item ready | EndOfStream |
| `FailedToPlayToEndTime` | classify the error in its userInfo |
| `CoreMediaErrorDomain -16830` (undecodable bytes, 16 s) and anything else | Unknown |

Measured time-to-failure: refused/DNS/401/403/500/redirect-loop/ATS 0.1–0.4 s; 404 0.3–1.3 s; TLS 0.7–4.9 s; playlist-extension 3–11 s; garbage 16 s; hang and HTTP/1.0 HTML never. The coordinator's watchdog is therefore mandatory on macOS.

**Capability differences to document (README / release notes).**
- **No track-title metadata on macOS (D26).** The spike did not read ICY metadata. The production adapter harness then tried `AVPlayerItemMetadataOutput`. It delivered `icy`/`StreamTitle` reliably only for a **Shoutcast v2** server, and **never** for Icecast MP3/AAC or HLS streams. `MacAvPlayerPlaybackEngine` therefore does not implement `ITrackMetadataProvider`, and macOS always shows the station tag. On Windows, `LibVlcPlaybackEngine` offers `Meta(NowPlaying)` titles only for **`http://`** streams: LibVLC 3's `https://` access module does not send `Icy-MetaData`, so https stations show the tag there too.
- The format list above (OS-version dependent).
- `.pls`/`.m3u`-named raw streams fail.
- `http://` needs the ATS media exception.
- Basic auth works through URL userinfo; custom headers or tokens only through the URL.
- Redirects (301/302, http↔https) are followed.
- TLS errors are strict: no override.
- HLS works. It starts at the live edge, so `currentTime` begins at about 12.8 s.
- Proxy: the system proxy settings apply (not tested).

---

## Spike 2: Windows `SystemEvents` (compile level)

**Goal / exit criteria (§4.4.1, §7.3).** The package resolves in `net10.0` **without** a `-windows` TFM. The code subscribes after the message loop is up, unsubscribes deterministically, and keeps the timer-gap fallback.

**What was done.** `spikes/systemevents`:
- `PowerEvents/`: a plain `net10.0` class library, with no RID and no `-windows` TFM, referencing `Microsoft.Win32.SystemEvents` **10.0.12** (latest 10.x on nuget.org). `WarningsAsErrors=CA1416` and `AnalysisLevel=latest-recommended` are set.
- `WindowsPowerEvents : ISystemPowerEvents`:
  - `TryStart()` returns false on non-Windows. The `if (!OperatingSystem.IsWindows()) return false;` guard is what satisfies CA1416 for the subscription.
  - `+= SystemEvents.PowerModeChanged` maps `PowerModes.Resume`/`Suspend` and ignores `StatusChange`. The handler carries `[SupportedOSPlatform("windows")]`.
  - `Dispose()` unsubscribes, is idempotent, and is also guarded.
  - Registration exceptions return false.
- `Host/`: a `net10.0` console app referencing it.
- `NegativeControl/`: the same call without a guard, to prove the analyzer is live.

**Output.**
```
== restore/build (no RID)          0 Warning(s)  0 Error(s)
== build -r win-x64                0 Warning(s)  0 Error(s)
== publish -r win-x64              Host -> .../spikes/publish/systemevents-win-x64/
== publish -r osx-arm64            Host -> .../spikes/publish/systemevents-osx-arm64/
NegativeControl: error CA1416: This call site is reachable on all platforms. 'SystemEvents.PowerModeChanged' is only supported on: 'windows'.
```
Asset selection (sha1 prefix, size):
```
94cddc92876e 80680 pkg:runtimes/win/lib/net10.0/Microsoft.Win32.SystemEvents.dll
e9a89fe267a0 27432 pkg:lib/net10.0/Microsoft.Win32.SystemEvents.dll            (non-Windows stub)
94cddc92876e 80680 systemevents-win-x64    <- Windows implementation selected
e9a89fe267a0 27432 systemevents-osx-arm64  <- stub selected
```
- The no-RID (portable) build carries `runtimes/win/lib/net10.0/...` through `deps.json` `runtimeTargets` (`rid: win`), so a framework-dependent portable app also gets the real implementation on Windows.
- On this Mac, the osx-arm64 host prints `WindowsPowerEvents.TryStart()=False IsActive=False -> timer-gap fallback only`, with no exception.

**Verdict:** compile level **PASS**. 0 warnings; CA1416 is enforced as an error and satisfied by the guard.

**Runtime checks for Windows CI / native (the Windows lane must assert these).**
1. `DialShift.App/Program.cs` has `[STAThread]`. Call `Start()` on the UI thread **after** the desktop lifetime has started (e.g. in `OnFrameworkInitializationCompleted` after the main window/tray exists).
   - Expected: `SystemEvents` binds its hidden window to that STA thread's message loop, which Avalonia pumps, so the event is raised on the UI thread.
   - If subscribed from an MTA/pool thread, `SystemEvents` creates its own "SystemEvents" thread instead.
   - Record the handler's thread in the smoke. Either way, forward only to the thread-agnostic `NotifyWakeAsync()`.
2. Subscribing before the loop runs must not deadlock or lose the subscription. The test asserts the event is delivered in the post-startup ordering above.
3. Deterministic unsubscribe on shutdown, before the lifetime ends. The process exits within a bounded time (no `SystemEvents` shutdown hang), and a second `Dispose` is a no-op.
4. `PowerModes.Resume` leads to exactly **one** `ResumeFromSleepAsync`, even when the timer-gap heuristic fires for the same wake (idempotent and rate-limited).
5. The timer-gap fallback stays active when `Start()` failed.
6. Manual native: a real sleep/resume on hardware produces `Resume`. CI cannot suspend a hosted runner.
7. Clock check (plan change 1): verify that the monotonic source used for the wake gap advances across sleep on Windows.

---

## Spike 3: macOS `NSWorkspace` wake notification (five §4.4 criteria)

**What was done.** `spikes/nswake` is a plain `net10.0` exe with no RID pinned and CA1416 as an error. It shares `ObjC.cs` and the spike `ISystemPowerEvents`.

`MacPowerEvents`:
1. Loads AppKit with `NativeLibrary.TryLoad`.
2. Reads `NSWorkspaceDidWakeNotification` (and `NSWorkspaceWillSleepNotification`) through `TryGetExport`.
3. Registers the runtime class `DialShiftWakeObserver` **once per process** (renamed in production: the shared `DialShiftNotificationObserver`, D27): `objc_lookUpClass` first, then `objc_allocateClassPair(NSObject)` + `class_addMethod(onWake:/onSleep:, "v@:@")` with `[UnmanagedCallersOnly]` function pointers + `objc_registerClassPair`.
4. Calls `alloc/init` for one observer, then `addObserver:selector:name:object:` on `[[NSWorkspace sharedWorkspace] notificationCenter]`.

`Dispose` calls `removeObserver:`, then releases the observer. `TryStart()` never throws; every failure returns false and sets `LastFailure`.

**Output (main run loop pumped, as in Avalonia; `out-nswake-pump.txt`, exit 0):**
```
PASS N0   TryStart registers observer                          IsActive=True failure=- classRegistrations=1
PASS N1   synthetic DidWake posted from background thread      callbacks=1 transition=Resumed deliveredOnMainThread=False (synchronous on posting thread)
PASS N1b  synthetic DidWake posted on main thread              callbacks=1 deliveredOnMainThread=True
PASS N1c  synthetic WillSleep -> Suspending                    callbacks=1
PASS N2   system-originated NSWorkspaceDidMountNotification    delivered=True onMainThread=True (attach: /dev/disk10s1 Apple_HFS .../nswake-2257/mnt)
PASS N3   create/dispose x5 then post after each dispose       starts=5/5 callbacksAfterDispose=0 classRegistrations=1 liveObservers=0 heap DialShiftWakeObserver instances=0
PASS N5   registration failure -> false, timer-gap still detects wake  badAppKit=False (AppKit not loadable: .../NoSuch.framework/NoSuch); badSymbol=False (symbol missing: NSWorkspaceNoSuchNotification); gapDetections=[False,False,False,True,False]
PASS N6   throwing subscriber contained in [UnmanagedCallersOnly]  caught: subscriber bug
```
With the main run loop **not** running (`--nopump`): N0/N1/N1c/N3/N5/N6 are the same, but `N2 ... delivered=False`. A system-originated workspace notification is never delivered without the main run loop. Dirty exit (observer never removed): process status 0 in 3/3 runs.

N2 used a **real, system-originated** NSWorkspace notification. A 2 MB disk image was attached with `hdiutil ... -nobrowse`, then detached. This proves the observer, the same workspace notification center, and the delivery-thread mechanics end to end, without sleeping the machine. Only the specific wake trigger remains unproven.

| # | Criterion | Evidence | Verdict |
|---|---|---|---|
| 1 | Builds without making `DialShift.App` macOS-only | Plain `net10.0`: 0 warnings / 0 errors for no RID, `-r win-x64` and `-r osx-arm64`, with CA1416 as an error. Native paths guarded by `OperatingSystem.IsMacOS()` | PASS |
| 2 | Fires after lid-close and normal sleep/wake | Callback path proven with synthetic DidWake/WillSleep through the real workspace center (N1, N1b, N1c). System delivery proven (N2) on the **main thread**, and only while the main run loop runs, which Avalonia provides. **Real lid-close and menu sleep are the native manual check NC-08** (this machine cannot be slept from the session) | PARTIAL (native pending) |
| 3 | No retained or leaked observer handles after shutdown | `removeObserver:` before release. No callbacks after dispose (0 in 5 cycles). Class registered once (`classRegistrations=1`). `liveObservers=0`. `heap` shows 0 `DialShiftWakeObserver` instances. Dirty exit is clean | PASS |
| 4 | Isolated entirely behind `MacPowerEvents` | The whole surface is `ISystemPowerEvents`; no ObjC types escape (shape below) | PASS |
| 5 | Registration failure leaves timer-gap recovery functional | Missing framework or missing symbol → `TryStart()=false` with a reason and no exception. A subscriber exception is contained. The gap detector detects the injected 42 s gap. **Caveat:** in production the gap clock must be sleep-inclusive (plan change 1), otherwise the fallback is blind on macOS | PASS (with clock fix) |

**Verdict: ADOPT** `NSWorkspaceDidWakeNotification` for macOS, conditional on NC-08 confirming a real lid-close/sleep wake on Apple Silicon. Keep the timer-gap fallback on the corrected clock. It does not block unification: on failure the adapter is a logged no-op.

**Production shape** (matching `DialShift.App/Platform/Abstractions/ISystemPowerEvents.cs`, i.e. `event EventHandler? Resumed; void Start();` plus `IDisposable`):
```csharp
[SupportedOSPlatform("macos")]
internal sealed class MacPowerEvents : ISystemPowerEvents
{
    public event EventHandler? Resumed;       // raised on the posting thread (main for real wakes, D30); the App forwards to NotifyWakeAsync()
    public void Start();                      // idempotent; on any failure logs power_events.unavailable and returns
    public void Dispose();                    // removeObserver: then release; idempotent
}
```
- It is created at composition time only when `OperatingSystem.IsMacOS()`.
- `Start()` runs after the Avalonia lifetime is up. Registration thread does not matter: the spike registered from a worker thread and still received main-thread delivery.
- The runtime ObjC class is process-global and registered once.
- An instance-to-owner map, not a `GCHandle` in an ivar, routes callbacks.
- `WillSleep` can be exposed later if the coordinator wants `SuspendedBySystem`. It is not needed for §4.4.

---

## Media compatibility corpus (§11)

Legally testable public streams, listened to briefly as an ordinary client, plus local transport cases (`corpus/server.py`, 127.0.0.1:8765). The AVPlayer column is the spike on macOS 26.5.2 arm64 (console binary unless marked `.app`). LibVLC on Windows is **CI/native pending** in every row.

The **Adapter (production)** column is the integrated result: the production `MacAvPlayerPlaybackEngine` ("AV", osx-arm64) and `LibVlcPlaybackEngine` ("VLC†") sources, each driven through the real `PlaybackCoordinator` with 1 Hz ticks by out-of-repo harnesses (session scratchpad: `engine-harness/`, `vlc-harness/`, shared `Corpus.cs`; logs `out-corpus*.txt`, `out-final.txt`). Times are from PlayAsync to coordinator Playing or Failed; kinds are the `kind=` of the coordinator's `playback.failed` log line. Across both runs, 0 of 274 log lines contained credentials, query strings or stream paths.

† VLC = the production `LibVlcPlaybackEngine` against the x86_64 macOS LibVLC 3.0.4 build (`VideoLAN.LibVLC.Mac` 3.1.3.1) under Rosetta. This validates the adapter's event mapping and failure normalization against real LibVLC 3. It does not replace the Windows run with `VideoLAN.LibVLC.Windows` 3.0.23.1. Under Rosetta, rapid player churn sometimes killed the process with SIGILL inside LibVLC: in 4 of 7 adapter stress runs of 160 rapid switches, and in 2 of 5 full functional runs. The same crash reproduced without the adapter, using the legacy `RadioController` pattern alone (1 of 4 runs of 160 switches), so it is environmental. The Windows CI must still stress rapid switching.

| ID | URL | Format | Transport case | AVPlayer spike (macOS 26.5, arm64) | Adapter (production) | LibVLC (Windows) |
|---|---|---|---|---|---|---|
| C1 | `https://ice1.somafm.com/groovesalad-128-mp3` | MP3 | HTTPS Icecast | Plays (0.5–2.7 s to Playing) | AV: plays, 3.6 s to coordinator Playing (1.6–1.8 s in the functional suite, CoreAudio output running) · VLC†: plays, 5.9 s (1.2–1.6 s in the functional suite) | Windows CI/native pending |
| C2 | `https://ice5.somafm.com/groovesalad-128-aac` | AAC (ADTS) | HTTPS Icecast | Plays | AV: plays, 1.8 s · VLC†: plays, 2.2 s | Windows CI/native pending |
| C3 | `https://a.files.bbci.co.uk/ms6/live/3441A116-B12E-4D2F-ACA8-C1984642FA4B/audio/simulcast/hls/nonuk/pc_hd_abr_v2/ak/bbc_world_service.m3u8` | HLS AAC | HTTPS, HTTP/2 | Plays (starts at live edge) | AV: plays, 0.5 s · VLC†: plays, 0.4 s | Windows CI/native pending |
| C4 | `https://stream.radiofrance.fr/franceinterlamusiqueinter/franceinterlamusiqueinter_hifi.m3u8?id=radiofrance` | HLS AAC | HTTPS + query | Plays | AV: plays, 0.6 s · VLC†: plays, 1.5 s | Windows CI/native pending |
| C5 | `https://stream.radios.bzh/hls/boa/aac_hifi.m3u8` | HLS AAC | HTTPS | Plays | AV: plays, 0.5 s · VLC†: plays, 3.0 s | Windows CI/native pending |
| C6 | `http://radiorecord.hostingradio.ru/deep96.aacp` | HE-AAC | cleartext HTTP | Plays (console). `.app` without ATS key: TlsFailure (`-1022`) in 0.1 s. With `NSAllowsArbitraryLoadsForMedia`: plays | AV: plays, 1.1 s (console binary) · VLC†: plays, 2.2 s | Windows CI/native pending |
| C7 | `https://icecast.radiofrance.fr/fip-hifi.aac` | AAC | HTTPS, HTTP/2 Icecast | Plays (4.7–5.4 s) | AV: plays, 3.6 s · VLC†: plays, 1.2 s | Windows CI/native pending |
| C8 | `http://stream.power-radio.de:8020/listen.pls` | MP3 served at a `.pls` path | cleartext, extension mismatch | **Fails** after 10–11 s: `CoreMediaErrorDomain -12646` → UnsupportedFormat. Same server at `/;` plays | AV: UnsupportedFormat after 10.6 s (`CoreMedia -12646`) · VLC†: also parses the MP3 body as a PLS file; no native error, so the coordinator watchdog fails it (Stalled after 25 s). Catalog fix, as in finding 4 | Windows CI/native pending |
| C9 | `http://france16.coollabel-productions.com:8276/;` | MP3 (Shoutcast v2 `/;`) | cleartext | Plays | AV: plays, 1.6 s · VLC†: plays, 1.0 s | Windows CI/native pending |
| C10 | `https://radio.ekodesgarrigues.com/eko-des-garrigues-256k.ogg` | Ogg Vorbis | HTTPS | Plays (macOS 26.5; verify on the minimum OS) | AV: plays, 6.6 s · VLC†: plays, 1.6 s | Windows CI/native pending |
| C11 | `https://st02.sslstream.dlf.de/dlf/02/low/opus/stream.opus?aggregator=web` | Opus | HTTPS 302 → token URL | Plays (macOS 26.5; verify on the minimum OS) | AV: plays, 2.9 s · VLC†: plays, 1.1 s | Windows CI/native pending |
| C12 | `https://onair.net-radio.fr/frequence3dance.flac` | FLAC in Ogg | HTTPS | Plays (macOS 26.5; verify on the minimum OS) | AV: plays, 2.6 s · VLC†: plays, 1.6 s | Windows CI/native pending |
| C13 | `https://streams.br.de/br-klassik_3.m3u` | M3U playlist file | HTTPS | Plays | AV: plays, 3.3 s · VLC†: plays, 3.2 s (the adapter plays the first playlist entry; before that fix it failed as UnsupportedFormat) | Windows CI/native pending |
| C14 | `https://somafm.com/groovesalad.pls` | PLS playlist file | HTTPS | Plays | AV: plays, 2.3 s · VLC†: plays, 4.5 s (first playlist entry) | Windows CI/native pending |
| C15 | `https://st01.sslstream.dlf.de/dlf/01/128/mp3/stream.mp3?aggregator=web` | MP3 | HTTPS 302 with token | Plays | AV: plays, 0.5 s · VLC†: plays, 0.5 s | Windows CI/native pending |
| T1 | `http://127.0.0.1:1/unavailable` | n/a | connection refused | Failed 0.10 s → NetworkUnavailable | AV: NetworkUnavailable, 0.3 s · VLC†: NetworkUnavailable, 0.3–0.9 s ("connection refused" / "cannot connect") | Windows CI/native pending |
| T2 | `https://stream.nonexistent.invalid/radio.mp3` | n/a | DNS failure | Failed 0.1–0.4 s → NetworkUnavailable | AV: NetworkUnavailable, 0.3 s · VLC†: NetworkUnavailable, 0.4–0.9 s ("cannot resolve") | Windows CI/native pending |
| T3 | `https://ice5.somafm.com/does-not-exist-xyz` | n/a | HTTP 404 | Failed 0.9–1.3 s → HttpError | AV: HttpError, 0.8 s · VLC†: HttpError, 1.0 s ("HTTP 404 error") | Windows CI/native pending |
| T4 | local `/403`, `/500`; HLS playlist 404 | n/a | HTTP 403/500/404 | 0.1–0.4 s → HttpError | AV: HttpError for 403 / 500 / HLS 404, 0.3–0.6 s · VLC†: HttpError, 0.3–0.5 s | Windows CI/native pending |
| T5 | `https://expired.badssl.com/`, `self-signed.`, `wrong.host.`, `untrusted-root.` | n/a | TLS certificate failures | 0.7–4.9 s → TlsFailure (`-1202`) | AV: TlsFailure for all four, 0.5–0.6 s · VLC†: TlsFailure for all four, 0.7–1.7 s ("TLS session handshake error") | Windows CI/native pending |
| T6 | local `/redirect-https` (302 → C2); `http://st01.dlf.de/dlf/01/128/mp3/stream.mp3` (302 http→http) | AAC / MP3 | redirects | Plays | AV: both play (1.6 s / 0.5 s) · VLC†: both play (1.2 s / 0.9 s) | Windows CI/native pending |
| T7 | local `/redirect-loop` | n/a | redirect loop | 0.1 s → HttpError (`-1007`) | AV: HttpError, 0.3 s (`-1007`) · VLC†: Unknown, 0.3 s (LibVLC logs only "HTTP connection failure" for this local HTTP/1.0 loop) | Windows CI/native pending |
| T8 | local `/auth/stream.mp3` without and with `user:pass@` | MP3 | HTTP Basic auth | Without: 0.1 s → HttpError (401). With userinfo: plays | AV: without credentials HttpError (0.3 s); with user-info plays (2.3 s) · VLC†: HttpError (0.4 s); plays (1.7 s) | Windows CI/native pending |
| T9 | local `/captive/stream.mp3` (HTTP/1.0 `200 text/html`); `https://example.com/` | HTML | captive-portal style | HTTP/1.0: **no error in 30 s** (watchdog → Stalled). HTTPS HTML: 0.3–0.7 s → UnsupportedFormat | AV: HTTP/1.0 HTML: no native error, coordinator watchdog Stalled at 25.8 s; HTTPS HTML: UnsupportedFormat, 0.3 s · VLC†: UnsupportedFormat for both, 0.3–0.4 s (EndReached before any audio) | Windows CI/native pending |
| T10 | local `/garbage.mp3` | random bytes | undecodable | 16 s → Unknown (`CoreMedia -16830`) | AV: Unknown after 16.3 s (`CoreMedia -16830`) · VLC†: no native error, coordinator watchdog Stalled at 25.3 s | Windows CI/native pending |
| T11 | local `/icy.mp3` | MP3 | Shoutcast v1 `ICY 200 OK` | Plays | AV: plays, 2.6 s · VLC†: plays, 1.2 s | Windows CI/native pending |
| T12 | local `/eos.mp3` | MP3 | server closes (EOS) | `DidPlayToEndTime` (main thread) → EndOfStream | AV: plays, then Ended + EndOfStream at 8.2 s · VLC†: plays, then Ended + EndOfStream at 7.2 s | Windows CI/native pending |
| T13 | local `/stall.mp3` | MP3 | server stops sending, keeps socket | `PlaybackStalled` (main thread), Waiting forever → Buffering, then coordinator watchdog | AV: plays, then Buffering, then failed-to-play-to-end (`CoreMedia -16830`) → Unknown at 17.2 s · VLC†: plays, then Buffering (no time progress for 5 s), coordinator watchdog Stalled at 36.2 s | Windows CI/native pending |
| T14 | local `/hang` | n/a | accepts, never answers | No error in 45 s → coordinator watchdog | AV: coordinator watchdog Stalled at 25.8 s · VLC†: coordinator watchdog Stalled at 25.7 s | Windows CI/native pending |
| T15 | `"not a url"`, `"http://"`, `"ht tp://…"`, `ftp://…`, `file:///…`, `""` | n/a | malformed URL | Managed `Uri` validation → InvalidUrl in ≤1 ms (raw to NSURL: nil or `-1002`) | Both: the coordinator rejects the station before any engine call (InvalidUrl, retry countdown 3 s then 6 s). Engine-level `ftp://` source: Failed(InvalidUrl), raised off the call stack | Windows CI/native pending |
| T16 | any `http://` inside a `.app` without the ATS key | any | ATS | 0.1 s → TlsFailure (`-1022`) | Not re-run (needs the bundled `.app`). Packaging now sets the key (D32), and the native check is NC-07/NC-17 · n/a for LibVLC | n/a (LibVLC does not use ATS) |

Not covered here: a real offline network (Wi-Fi off) and a real captive portal. Both are native manual checks.

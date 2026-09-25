---
paths: DialShift.Core/**
---
# DialShift.Core purity rules

- Zero references to: Avalonia, Windows, macOS, LibVLC, registry, file-system locations, named pipes, processes, UI dispatch.
- Time sources: `IClock` (`DateTimeOffset UtcNow`) for schedules/persisted timestamps; `IMonotonicClock` (`GetTimestamp`/`GetElapsedTime`) for wake-gap/elapsed durations. Wake detection MUST use monotonic time, never `UtcNow`.
- `IPlaybackEngine` is the one allowed boundary port — a contract only, no infrastructure. Lowest-common-denominator: no ObjC/AppKit/CoreFoundation types, metadata optional (nullable capability).
- `PlaybackCoordinator` owns retry/fallback/schedule/wake/cancellation policy. All transitions serialize through ONE `SemaphoreSlim` gate that is never held across an await; every delayed operation validates its generation before acting; per-state event acceptance documented in source.
- `SettingsStore` receives the data directory as a parameter — it never computes filesystem locations.

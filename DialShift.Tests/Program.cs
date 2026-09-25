using DialShift.Tests;
using DialShift.Tests.App;
using DialShift.Tests.Core;
using DialShift.Tests.Fakes;
using DialShift.Tests.Platform;

// Child-process modes used by the process-level single-instance (CT-SI-03) and cross-process log (LOG-D1) checks;
// never part of a normal run.
if (args.Length > 0 && args[0].StartsWith("--si-", StringComparison.Ordinal))
    return await SingleInstanceTests.RunChildAsync(args);
if (args.Length > 0 && args[0] == "--log-child")
    return FileAppLogTests.RunChild(args);

// Deterministic console checks (no test framework). Usage: dotnet run --project DialShift.Tests -- [--filter Scheduler]
return await TestHarness.RunAsync(args,
    new TestSuite("Scheduler", SchedulerTests.Run),
    new TestSuite("ScheduleSession", ScheduleSessionTests.Run),
    new TestSuite("Timezone", TimezoneTests.RunAsync),
    new TestSuite("SettingsStore", SettingsStoreTests.Run),
    new TestSuite("RetryPolicy", RetryPolicyTests.Run),
    new TestSuite("PlaybackCoordinator", PlaybackCoordinatorTests.RunAsync),
    new TestSuite("PlaybackStateMachine", PlaybackStateMachineTests.RunAsync),
    new TestSuite("PlaybackProperty", PlaybackStateMachineTests.PropertyAsync),
    new TestSuite("PlaybackRace", PlaybackStateMachineTests.RacesAsync),
    new TestSuite("UiViewModels", DialShift.Tests.Ui.ViewModelTests.RunAsync),
    new TestSuite("HeadlessUi", DialShift.Tests.Ui.HeadlessUiTests.RunAsync),
    new TestSuite("UiTimeZone", DialShift.Tests.Ui.TimeZoneUiTests.RunAsync),
    new TestSuite("Fakes", FakeSelfTests.RunAsync),
    new TestSuite("SingleInstance", SingleInstanceTests.RunAsync),
    new TestSuite("FileAppLog", FileAppLogTests.RunAsync),
    new TestSuite("AppPaths", AppPathsTests.Run),
    new TestSuite("StartupRegistration", StartupRegistrationTests.RunAsync),
    new TestSuite("FileReveal", FileRevealTests.RunAsync),
    new TestSuite("MonotonicClock", MonotonicClockTests.Run),
    new TestSuite("PowerEvents", PowerEventsTests.Run),
    new TestSuite("LibVlcEngine", LibVlcEngineTests.RunAsync));

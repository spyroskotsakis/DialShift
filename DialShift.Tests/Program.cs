using DialShift.Tests;
using DialShift.Tests.Core;
using DialShift.Tests.Fakes;

// Deterministic console checks (no test framework). Usage: dotnet run --project DialShift.Tests -- [--filter Scheduler]
return await TestHarness.RunAsync(args,
    new TestSuite("Scheduler", SchedulerTests.Run),
    new TestSuite("ScheduleSession", ScheduleSessionTests.Run),
    new TestSuite("SettingsStore", SettingsStoreTests.Run),
    new TestSuite("Fakes", FakeSelfTests.RunAsync));

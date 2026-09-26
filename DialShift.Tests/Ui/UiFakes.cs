using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using DialShift.App.Platform;
using DialShift.App.Services;
using DialShift.App.SingleInstance;
using DialShift.App.ViewModels;
using DialShift.Core;
using DialShift.Core.Catalog;
using DialShift.Core.Playback;

namespace DialShift.Tests.Ui;

/// <summary>
/// One ordered record of what the UI asked for, shared by the fakes of a rig: coordinator calls (<c>coordinator.Toggle</c>),
/// settings saves and commits (<c>settings.save</c>, <c>settings.commit:Schedule</c>), shell calls. HS-04 asserts on the
/// order ("the coordinator call, then a save").
/// </summary>
public sealed class Journal
{
    private readonly Lock gate = new();
    private readonly List<string> entries = [];

    public IReadOnlyList<string> Entries { get { lock (gate) return [.. entries]; } }

    public int Count { get { lock (gate) return entries.Count; } }

    public void Add(string entry) { lock (gate) entries.Add(entry); }

    /// <summary>Entries added since <paramref name="mark"/> (a previous <see cref="Count"/>).</summary>
    public IReadOnlyList<string> Since(int mark) { lock (gate) return [.. entries.Skip(mark)]; }
}

/// <summary>
/// Recording <see cref="IPlaybackCoordinator"/> for routing tests (HS-04, MX-04). Every call is journaled as
/// <c>coordinator.&lt;Name&gt;[:arg]</c>; <see cref="Publish"/> raises <see cref="SnapshotChanged"/> like the real one
/// (from any thread). <see cref="HoldDispose"/> makes <see cref="DisposeAsync"/> hang, for the bounded-quit check (CR-02).
/// </summary>
public sealed class FakeCoordinator(Journal journal, PlaybackSnapshot? initial = null) : IPlaybackCoordinator
{
    private PlaybackSnapshot snapshot = initial ?? PlaybackSnapshot.Initial(60);

    public PlaybackSnapshot Snapshot => Volatile.Read(ref snapshot);

    public event EventHandler<PlaybackSnapshot>? SnapshotChanged;

    /// <summary>When true, <see cref="DisposeAsync"/> never completes.</summary>
    public bool HoldDispose { get; set; }

    public int DisposeCount { get; private set; }

    /// <summary>Thrown by <see cref="StartScheduleAsync"/> when set (startup-failure path, HS-08).</summary>
    public Exception? StartScheduleException { get; set; }

    public void Publish(PlaybackSnapshot value)
    {
        Volatile.Write(ref snapshot, value);
        SnapshotChanged?.Invoke(this, value);
    }

    public Task PlayAsync(Guid stationId) => Record("PlayAsync:" + stationId);
    public Task ToggleAsync() => Record("ToggleAsync");
    public Task StopAsync() => Record("StopAsync");
    public Task NextStationAsync() => Record("NextStationAsync");
    public Task SetVolumeAsync(int volume) => Record("SetVolumeAsync:" + volume);

    public Task StartScheduleAsync()
    {
        journal.Add("coordinator.StartScheduleAsync");
        return StartScheduleException is { } error ? Task.FromException(error) : Task.CompletedTask;
    }

    public Task RefreshScheduleAsync() => Record("RefreshScheduleAsync");
    public Task OnTickAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task NotifyWakeAsync() => Record("NotifyWakeAsync");
    public Task NotifySettingsChangedAsync() => Record("NotifySettingsChangedAsync");
    public Task ForgetStationAsync(Guid stationId) => Record("ForgetStationAsync:" + stationId);

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        journal.Add("coordinator.DisposeAsync");
        return HoldDispose ? new ValueTask(new TaskCompletionSource().Task) : ValueTask.CompletedTask;
    }

    private Task Record(string call)
    {
        journal.Add("coordinator." + call);
        return Task.CompletedTask;
    }
}

/// <summary>
/// The production <see cref="SettingsService"/> (real <see cref="SettingsStore"/> on a temp directory, real "Save failed"
/// dialog path), with every <see cref="SaveAsync"/>/<see cref="CommitAsync"/> journaled first so HS-04 can check that a save
/// follows each command. Behavior is the production service's.
/// </summary>
public sealed class JournalingSettingsService(SettingsService inner, Journal journal) : ISettingsService
{
    public Settings Settings => inner.Settings;

    public event EventHandler? SettingsChanged
    {
        add => inner.SettingsChanged += value;
        remove => inner.SettingsChanged -= value;
    }

    public Task<bool> SaveAsync()
    {
        journal.Add("settings.save");
        return inner.SaveAsync();
    }

    public Task CommitAsync(SettingsChange change)
    {
        journal.Add("settings.commit:" + change);
        return inner.CommitAsync(change);
    }
}

/// <summary>
/// Scriptable <see cref="IDialogService"/> + <see cref="IEditorDialogService"/> for view-model tests. Messages and
/// confirmations are recorded; confirmations answer from <see cref="ConfirmAnswers"/> (default: false). Editors run the
/// queued script (which drives the editor view model like a user would) and return the editor's own result.
/// </summary>
public sealed class RecordingDialogService : IDialogService, IEditorDialogService
{
    public List<(string Title, string Message)> Messages { get; } = [];
    public List<(string Title, string Message, string Confirm, string Cancel)> Confirmations { get; } = [];
    public Queue<bool> ConfirmAnswers { get; } = new();
    public Queue<Func<StationEditorViewModel, Task>> StationScripts { get; } = new();
    public Queue<Func<ScheduleEditorViewModel, Task>> ScheduleScripts { get; } = new();

    public Task ShowMessageAsync(string title, string message)
    {
        Messages.Add((title, message));
        return Task.CompletedTask;
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "Yes", string cancelText = "No")
    {
        Confirmations.Add((title, message, confirmText, cancelText));
        return Task.FromResult(ConfirmAnswers.Count > 0 && ConfirmAnswers.Dequeue());
    }

    public async Task<EditorResult> ShowStationEditorAsync(StationEditorViewModel editor)
    {
        if (StationScripts.Count > 0) await StationScripts.Dequeue()(editor);
        return editor.Result;
    }

    public async Task<EditorResult> ShowScheduleEditorAsync(ScheduleEditorViewModel editor)
    {
        if (ScheduleScripts.Count > 0) await ScheduleScripts.Dequeue()(editor);
        return editor.Result;
    }
}

/// <summary>
/// <see cref="IStartupRegistration"/> double: <see cref="Status"/> is what the OS "has"; <see cref="SetResult"/> (when set)
/// overrides what a write verifies to (a failed write). Calls are counted, so the BHV-59 "exactly one call" rule is checkable.
/// </summary>
public sealed class FakeStartupRegistration : IStartupRegistration
{
    public StartupRegistrationStatus Status { get; set; } = new(false);
    public Func<bool, StartupRegistrationStatus>? SetResult { get; set; }
    public List<bool> SetCalls { get; } = [];
    public int GetCalls { get; private set; }

    public Task<StartupRegistrationStatus> GetStatusAsync(CancellationToken ct = default)
    {
        GetCalls++;
        return Task.FromResult(Status);
    }

    /// <summary>When set, a write stays in flight until this completes (the checkbox must be disabled meanwhile).</summary>
    public TaskCompletionSource? Hold { get; set; }

    /// <summary>When set, a write throws this (the contract forbids it; the view model must cope anyway).</summary>
    public Exception? Throw { get; set; }

    public async Task<StartupRegistrationStatus> SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        SetCalls.Add(enabled);
        if (Hold is { } hold) await hold.Task;
        if (Throw is { } error) throw error;
        Status = SetResult?.Invoke(enabled) ?? new StartupRegistrationStatus(enabled);
        return Status;
    }
}

public sealed class FakeFileReveal : IFileRevealService
{
    public List<string> Paths { get; } = [];
    public Exception? Error { get; set; }

    public Task RevealInFileManagerAsync(string fileOrDirectoryPath, CancellationToken cancellationToken = default)
    {
        Paths.Add(fileOrDirectoryPath);
        return Error is { } error ? Task.FromException(error) : Task.CompletedTask;
    }
}

/// <summary>Records <see cref="IAppShell"/> calls into the journal (<c>shell.ShowMainWindow</c>, ...).</summary>
public sealed class FakeShell(Journal journal) : IAppShell
{
    public void ShowMainWindow() => journal.Add("shell.ShowMainWindow");
    public void HideMainWindow() => journal.Add("shell.HideMainWindow");
    public void Quit() => journal.Add("shell.Quit");
}

/// <summary>
/// The Add dialog's station catalog: answers every load with <see cref="Result"/> (default: loaded, no stations). With
/// <see cref="Hold"/> set, a load waits for it (the loading state, CAT-13; a load that never completes, CAT-04), and a
/// cancelled wait ends with <see cref="OperationCanceledException"/> like the real provider's. <see cref="Fault"/> makes a
/// load throw, which the contract forbids but the dialog must survive. Calls are counted (Edit mode never loads, D62).
/// </summary>
public sealed class FakeCatalogProvider : ICatalogProvider
{
    private int calls;

    public CatalogLoadResult Result { get; set; } = new(CatalogLoadState.Loaded, StationCatalogIndex.Empty, null, null);

    public TaskCompletionSource? Hold { get; set; }

    public Exception? Fault { get; set; }

    public int Calls => Volatile.Read(ref calls);

    /// <summary>The token of the latest call: the dialog cancels it when it closes.</summary>
    public CancellationToken LastToken { get; private set; }

    public async Task<CatalogLoadResult> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref calls);
        LastToken = cancellationToken;
        if (Hold is { } hold) await hold.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (Fault is { } fault) throw fault;
        return Result;
    }
}

/// <summary>
/// Catalog logos without a network. By default every logo fails, so the dialog shows monograms; <see cref="Answer"/> maps
/// a URL to a bitmap (made on the headless platform) instead. Every requested URL is recorded, from any thread.
/// </summary>
public sealed class FakeLogoLoader : ICatalogLogoLoader
{
    private readonly Lock gate = new();
    private readonly List<string> requests = [];

    public Func<string, Bitmap?>? Answer { get; set; }

    public IReadOnlyList<string> Requests { get { lock (gate) return [.. requests]; } }

    public Task<Bitmap?> LoadAsync(string url, CancellationToken cancellationToken)
    {
        lock (gate) requests.Add(url);
        return Task.FromResult(Answer?.Invoke(url));
    }
}

/// <summary>
/// A UI thread for view-model tests that must see what runs where: <see cref="Post"/> only queues, and the queue runs when
/// the test drains it, so nothing a background search produces reaches the view model until the test lets it (CAT-14),
/// and every view-model change happens in the test's own sequence, never concurrently with it.
/// </summary>
public sealed class QueuedUiDispatcher : IUiDispatcher
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> queue = new();

    /// <summary>Posts waiting for the next <see cref="Drain"/>.</summary>
    public int Pending => queue.Count;

    public bool CheckAccess() => true;

    public void Post(Action action) => queue.Enqueue(action);

    public Task InvokeAsync(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => { action(); done.SetResult(); });
        return done.Task;
    }

    /// <summary>Runs every queued post (and the posts they queue) now; returns how many ran.</summary>
    public int Drain()
    {
        var ran = 0;
        while (queue.TryDequeue(out var action))
        {
            action();
            ran++;
        }
        return ran;
    }

    /// <summary>Runs the oldest queued post only (not the posts it queues); false when nothing was queued. A check steps the
    /// UI thread with it to stop between two posts, e.g. after the catalog load applied and before its first search did.</summary>
    public bool RunOne()
    {
        if (!queue.TryDequeue(out var action)) return false;
        action();
        return true;
    }

    /// <summary>Drains until <paramref name="condition"/> holds; false after <paramref name="timeout"/> (default 10 s, a <see cref="TestDeadline"/>).</summary>
    public async Task<bool> RunUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = new TestDeadline(timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            Drain();
            if (condition()) return true;
            if (deadline.HasPassed) return false;
            await Task.Delay(1);
        }
    }
}

/// <summary>A dispatcher for view-model tests without a UI loop: posts run inline, in order.</summary>
public sealed class InlineUiDispatcher : IUiDispatcher
{
    public bool CheckAccess() => true;
    public void Post(Action action) => action();
    public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
}

public sealed class FakePowerEvents(Journal journal) : ISystemPowerEvents
{
    public event EventHandler? Resumed;
    public int StartCount { get; private set; }
    public int DisposeCount { get; private set; }
    public bool HasSubscribers => Resumed != null;

    public void Start() { StartCount++; journal.Add("power.Start"); }
    public void Dispose() { DisposeCount++; journal.Add("power.Dispose"); }
    public void RaiseResumed() => Resumed?.Invoke(this, EventArgs.Empty);
}

/// <summary>An <see cref="ISingleInstanceService"/> that is already primary; disposal (the lock release) is journaled.</summary>
public sealed class FakeSingleInstance(Journal journal) : ISingleInstanceService
{
    public string PipeName => "DialShift-test";
    public event EventHandler? ActivationRequested;
    public int DisposeCount { get; private set; }
    public bool HasSubscribers => ActivationRequested != null;

    public SingleInstanceStartResult TryStartPrimary() => SingleInstanceStartResult.Primary;

    public Task<SingleInstanceActivationResult> ActivateExistingAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(SingleInstanceActivationResult.NoResponse);

    /// <summary>Raised from a thread-pool thread, like the real listener.</summary>
    public Task RaiseActivationAsync() => Task.Run(() => ActivationRequested?.Invoke(this, EventArgs.Empty));

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        journal.Add("single_instance.Dispose");
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// The desktop lifetime <c>Program.Main</c> would hand to <c>App.Run</c>; records <c>MainWindow</c>, the shutdown mode and the
/// exit code. Avalonia 12 marks its lifetime interfaces "not implementable by user code" (a member with an unspeakable
/// name), so this is a <see cref="DispatchProxy"/> that implements the interface at run time; <see cref="Create"/> returns
/// the proxy, which is also this class.
/// </summary>
public class FakeDesktopLifetime : DispatchProxy
{
    private Journal journal = null!;
    private readonly List<EventHandler<ShutdownRequestedEventArgs>> shutdownRequested = [];

    public int? ExitCode { get; private set; }
    public ShutdownMode ShutdownMode { get; private set; }
    public Window? MainWindow { get; private set; }
    public bool HasShutdownRequestedSubscribers => shutdownRequested.Count > 0;

    public static FakeDesktopLifetime Create(Journal journal)
    {
        var proxy = (FakeDesktopLifetime)(object)Create<IClassicDesktopStyleApplicationLifetime, FakeDesktopLifetime>();
        proxy.journal = journal;
        return proxy;
    }

    public IClassicDesktopStyleApplicationLifetime Lifetime => (IClassicDesktopStyleApplicationLifetime)(object)this;

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method?.Name)
        {
            case "get_MainWindow": return MainWindow;
            case "set_MainWindow": MainWindow = (Window?)args![0]; return null;
            case "get_ShutdownMode": return ShutdownMode;
            case "set_ShutdownMode": ShutdownMode = (ShutdownMode)args![0]!; return null;
            case "get_Args": return Array.Empty<string>();
            case "get_Windows": return MainWindow is { } window ? new[] { window } : Array.Empty<Window>();
            case "add_ShutdownRequested": shutdownRequested.Add((EventHandler<ShutdownRequestedEventArgs>)args![0]!); return null;
            case "remove_ShutdownRequested": shutdownRequested.Remove((EventHandler<ShutdownRequestedEventArgs>)args![0]!); return null;
            // App.Run is called from Startup by Program.Main; the tests call it directly, so Startup and Exit never fire.
            case "add_Startup" or "remove_Startup" or "add_Exit" or "remove_Exit": return null;
            case "Shutdown":
                Shutdown((int)args![0]!);
                return null;
            case "TryShutdown":
                Shutdown((int)args![0]!);
                return true;
            default: throw new NotSupportedException($"FakeDesktopLifetime does not implement {method?.Name}.");
        }
    }

    /// <summary>The OS or the app menu asks to quit (<c>ShutdownRequested</c>); returns whether a handler cancelled it.</summary>
    public bool RaiseShutdownRequested()
    {
        var e = new ShutdownRequestedEventArgs();
        foreach (var handler in shutdownRequested.ToList()) handler(Lifetime, e);
        return e.Cancel;
    }

    private void Shutdown(int exitCode)
    {
        ExitCode = exitCode;
        journal.Add("lifetime.Shutdown:" + exitCode);
    }
}

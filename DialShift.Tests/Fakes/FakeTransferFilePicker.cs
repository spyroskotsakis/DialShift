using DialShift.App.Services;

namespace DialShift.Tests.Fakes;

/// <summary>
/// <see cref="ITransferFilePicker"/> double for the settings-transfer checks (brief 5 §7), mirroring <c>FakeFileReveal</c>:
/// every requested Save/Open is recorded and the configured path is returned, so no OS dialog ever blocks automation.
/// <see cref="SavePath"/>/<see cref="OpenPath"/> left null is "the user cancelled" (the contract's null result).
/// </summary>
/// <remarks>
/// <see cref="Hold"/> keeps a pick in flight until the scenario releases it, so the busy state
/// (<c>IsTransferBusy</c>, IE-08) is observable between the button click and the pick's answer — the same seam
/// <c>FakeStartupRegistration.Hold</c> and <c>FakeCatalogProvider.Hold</c> use.
/// </remarks>
public sealed class FakeTransferFilePicker : ITransferFilePicker
{
    /// <summary>The suggested file name of every Save call, in order.</summary>
    public List<string> SaveRequests { get; } = [];

    /// <summary>How many Open calls were made.</summary>
    public int OpenRequests { get; private set; }

    /// <summary>What a Save answers; null is a cancelled picker.</summary>
    public string? SavePath { get; set; }

    /// <summary>What an Open answers; null is a cancelled picker.</summary>
    public string? OpenPath { get; set; }

    /// <summary>When set, a pick waits on this before answering (the busy-state seam).</summary>
    public TaskCompletionSource? Hold { get; set; }

    public async Task<string?> PickSavePathAsync(string defaultName)
    {
        SaveRequests.Add(defaultName);
        if (Hold is { } hold) await hold.Task;
        return SavePath;
    }

    public async Task<string?> PickOpenPathAsync()
    {
        OpenRequests++;
        if (Hold is { } hold) await hold.Task;
        return OpenPath;
    }
}

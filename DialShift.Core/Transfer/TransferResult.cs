namespace DialShift.Core.Transfer;

/// <summary>How a settings transfer ended (brief 5 §6).</summary>
public enum TransferOutcome
{
    /// <summary>Completed: the file was written, or the import was committed.</summary>
    Completed,

    /// <summary>The user cancelled the import confirmation. Nothing was mutated, saved or logged as an error (brief 5 §6).</summary>
    Cancelled,

    /// <summary>A read, validation, write or commit failure. <see cref="TransferResult.ErrorReason"/> says what (brief 5 §5, §6).</summary>
    Failed
}

/// <summary>
/// The outcome of <c>ISettingsTransferService.ExportAsync</c>/<c>ImportAsync</c> (brief 5 §6, §7): the counts, the
/// number of orphan slots dropped on import, and the failure reason. Pure data.
/// </summary>
/// <remarks>
/// <para><b>Placement:</b> this type lives in <c>DialShift.Core.Transfer</c>, beside <see cref="TransferFile"/> and
/// the codec, because it is pure (no I/O, no clock, no ambient state) and both the Core tests and the App service
/// must share one shape with no App → Core cycle. The Core purity rule forbids infrastructure, not plain data.</para>
/// <para><b>Redaction:</b> the transfer service logs this result's counts and outcome only — never station names or
/// URLs (brief 5 §6, the <c>IAppLog</c> redaction rule).</para>
/// </remarks>
public sealed record TransferResult
{
    /// <summary>What happened. <see cref="TransferOutcome.Completed"/> is the only success.</summary>
    public required TransferOutcome Outcome { get; init; }

    /// <summary>Stations written (export) or imported (import) on success; 0 otherwise.</summary>
    public int StationCount { get; init; }

    /// <summary>Schedule slots written (export) or imported (import, after orphan slots are dropped) on success; 0 otherwise.</summary>
    public int ScheduleCount { get; init; }

    /// <summary>Slots dropped on import because their <c>StationId</c> was not among the file's stations (brief 5 §5, §11 #6); 0 otherwise.</summary>
    public int DroppedOrphanCount { get; init; }

    /// <summary>The §5 validation reason or the read/write failure message when <see cref="Outcome"/> is <see cref="TransferOutcome.Failed"/>; null otherwise.</summary>
    public string? ErrorReason { get; init; }

    /// <summary>True only when <see cref="Outcome"/> is <see cref="TransferOutcome.Completed"/>.</summary>
    public bool Succeeded => Outcome == TransferOutcome.Completed;
}

using ModificationsKnown.Core.Saves;

namespace ModificationsKnown.Infrastructure.Saves;

/// <summary>What the one global live path currently is. Every junction is not
/// the same: an MK group link can be copied out, an external link must never be
/// touched, and an unreadable link is “unknown”, not “empty”.</summary>
public enum HostingLiveKind
{
    Missing,
    EmptyDirectory,
    Data,
    MkGroupLink,
    /// <summary>A junction into the profiles root that is not a group manifest
    /// (the pre-group single-profile layout). It is still MK-owned data.</summary>
    MkProfileLink,
    ExternalLink,
    DanglingLink,
    Unknown
}

public sealed record HostingLiveInfo(
    HostingLiveKind Kind,
    string LiveDirectory,
    int FileCount,
    SaveGroupOwner? Owner = null,
    string? LinkTarget = null);

public enum HostingOperationState
{
    /// <summary>Filesystem work is done and held open; the host must persist the
    /// switch and call <c>CompleteHostingChange</c>. Nothing is published yet.</summary>
    Prepared,
    Disabled,
    Enabled,
    AlreadyDisabled,
    AlreadyEnabled,
    NeedsChoice,
    Canceled,
    RunningLocked,
    Failed,
    FailedUnknown,
    PendingRecovery
}

public sealed record HostingOperationResult(
    HostingOperationState State,
    string Message,
    SaveGroup? Group = null,
    IReadOnlyList<SaveGroup>? Groups = null,
    string LiveDirectory = "",
    int FileCount = 0)
{
    /// <summary>Only committed modes count as success; <c>Prepared</c> still
    /// depends on settings persistence.</summary>
    public bool Success => State is HostingOperationState.Disabled
        or HostingOperationState.Enabled
        or HostingOperationState.AlreadyDisabled
        or HostingOperationState.AlreadyEnabled;

    public bool NeedsCommit => State == HostingOperationState.Prepared;
}

/// <summary>The result of undoing a prepared transaction. <c>Complete</c> is
/// true only when every destructive step was reversed; a partial undo must
/// never be reported as a successful rollback.</summary>
public sealed record RollbackResult(
    bool Complete,
    string Description,
    IReadOnlyList<string> FailedSteps)
{
    public static RollbackResult Ok(string description) => new(true, description, []);

    public static RollbackResult Failed(string description, params string[] failedSteps) =>
        new(false, description, failedSteps);
}

public enum HostingEnableChoice
{
    ImportIntoNewGroup,
    OverwriteExistingGroup
}

/// <summary>Chosen in the re-enable dialog. <c>TargetInstallationKey</c> is
/// intentionally absent: the receiving instance is always the active one, so a
/// stale dialog cannot write into the copy the user switched away from.</summary>
public sealed record HostingEnableRequest(
    HostingEnableChoice Choice,
    string? NewGroupName = null,
    Guid? TargetGroupId = null,
    bool BackupTargetGroup = true);

public sealed record HostingEnablePreview(
    HostingLiveInfo Live,
    IReadOnlyList<SaveGroup> Groups,
    Guid? SelectedGroupId,
    string SuggestedName);

/// <summary>A persisted transaction marker. It only needs to be good enough to
/// stop destructive work and tell the user which paths exist; it is deleted on
/// commit and never auto-cleans data it cannot prove is safe.</summary>
public sealed record HostingRecoveryInfo(
    string JournalPath,
    string Operation,
    string Phase,
    string LiveDirectory,
    string? StagingPath,
    string? RollbackPath,
    string? BackupPath,
    DateTimeOffset StartedAt);

// ---------------------------------------------------------------------------
// Journal v2
// ---------------------------------------------------------------------------

public sealed record HostingJournalFile(string RelativePath, long Length, string Sha256);

public sealed record HostingJournalTree(
    IReadOnlyList<HostingJournalFile> Files,
    IReadOnlyList<string> Directories);

/// <summary>Durable progress of a recovery that itself can be interrupted.</summary>
public static class HostingRecoveryStages
{
    public const string None = "";
    public const string LiveMovedAside = "live-moved-aside";
    public const string LiveRestored = "live-restored";
    public const string TargetRestored = "target-restored";
    public const string CatalogRestored = "catalog-restored";
    public const string SettingsPending = "settings-pending";

    public static bool IsKnown(string? stage) => stage is None
        or LiveMovedAside or LiveRestored or TargetRestored
        or CatalogRestored or SettingsPending;
}

/// <summary>
/// A complete, verifiable transaction record: paths and the verified tree are
/// persisted before any destructive step, so a fresh service instance can prove
/// what a temp directory contains without trusting in-memory state.
/// </summary>
public sealed class HostingJournalRecord
{
    public const int CurrentSchema = 2;

    public int Schema { get; set; } = CurrentSchema;

    public string Id { get; set; } = string.Empty;

    public string Operation { get; set; } = string.Empty;

    public string Phase { get; set; } = string.Empty;

    public string InstallationKey { get; set; } = string.Empty;

    public Guid? GroupId { get; set; }

    public string LivePath { get; set; } = string.Empty;

    public string LivePreviousKind { get; set; } = string.Empty;

    public string? LivePreviousTarget { get; set; }

    public Guid? PreviousSelectionGroupId { get; set; }

    public string SourcePath { get; set; } = string.Empty;

    public string? StagingPath { get; set; }

    public string? RollbackPath { get; set; }

    public string? BackupPath { get; set; }

    public string? TargetGroupPath { get; set; }

    public HostingJournalTree? Tree { get; set; }

    /// <summary>Verified content of the old target, captured before it is moved
    /// aside; lets a retry prove the restored target is the original.</summary>
    public HostingJournalTree? RollbackTree { get; set; }

    /// <summary>Progress of an interrupted recovery; empty for normal work.</summary>
    public string RecoveryStage { get; set; } = HostingRecoveryStages.None;

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum HostingJournalScanStatus
{
    None,
    Valid,
    Invalid
}

public sealed record HostingJournalScan(
    HostingJournalScanStatus Status,
    IReadOnlyList<HostingJournalRecord> Records,
    string Message,
    IReadOnlyList<string> Paths);

public enum HostingRecoveryAction
{
    /// <summary>Restore/keep the hosting mode that was in place before the
    /// interrupted operation where that is provably safe.</summary>
    RestoreHosting,

    /// <summary>Finish or roll back to a safe disabled state.</summary>
    DisableHosting
}

public enum HostingRecoveryOutcome
{
    /// <summary>Filesystem/catalog work is done; the host must persist the
    /// required preference and call <c>CompleteHostingRecovery</c>.</summary>
    NeedsSettings,
    Completed,
    Refused,
    FailedUnknown
}

public sealed record HostingRecoveryResult(
    HostingRecoveryOutcome Outcome,
    string Message,
    bool? RequiredEnabled = null,
    string? JournalPath = null,
    IReadOnlyList<string>? Paths = null)
{
    public bool NeedsCommit => Outcome == HostingRecoveryOutcome.NeedsSettings;
}

using System.Text.Json;
using System.Text.RegularExpressions;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Saves;

namespace ModificationsKnown.Infrastructure.Saves;

public sealed partial class SaveGroupService
{
    private static readonly Regex Sha256Pattern = new("^[0-9a-f]{64}$", RegexOptions.Compiled);

    private PendingRecoveryCommit? _pendingRecovery;

    private sealed record PendingRecoveryCommit(
        HostingJournalRecord Record,
        string JournalPath,
        bool RequiredEnabled,
        string SuccessMessage,
        Func<(bool Success, string Description)>? Compensate);

    // ---------------------------------------------------------------------
    // Public entry points
    // ---------------------------------------------------------------------

    /// <summary>
    /// Executes only the recovery step that the journal can prove is safe.
    /// Running/Unknown, invalid/legacy journals, multiple transactions and
    /// unverifiable temp trees all refuse before touching the disk.
    /// </summary>
    public HostingRecoveryResult PrepareHostingRecovery(HostingRecoveryAction action)
    {
        lock (_writeGate)
        {
            if (_pendingRecovery is not null || _pendingHosting is not null || _hostingTransition)
                return Refused("另一个存档操作正在进行；未改动任何文件。");

            var scan = ScanJournalFiles();
            if (scan.Status == HostingJournalScanStatus.None)
                return new HostingRecoveryResult(
                    HostingRecoveryOutcome.Refused, "没有待恢复的存档事务。");
            if (scan.Status != HostingJournalScanStatus.Valid)
                return Refused(scan.Message, scan.Paths);

            if (scan.Records.Count != 1)
                return Refused(
                    "检测到多个未完成的存档事务，无法确定应恢复哪一个；未改动任何文件。",
                    scan.Paths);

            var probe = ProbeAnyRunning();
            if (probe == GameRunningState.Running)
                return Refused("游戏正在运行；请先关闭游戏再恢复。");
            if (probe != GameRunningState.NotRunning)
                return Refused("无法确认游戏是否正在运行（进程信息不可读）；未改动任何文件。");

            var record = scan.Records[0];
            try
            {
                return record.Operation switch
                {
                    "disable" => RecoverDisable(record, action),
                    "enable-empty" or "enable-import" => RecoverEnable(record, action),
                    "enable-overwrite" => RecoverOverwrite(record, action),
                    _ => Refused($"未知的事务类型「{record.Operation}」；未改动任何文件。", scan.Paths)
                };
            }
            catch (Exception exception)
            {
                _recoveryRequired = true;
                _hostingEnabled = false;
                _recoveryMessage =
                    $"恢复失败：{exception.Message}；已保留事务记录与所有副本，状态待恢复。";
                _options.Logger?.Warn(_recoveryMessage);
                return new HostingRecoveryResult(
                    HostingRecoveryOutcome.FailedUnknown,
                    _recoveryMessage,
                    JournalPath: scan.Paths.FirstOrDefault(),
                    Paths: scan.Paths);
            }
        }
    }

    /// <summary>
    /// Config anomaly without any journal (settings missing/corrupt while live
    /// is still a valid MK link). Safe close must run the ordinary, rollbackable
    /// copy transaction; it must not just flip the preference. Invalid journals
    /// are never allowed to take this path.
    /// </summary>
    public HostingOperationResult PrepareDisableHostingWithoutJournalForRecovery(
        GameInstallation installation)
    {
        lock (_writeGate)
        {
            if (_pendingHosting is not null || _hostingTransition || _pendingRecovery is not null)
                return BusyResult();

            var scan = ScanJournalFiles();
            if (scan.Status != HostingJournalScanStatus.None)
                return new HostingOperationResult(
                    HostingOperationState.PendingRecovery,
                    "存在事务记录；不能按无记录配置异常处理。");

            if (!_recoveryRequired) return PrepareDisableHosting();

            var probe = ProbeAnyRunning();
            if (probe == GameRunningState.Running)
                return new HostingOperationResult(
                    HostingOperationState.RunningLocked,
                    "游戏正在运行；请先关闭游戏再恢复。");
            if (probe != GameRunningState.NotRunning)
                return new HostingOperationResult(
                    HostingOperationState.RunningLocked,
                    "无法确认游戏是否正在运行（进程信息不可读）；未改动任何文件。");

            var info = DescribeLiveLocked();
            if (info.Kind is not (HostingLiveKind.MkGroupLink or HostingLiveKind.MkProfileLink))
                return new HostingOperationResult(
                    HostingOperationState.Failed,
                    "当前游戏目录没有指向可确认的 MK 存档；不能走无记录的关闭恢复。");

            var installationKey = GetInstallationKey(installation);
            if (info.Kind == HostingLiveKind.MkGroupLink
                && (info.Owner is null
                    || !string.Equals(info.Owner.InstallationKey, installationKey,
                        StringComparison.OrdinalIgnoreCase)))
                return new HostingOperationResult(
                    HostingOperationState.Failed,
                    "当前存档链接属于另一个实例；未关闭托管。");
            if (info.Kind == HostingLiveKind.MkProfileLink
                && (info.LinkTarget is null
                    || !TargetPathMatchesInstallation(info.LinkTarget, installationKey)))
                return new HostingOperationResult(
                    HostingOperationState.Failed,
                    "当前存档链接属于另一个实例的旧 profile；未关闭托管。");

            return PrepareDisableCopy(info);
        }
    }

    /// <summary>
    /// Called after the host persisted the recovered preference. A failed
    /// preference write keeps the journal and the pending gate; it never
    /// deletes the only proof of what was done.
    /// </summary>
    public HostingRecoveryResult CompleteHostingRecovery(bool persisted, bool enabled)
    {
        lock (_writeGate)
        {
            var pending = _pendingRecovery;
            _pendingRecovery = null;
            if (pending is null)
                return new HostingRecoveryResult(
                    HostingRecoveryOutcome.FailedUnknown, "没有待完成的恢复操作。");

            if (pending.RequiredEnabled != enabled)
                return new HostingRecoveryResult(
                    HostingRecoveryOutcome.FailedUnknown, "恢复的期望状态不一致；未发布新状态。");

            if (persisted)
            {
                _hostingEnabled = enabled;
                _recoveryRequired = false;
                _recoveryMessage = string.Empty;
                DeleteJournal(pending.JournalPath);
                HostingStateChanged?.Invoke(this, EventArgs.Empty);
                return new HostingRecoveryResult(
                    HostingRecoveryOutcome.Completed, pending.SuccessMessage,
                    RequiredEnabled: enabled);
            }

            _hostingEnabled = false;
            _recoveryRequired = true;
            if (pending.Compensate is null)
            {
                _recoveryMessage =
                    $"未能保存托管设置；恢复步骤已完成（{pending.SuccessMessage}），" +
                    $"事务记录与副本保留，可重试：{pending.JournalPath}";
                HostingStateChanged?.Invoke(this, EventArgs.Empty);
                return new HostingRecoveryResult(
                    HostingRecoveryOutcome.Refused,
                    _recoveryMessage,
                    JournalPath: pending.JournalPath);
            }

            (bool Success, string Description) compensation;
            try
            {
                compensation = pending.Compensate();
            }
            catch (Exception exception)
            {
                compensation = (false, exception.Message);
            }

            _recoveryMessage = compensation.Success
                ? $"未能保存托管设置；已补偿恢复现场，事务记录保留：{pending.JournalPath}"
                : $"未能保存托管设置，且补偿失败（{compensation.Description}）；" +
                  $"事务记录与副本保留：{pending.JournalPath}";
            HostingStateChanged?.Invoke(this, EventArgs.Empty);
            return new HostingRecoveryResult(
                compensation.Success
                    ? HostingRecoveryOutcome.Refused
                    : HostingRecoveryOutcome.FailedUnknown,
                _recoveryMessage,
                JournalPath: pending.JournalPath);
        }
    }

    /// <summary>Legacy entry used by the existing “safe close” button. It only
    /// routes to the safe recovery engine; it never installs staging blindly.</summary>
    public HostingOperationResult PrepareDisableHostingFromRecovery()
    {
        var result = PrepareHostingRecovery(HostingRecoveryAction.DisableHosting);
        return result.NeedsCommit
            ? new HostingOperationResult(
                HostingOperationState.Prepared, result.Message,
                LiveDirectory: _options.LiveSaveDirectory)
            : new HostingOperationResult(
                result.Outcome == HostingRecoveryOutcome.Refused
                    ? HostingOperationState.PendingRecovery
                    : HostingOperationState.FailedUnknown,
                result.Message,
                LiveDirectory: _options.LiveSaveDirectory);
    }

    /// <summary>Legacy entry: finish a recovery that required a settings write.</summary>
    public HostingOperationResult CompleteHostingRecoveryAsOperation(bool persisted, bool enabled)
    {
        var result = CompleteHostingRecovery(persisted, enabled);
        return new HostingOperationResult(
            result.Outcome == HostingRecoveryOutcome.Completed
                ? (enabled ? HostingOperationState.Enabled : HostingOperationState.Disabled)
                : (result.Outcome == HostingRecoveryOutcome.Refused
                    ? HostingOperationState.Failed
                    : HostingOperationState.FailedUnknown),
            result.Message,
            LiveDirectory: _options.LiveSaveDirectory);
    }

    // ---------------------------------------------------------------------
    // Per-operation recovery
    // ---------------------------------------------------------------------

    private HostingRecoveryResult RecoverDisable(HostingJournalRecord record, HostingRecoveryAction action)
    {
        var paths = RecoveryPaths(record);
        if (!TryParsePreviousKind(record, out var previousKind))
            return Refused("事务记录的原 live 类型无法识别；未改动任何文件。", paths);

        if (record.Phase is "starting" or "copy")
            return Refused(
                "关闭操作的复制尚未完成，暂存目录不能安装；已保留原链接与事务记录。", paths);

        var live = _options.LiveSaveDirectory;
        var tree = record.Tree;
        var staging = record.StagingPath;
        var stagingExists = staging is not null && Directory.Exists(staging);
        var liveExists = Directory.Exists(live);
        var liveIsLink = liveExists
                         && (IsReparsePoint(live) || _options.Linker.TryGetTarget(live, out _));
        var target = record.LivePreviousTarget ?? string.Empty;

        // Safe close of an already-installed real directory does not need the
        // original group; only re-linking does.
        if (!(action == HostingRecoveryAction.DisableHosting && liveExists && !liveIsLink)
            && !TryResolveDisableTarget(record, out target, out var groupError))
            return Refused(groupError, paths);

        if (liveExists && liveIsLink)
        {
            if (!_options.Linker.TryGetTarget(live, out var current)
                || !PathEquals(current, target))
                return Refused("游戏目录的链接目标与事务记录不一致；未改动任何文件。", paths);

            if (action == HostingRecoveryAction.RestoreHosting)
            {
                if (tree is null || !TreeMatches(target, tree))
                    return Refused("原存档组内容与事务记录不一致，不能保留链接；已保留现场。", paths);
                SetRecoveryStage(record, HostingRecoveryStages.LiveRestored);
                return NeedsSettings(
                    record, true, "存档链接已经就位；正在保存托管设置。", null, paths);
            }

            if (!stagingExists || tree is null || !TreeMatches(staging!, tree))
                return Refused("暂存副本缺失或校验不通过，不能安装；已保留现场。", paths);
            if (!_options.Linker.RemoveJunction(live, out var removeError))
                return RecoveryFailed($"未能移除原存档链接：{removeError}", paths);
            if (!TryMoveDirectory(staging!, live, out var moveError))
                return RecoveryFailed($"未能安装已验证的暂存副本：{moveError}", paths);
            if (!TreeMatches(live, tree))
                return RecoveryFailed("安装后的游戏目录与事务记录不一致；已保留现场。", paths);

            SetRecoveryStage(record, HostingRecoveryStages.LiveRestored);
            return NeedsSettings(
                record, false, "已补完关闭操作；正在保存托管设置。",
                () => RemoveLiveAndRestoreJunction(target, staging!), paths);
        }

        if (liveExists)
        {
            if (tree is null || !TreeMatches(live, tree))
                return Refused(
                    "游戏目录包含新增或变化内容，不能证明它就是事务清单中的副本；未改动任何文件。",
                    paths);

            if (action == HostingRecoveryAction.DisableHosting)
            {
                SetRecoveryStage(record, HostingRecoveryStages.LiveRestored);
                return NeedsSettings(
                    record, false, "存档目录已是与记录一致的普通目录；正在保存托管设置。", null, paths);
            }

            if (tree is null || !TreeMatches(target, tree))
                return Refused("原存档组内容与事务记录不一致，不能重链接；已保留现场。", paths);

            var stagingForRestore = staging;
            if (stagingForRestore is null)
                return Refused("没有可用于保存当前 live 的 staging 路径；未改动任何文件。", paths);

            if (Directory.Exists(stagingForRestore))
            {
                if (!TreeMatches(stagingForRestore, tree))
                    return Refused("暂存目录内容与事务记录不一致，不能覆盖；未改动任何文件。", paths);
                try
                {
                    Directory.Delete(live, recursive: true);
                }
                catch (Exception exception)
                {
                    return RecoveryFailed($"未能移除已验证的 live 副本：{exception.Message}", paths);
                }
            }
            else
            {
                if (!TryMoveDirectory(live, stagingForRestore, out var preserveError))
                    return RecoveryFailed($"未能先保存当前 live：{preserveError}", paths);
                SetRecoveryStage(record, HostingRecoveryStages.LiveMovedAside);
            }

            if (!CreateJunctionOrReport(live, target, out var linkError))
                return RecoveryFailed($"未能重建原存档链接：{linkError}", paths);

            SetRecoveryStage(record, HostingRecoveryStages.LiveRestored);
            return NeedsSettings(
                record, true, "已恢复原存档链接；正在保存托管设置。",
                () => RestoreRealLiveFromStaging(stagingForRestore), paths);
        }

        // live is missing.
        if (action == HostingRecoveryAction.RestoreHosting)
        {
            if (tree is null || !TreeMatches(target, tree))
                return Refused("原存档组内容与事务记录不一致，不能重链接；已保留现场。", paths);
            if (!CreateJunctionOrReport(live, target, out var linkError))
                return RecoveryFailed($"未能恢复原存档链接：{linkError}", paths);
            SetRecoveryStage(record, HostingRecoveryStages.LiveRestored);
            return NeedsSettings(
                record, true, "已恢复原存档链接；正在保存托管设置。",
                () => RemoveJunctionCompensation(), paths);
        }

        if (!stagingExists || tree is null || !TreeMatches(staging!, tree))
            return Refused("暂存副本缺失或校验不通过，不能安装；已保留现场。", paths);
        if (!TryMoveDirectory(staging!, live, out var installError))
            return RecoveryFailed($"未能把已验证的暂存副本安装到游戏目录：{installError}", paths);
        if (!TreeMatches(live, tree))
            return RecoveryFailed("安装后的游戏目录与事务记录不一致；已保留现场。", paths);

        SetRecoveryStage(record, HostingRecoveryStages.LiveRestored);
        return NeedsSettings(
            record, false, "已补完关闭操作并把存档放回游戏目录；正在保存托管设置。",
            () => RemoveLiveAndRestoreJunction(target, staging!), paths);
    }

    private HostingRecoveryResult RecoverEnable(HostingJournalRecord record, HostingRecoveryAction action)
    {
        var paths = RecoveryPaths(record);
        if (record.TargetGroupPath is not { } targetPath
            || !SaveGroupManifestStore.TryRead(targetPath, out _))
            return Refused("记录中的目标存档组不存在或清单损坏；未改动任何文件。", paths);
        if (!TryParsePreviousKind(record, out var previousKind))
            return Refused("事务记录的原 live 类型无法识别；未改动任何文件。", paths);
        if (record.Phase is "starting" or "copy")
            return Refused("开启操作的复制尚未完成，不能继续；已保留现场。", paths);

        var live = _options.LiveSaveDirectory;
        var tree = record.Tree;
        var liveExists = Directory.Exists(live);
        var liveIsTargetLink = liveExists
                               && (IsReparsePoint(live) || _options.Linker.TryGetTarget(live, out _))
                               && _options.Linker.TryGetTarget(live, out var linkTarget)
                               && PathEquals(linkTarget, targetPath);

        if (liveIsTargetLink)
        {
            if (action == HostingRecoveryAction.RestoreHosting)
            {
                if (tree is null || !TreeMatches(targetPath, tree))
                    return Refused("新组内容与事务记录不一致，不能保留链接；已保留现场。", paths);
                if (!TrySelectTargetGroup(record, targetPath, out var targetSelectionError))
                    return RecoveryFailed($"未能持久化目标组选择：{targetSelectionError}", paths);
                SetRecoveryStage(record, HostingRecoveryStages.LiveRestored);
                SetRecoveryStage(record, HostingRecoveryStages.CatalogRestored);
                return NeedsSettings(
                    record, true, "存档链接与选定组已指向新组；正在保存托管设置。",
                    () => CompensateEnableRestore(record), paths);
            }

            if (!CanRestorePreState(record, previousKind, out var backup, out var preStateError))
                return Refused(preStateError, paths);

            if (!_options.Linker.RemoveJunction(live, out var removeError))
                return RecoveryFailed($"未能移除新链接：{removeError}", paths);
            if (!RestoreLivePreState(record, previousKind, backup, out var restoreError))
                return RecoveryFailed($"未能恢复原游戏目录：{restoreError}", paths);
            if (!RestoreSelectionForRecord(record, out var selectionError))
                return RecoveryFailed($"未能恢复选定组：{selectionError}", paths);

            SetRecoveryStage(record, HostingRecoveryStages.LiveRestored);
            SetRecoveryStage(record, HostingRecoveryStages.CatalogRestored);
            return NeedsSettings(
                record, false, "已撤销新链接并恢复原游戏目录与选定组；正在保存关闭设置。", null, paths);
        }

        if (liveExists && !IsReparsePoint(live))
        {
            if (action == HostingRecoveryAction.RestoreHosting)
            {
                if (previousKind != HostingLiveKind.EmptyDirectory
                    || Directory.EnumerateFileSystemEntries(live).Any())
                    return Refused(
                        "游戏目录仍是普通目录，不能自动接回托管；请关闭恢复窗口后重新开启托管。",
                        paths);
                if (tree is null || !TreeMatches(targetPath, tree))
                    return Refused("新组内容与事务记录不一致，不能接回游戏目录；已保留现场。", paths);
                try
                {
                    Directory.Delete(live, recursive: false);
                    if (!CreateJunctionOrReport(live, targetPath, out var emptyLinkError))
                        return RecoveryFailed($"未能创建新组链接：{emptyLinkError}", paths);
                }
                catch (Exception exception)
                {
                    return RecoveryFailed($"未能接回空目录：{exception.Message}", paths);
                }

                if (!TrySelectTargetGroup(record, targetPath, out var emptySelectionError))
                    return RecoveryFailed($"未能选定新组：{emptySelectionError}", paths);
                SetRecoveryStage(record, HostingRecoveryStages.LiveRestored);
                SetRecoveryStage(record, HostingRecoveryStages.CatalogRestored);
                return NeedsSettings(
                    record, true, "已把新组接回游戏目录并选定；正在保存托管设置。",
                    () => CompensateEnableRestore(record), paths);
            }

            if (!RestoreSelectionForRecord(record, out var realSelectionError))
                return RecoveryFailed($"未能恢复选定组：{realSelectionError}", paths);
            SetRecoveryStage(record, HostingRecoveryStages.CatalogRestored);
            return NeedsSettings(
                record, false,
                "游戏目录仍是普通目录；已回退选定组，正在保存关闭设置。", null, paths);
        }

        if (!liveExists)
        {
            if (action == HostingRecoveryAction.RestoreHosting)
            {
                if (tree is null || !TreeMatches(targetPath, tree))
                    return Refused("新组内容与事务记录不一致，不能接回游戏目录；已保留现场。", paths);
                if (!CreateJunctionOrReport(live, targetPath, out var linkError))
                    return RecoveryFailed($"未能创建新组链接：{linkError}", paths);
                if (!TrySelectTargetGroup(record, targetPath, out var linkSelectionError))
                    return RecoveryFailed($"未能选定新组：{linkSelectionError}", paths);
                SetRecoveryStage(record, HostingRecoveryStages.LiveRestored);
                SetRecoveryStage(record, HostingRecoveryStages.CatalogRestored);
                return NeedsSettings(
                    record, true, "已把新组接回游戏目录并选定；正在保存托管设置。",
                    () => CompensateEnableRestore(record), paths);
            }

            if (!CanRestorePreState(record, previousKind, out var backup, out var preStateError))
                return Refused(preStateError, paths);
            if (!RestoreLivePreState(record, previousKind, backup, out var restoreError))
                return RecoveryFailed($"未能恢复原游戏目录：{restoreError}", paths);
            if (!RestoreSelectionForRecord(record, out var preStateSelectionError))
                return RecoveryFailed($"未能恢复选定组：{preStateSelectionError}", paths);
            SetRecoveryStage(record, HostingRecoveryStages.LiveRestored);
            SetRecoveryStage(record, HostingRecoveryStages.CatalogRestored);
            return NeedsSettings(
                record, false, "已恢复原游戏目录与选定组；正在保存关闭设置。", null, paths);
        }

        return Refused("游戏目录的链接状态与事务记录不一致；未改动任何文件。", paths);
    }

    private HostingRecoveryResult RecoverOverwrite(HostingJournalRecord record, HostingRecoveryAction action)
    {
        var paths = RecoveryPaths(record);
        if (record.TargetGroupPath is not { } targetPath)
            return Refused("覆盖事务缺少目标组路径；未改动任何文件。", paths);
        if (record.Phase is "starting" or "copy")
            return Refused("覆盖操作的复制尚未完成，不能继续；已保留现场。", paths);
        if (!TryParsePreviousKind(record, out _))
            return Refused("事务记录的原 live 类型无法识别；未改动任何文件。", paths);

        var live = _options.LiveSaveDirectory;
        var tree = record.Tree;
        var liveExists = Directory.Exists(live);
        var liveIsLink = liveExists
                         && (IsReparsePoint(live) || _options.Linker.TryGetTarget(live, out _));
        var liveIsTargetLink = liveIsLink
                               && _options.Linker.TryGetTarget(live, out var linkTarget)
                               && PathEquals(linkTarget, targetPath);
        if (action == HostingRecoveryAction.RestoreHosting && liveExists && !liveIsLink)
            return Refused(
                "游戏目录仍是普通目录，不能自动恢复托管；未改动任何文件。请保留待恢复现场并重新选择。",
                paths);

        var rollback = record.RollbackPath;
        var rollbackExists = rollback is not null && Directory.Exists(rollback);
        var targetExists = Directory.Exists(targetPath);

        if (rollbackExists
            && (!SaveGroupManifestStore.TryRead(rollback!, out var rollbackManifest)
                || !string.Equals(rollbackManifest.InstallationKey, record.InstallationKey,
                    StringComparison.OrdinalIgnoreCase)
                || (record.GroupId is { } rollbackGroup && rollbackManifest.GroupId != rollbackGroup)))
            return Refused("旧目标副本的清单不可读或不属于该实例；未改动任何文件。", paths);
        if (!rollbackExists && targetExists
            && (!SaveGroupManifestStore.TryRead(targetPath, out var existingManifest)
                || !string.Equals(existingManifest.InstallationKey, record.InstallationKey,
                    StringComparison.OrdinalIgnoreCase)))
            return Refused("目标组清单不可读或不属于该实例，无法验证恢复结果；未改动任何文件。", paths);

        if (action == HostingRecoveryAction.RestoreHosting && liveIsTargetLink)
        {
            if (tree is null || !TreeMatches(targetPath, tree))
                return Refused("覆盖后的目标组内容与记录不一致，不能结案；已保留现场。", paths);
            if (!TrySelectTargetGroup(record, targetPath, out var targetSelectionError))
                return RecoveryFailed($"未能持久化目标组选择：{targetSelectionError}", paths);
            SetRecoveryStage(record, HostingRecoveryStages.LiveRestored);
            SetRecoveryStage(record, HostingRecoveryStages.CatalogRestored);
            return NeedsSettings(
                record, true, "覆盖结果已就位，选定组已读回；正在保存托管设置。", null, paths);
        }

        // Preflight everything required by the rollback before removing the
        // only live copy. A missing or mismatched backup is a refusal, not a
        // reason to unlink first and ask questions later.
        string? backup = null;
        if (liveIsLink || !liveExists)
        {
            if (record.BackupPath is not { } recordedBackup
                || !Directory.Exists(recordedBackup)
                || tree is null
                || !TreeMatches(recordedBackup, tree))
                return Refused(
                    "覆盖恢复所需的 live 原件备份缺失或校验不通过；未改动任何文件。",
                    paths);
            backup = recordedBackup;
        }

        var targetMatchesRollbackTree = record.RollbackTree is not null
                                        && TreeMatches(targetPath, record.RollbackTree);
        var targetAlreadyRestored = targetMatchesRollbackTree
                                    || (record.RollbackTree is null
                                        && record.RecoveryStage is HostingRecoveryStages.TargetRestored
                                            or HostingRecoveryStages.CatalogRestored
                                            or HostingRecoveryStages.SettingsPending);
        if (!rollbackExists)
        {
            if (!targetExists)
                return Refused("旧目标副本与目标目录都不存在，无法恢复；未改动任何文件。", paths);
            if (!targetAlreadyRestored)
                return Refused("旧目标副本缺失，且无法证明目标组已恢复；未改动任何文件。", paths);
        }
        else if (record.RollbackTree is not null
                 && !TreeMatches(rollback!, record.RollbackTree))
        {
            return Refused("旧目标副本内容与记录不一致，不能恢复；未改动任何文件。", paths);
        }

        if (liveIsLink)
        {
            if (!liveIsTargetLink)
                return Refused("live 链接目标与事务记录不一致；未改动任何文件。", paths);
            if (!_options.Linker.RemoveJunction(live, out var removeError))
                return RecoveryFailed($"未能移除覆盖后的存档链接：{removeError}", paths);
        }

        if (!Directory.Exists(live) && backup is not null)
        {
            var restore = RestoreBackupWithFallback(backup, live);
            if (!restore.Success)
                return RecoveryFailed($"未能恢复原游戏目录：{restore.Error}", paths);
            if (tree is null || !TreeMatches(live, tree))
                return RecoveryFailed("恢复后的游戏目录与事务记录不一致；已保留现场。", paths);
            SetRecoveryStage(record, HostingRecoveryStages.LiveRestored);
        }

        if (!targetAlreadyRestored)
        {
            try
            {
                var failed = Path.Combine(
                    BackupsRoot,
                    $"{Path.GetFileName(targetPath)}-{_options.UtcNow():yyyyMMddHHmmss}-{Guid.NewGuid():N}-failed");
                Directory.CreateDirectory(BackupsRoot);
                if (Directory.Exists(targetPath))
                    MoveDirectoryWithFallback(targetPath, failed);
                MoveDirectoryWithFallback(rollback!, targetPath);
            }
            catch (Exception exception)
            {
                return RecoveryFailed($"未能恢复被覆盖的目标组：{exception.Message}", paths);
            }

            if (!SaveGroupManifestStore.TryRead(targetPath, out var targetManifest)
                || !string.Equals(targetManifest.InstallationKey, record.InstallationKey,
                    StringComparison.OrdinalIgnoreCase)
                || (record.GroupId is { } expectedGroup && targetManifest.GroupId != expectedGroup))
                return RecoveryFailed("恢复后的目标组清单身份不一致；已保留现场。", paths);
            if (record.RollbackTree is not null && !TreeMatches(targetPath, record.RollbackTree))
                return RecoveryFailed("恢复后的目标组内容与旧目标快照不一致；已保留现场。", paths);
            SetRecoveryStage(record, HostingRecoveryStages.TargetRestored);
        }

        if (!RestoreSelectionForRecord(record, out var rollbackSelectionError))
            return RecoveryFailed($"目标组已恢复，但未能恢复选定组：{rollbackSelectionError}", paths);
        SetRecoveryStage(record, HostingRecoveryStages.CatalogRestored);

        return NeedsSettings(
            record, false,
            "已恢复被覆盖的目标组、原游戏目录与选定组；正在保存关闭设置。", null, paths);
    }

    // ---------------------------------------------------------------------
    // Recovery helpers
    // ---------------------------------------------------------------------

    private HostingRecoveryResult NeedsSettings(
        HostingJournalRecord record,
        bool requiredEnabled,
        string message,
        Func<(bool Success, string Description)>? compensate,
        IReadOnlyList<string> paths)
    {
        var journalPath = Path.Combine(JournalRoot, record.Id + ".json");
        SetRecoveryStage(record, HostingRecoveryStages.SettingsPending);
        _pendingRecovery = new PendingRecoveryCommit(
            record, journalPath, requiredEnabled, message, compensate);
        return new HostingRecoveryResult(
            HostingRecoveryOutcome.NeedsSettings,
            message,
            RequiredEnabled: requiredEnabled,
            JournalPath: journalPath,
            Paths: paths);
    }

    private HostingRecoveryResult Refused(string message, IReadOnlyList<string>? paths = null)
    {
        _hostingEnabled = false;
        _recoveryRequired = true;
        _recoveryMessage = message;
        return new HostingRecoveryResult(
            HostingRecoveryOutcome.Refused, message, Paths: paths);
    }

    private HostingRecoveryResult RecoveryFailed(string message, IReadOnlyList<string> paths)
    {
        _hostingEnabled = false;
        _recoveryRequired = true;
        _recoveryMessage = message;
        return new HostingRecoveryResult(
            HostingRecoveryOutcome.FailedUnknown, message, Paths: paths);
    }

    private bool TryResolveDisableTarget(
        HostingJournalRecord record,
        out string target,
        out string error)
    {
        target = record.LivePreviousTarget ?? string.Empty;
        if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target))
        {
            error = "事务记录中的原存档组目录不存在，无法恢复；未改动任何文件。";
            return false;
        }

        if (record.InstallationKey.Length == 0
            || !TargetPathMatchesInstallation(target, record.InstallationKey))
        {
            error = "事务记录中的原存档组不属于记录中的实例；未改动任何文件。";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private bool TargetPathMatchesInstallation(string target, string installationKey)
    {
        var parent = Path.GetDirectoryName(target) ?? string.Empty;
        if (PathEquals(parent, ProfilesRoot))
            return PathEquals(Path.GetFileName(target), installationKey);
        return PathEquals(Path.GetFileName(parent), installationKey);
    }

    private bool TryParsePreviousKind(HostingJournalRecord record, out HostingLiveKind kind)
    {
        var value = record.LivePreviousKind;
        if (Enum.TryParse<HostingLiveKind>(value, ignoreCase: true, out kind)
            && Enum.IsDefined(typeof(HostingLiveKind), kind))
            return true;

        // Older schema-2 markers used short aliases.
        kind = value.ToLowerInvariant() switch
        {
            "junction" or "link" => HostingLiveKind.MkGroupLink,
            "dir" or "directory" => HostingLiveKind.Data,
            "empty" => HostingLiveKind.EmptyDirectory,
            "missing" => HostingLiveKind.Missing,
            _ => HostingLiveKind.Unknown
        };
        return kind != HostingLiveKind.Unknown;
    }

    private void SetRecoveryStage(HostingJournalRecord record, string stage)
    {
        if (record.RecoveryStage == stage) return;
        UpdateJournal(Path.Combine(JournalRoot, record.Id + ".json"),
            current => current.RecoveryStage = stage);
        record.RecoveryStage = stage;
    }

    private static bool TryMoveDirectory(string source, string destination, out string error)
    {
        error = string.Empty;
        try
        {
            Directory.Move(source, destination);
            if (!Directory.Exists(destination))
            {
                error = "移动后目标目录不存在";
                return false;
            }

            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private bool TrySelectTargetGroup(
        HostingJournalRecord record,
        string targetPath,
        out string error)
    {
        if (record.GroupId is { } groupId)
            return RestoreSelectionForRecord(record, out error, groupId.ToString());

        if (SaveGroupManifestStore.TryRead(targetPath, out var manifest))
            return RestoreSelectionForRecord(record, out error, manifest.GroupId.ToString());

        error = "目标组缺少可读的组标识。";
        return false;
    }

    /// <summary>Proves the pre-operation live state can be recreated before any
    /// junction is removed. A recorded backup that is missing/mismatched is a
    /// refusal, not an empty directory.</summary>
    private bool CanRestorePreState(
        HostingJournalRecord record,
        HostingLiveKind previousKind,
        out string? backup,
        out string error)
    {
        backup = null;
        error = string.Empty;
        if (previousKind is HostingLiveKind.Missing or HostingLiveKind.EmptyDirectory)
        {
            if (record.BackupPath is { } recorded
                && (!Directory.Exists(recorded)
                    || record.Tree is null
                    || !TreeMatches(recorded, record.Tree)))
            {
                error = "记录中的原目录备份缺失或与事务清单不一致；未改动任何文件。";
                return false;
            }

            return true;
        }

        if (previousKind is HostingLiveKind.Data or HostingLiveKind.MkGroupLink
            or HostingLiveKind.MkProfileLink)
        {
            if (record.BackupPath is not { } recordedBackup
                || !Directory.Exists(recordedBackup)
                || record.Tree is null
                || !TreeMatches(recordedBackup, record.Tree))
            {
                error = "恢复原 live 所需的备份缺失或内容不一致；未改动任何文件。";
                return false;
            }

            backup = recordedBackup;
            return true;
        }

        error = "记录中的原 live 类型不能安全恢复；未改动任何文件。";
        return false;
    }

    private bool RestoreLivePreState(
        HostingJournalRecord record,
        HostingLiveKind previousKind,
        string? backup,
        out string error)
    {
        error = string.Empty;
        var live = _options.LiveSaveDirectory;
        try
        {
            if (backup is not null)
            {
                var restored = RestoreBackupWithFallback(backup, live);
                if (!restored.Success)
                {
                    error = restored.Error ?? "原目录恢复失败";
                    return false;
                }
            }
            else if (previousKind == HostingLiveKind.EmptyDirectory)
            {
                Directory.CreateDirectory(live);
            }
            else if (previousKind == HostingLiveKind.Missing)
            {
                if (Directory.Exists(live))
                {
                    error = "应恢复为缺失路径，但目录仍存在";
                    return false;
                }
            }
            else
            {
                error = "没有可用于恢复的备份。";
                return false;
            }

            return VerifyLivePreState(previousKind, out error);
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private bool VerifyLivePreState(HostingLiveKind previousKind, out string error)
    {
        error = string.Empty;
        var live = _options.LiveSaveDirectory;
        if (previousKind == HostingLiveKind.Missing)
        {
            if (Directory.Exists(live) || _options.Linker.Exists(live))
            {
                error = "恢复后 live 路径仍存在，未达到缺失状态";
                return false;
            }

            return true;
        }

        if (!Directory.Exists(live) || IsReparsePoint(live))
        {
            error = "恢复后 live 不是普通真实目录";
            return false;
        }

        if (previousKind == HostingLiveKind.EmptyDirectory
            && Directory.EnumerateFileSystemEntries(live).Any())
        {
            error = "恢复后空目录不为空";
            return false;
        }

        return true;
    }

    private bool RestoreSelectionForRecord(
        HostingJournalRecord record,
        out string error,
        string? selectionOverride = null)
    {
        error = string.Empty;
        var installation = FindInstallation(record.InstallationKey);
        if (installation is null)
        {
            error = "找不到记录中的游戏实例，不能确认选定组。";
            return false;
        }

        Guid? expected;
        if (selectionOverride is null)
        {
            expected = record.PreviousSelectionGroupId;
            if (!RestoreSelection(installation, expected))
            {
                error = "恢复选定组失败。";
                return false;
            }
        }
        else
        {
            if (!Guid.TryParse(selectionOverride, out var selection))
            {
                error = "目标组标识无效。";
                return false;
            }

            expected = selection;
            if (!TryPersistSelection(installation, selection, out error))
                return false;
        }

        if (GetNextLaunchGroupId(installation) != expected)
        {
            error = "选定组读回与事务记录不一致。";
            return false;
        }

        return true;
    }

    private bool CreateJunctionOrReport(string live, string target, out string error)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(live)!);
        return _options.Linker.CreateJunction(live, target, out error)
               && _options.Linker.TryGetTarget(live, out var readBack)
               && PathEquals(readBack, target);
    }

    private (bool Success, string Description) CompensateEnableRestore(
        HostingJournalRecord record)
    {
        try
        {
            if (!TryParsePreviousKind(record, out var previousKind))
                return (false, "无法识别原 live 类型");

            var live = _options.LiveSaveDirectory;
            if (IsReparsePoint(live) || _options.Linker.TryGetTarget(live, out _))
            {
                if (!_options.Linker.RemoveJunction(live, out var removeError))
                    return (false, removeError);
            }

            if (record.BackupPath is { } backup && Directory.Exists(backup))
            {
                var restored = RestoreBackupWithFallback(backup, live);
                if (!restored.Success) return (false, restored.Error ?? "原目录恢复失败");
            }
            else if (previousKind == HostingLiveKind.EmptyDirectory)
            {
                Directory.CreateDirectory(live);
            }
            else if (previousKind == HostingLiveKind.Missing)
            {
                if (Directory.Exists(live) || _options.Linker.Exists(live))
                    return (false, "应恢复为缺失路径，但 live 仍存在");
            }
            else if (record.StagingPath is { } staging
                     && Directory.Exists(staging))
            {
                if (Directory.Exists(live))
                    return (false, "live 仍存在，拒绝用 staging 覆盖");
                Directory.Move(staging, live);
            }
            else
            {
                return (false, "没有可用于恢复的备份或暂存副本");
            }

            if (!VerifyLivePreState(previousKind, out var verifyError))
                return (false, verifyError);
            if (!RestoreSelectionForRecord(record, out var selectionError))
                return (false, selectionError);
            return (true, "已撤销恢复时创建的链接");
        }
        catch (Exception exception)
        {
            return (false, exception.Message);
        }
    }

    private (bool Success, string Description) RemoveJunctionCompensation()
    {
        try
        {
            if (_options.Linker.Exists(_options.LiveSaveDirectory)
                && !_options.Linker.RemoveJunction(_options.LiveSaveDirectory, out var error))
                return (false, error);
            if (Directory.Exists(_options.LiveSaveDirectory)
                || _options.Linker.Exists(_options.LiveSaveDirectory))
                return (false, "移除链接后 live 仍存在");
            return (true, "已移除恢复时创建的链接");
        }
        catch (Exception exception)
        {
            return (false, exception.Message);
        }
    }

    private (bool Success, string Description) RemoveLiveAndRestoreJunction(
        string target,
        string? preserveAt = null)
    {
        try
        {
            if (Directory.Exists(_options.LiveSaveDirectory)
                && !IsReparsePoint(_options.LiveSaveDirectory))
            {
                if (preserveAt is not null && !Directory.Exists(preserveAt))
                {
                    Directory.Move(_options.LiveSaveDirectory, preserveAt);
                }
                else if (preserveAt is not null)
                {
                    if (!TreesEqual(
                            CaptureTree(preserveAt, rejectNestedLinks: false),
                            CaptureTree(_options.LiveSaveDirectory, rejectNestedLinks: false)))
                        return (false, "live 与 staging 内容不一致，拒绝删除");
                    Directory.Delete(_options.LiveSaveDirectory, recursive: true);
                }
                else
                {
                    Directory.Delete(_options.LiveSaveDirectory, recursive: true);
                }
            }

            if (!CreateJunctionOrReport(_options.LiveSaveDirectory, target, out var error))
                return (false, error);
            return (true, "已恢复原存档链接");
        }
        catch (Exception exception)
        {
            return (false, exception.Message);
        }
    }

    private (bool Success, string Description) RestoreRealLiveFromStaging(string staging)
    {
        try
        {
            var live = _options.LiveSaveDirectory;
            if (IsReparsePoint(live) || _options.Linker.TryGetTarget(live, out _))
            {
                if (!_options.Linker.RemoveJunction(live, out var removeError))
                    return (false, removeError);
            }
            else if (Directory.Exists(live))
            {
                return (false, "live 已是真实目录，未覆盖");
            }

            if (!Directory.Exists(live))
            {
                if (!Directory.Exists(staging)) return (false, "staging 不存在");
                Directory.Move(staging, live);
            }
            return (true, "已恢复真实游戏目录");
        }
        catch (Exception exception)
        {
            return (false, exception.Message);
        }
    }

    private static IReadOnlyList<string> RecoveryPaths(HostingJournalRecord record) =>
        new[]
        {
            record.LivePath, record.StagingPath, record.RollbackPath, record.BackupPath
        }
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Cast<string>()
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    // ---------------------------------------------------------------------
    // Journal v2
    // ---------------------------------------------------------------------

    private string CreateJournal(
        string operation,
        string installationKey,
        Guid? groupId,
        string live,
        string previousKind,
        string? previousTarget,
        string source,
        string? staging,
        string? rollback,
        string? backup,
        string? targetGroupPath)
    {
        var record = new HostingJournalRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Operation = operation,
            Phase = "starting",
            InstallationKey = installationKey,
            GroupId = groupId,
            LivePath = live,
            LivePreviousKind = previousKind,
            LivePreviousTarget = previousTarget,
            SourcePath = source,
            StagingPath = staging,
            RollbackPath = rollback,
            BackupPath = backup,
            TargetGroupPath = targetGroupPath
        };

        Directory.CreateDirectory(JournalRoot);
        var path = Path.Combine(JournalRoot, record.Id + ".json");
        WriteJournalAtomic(path, record);
        return path;
    }

    private void UpdateJournal(string path, string phase) =>
        UpdateJournal(path, record => record.Phase = phase);

    private void UpdateJournal(string path, string phase, HostingJournalTree tree) =>
        UpdateJournal(path, record =>
        {
            record.Phase = phase;
            record.Tree = tree;
        });

    private void UpdateJournal(string path, Action<HostingJournalRecord> mutate)
    {
        var record = ReadJournal(path)
                     ?? throw new IOException($"事务记录不存在或无法读取：{path}");
        mutate(record);
        WriteJournalAtomic(path, record);
    }

    private static void WriteJournalAtomic(string path, HostingJournalRecord record)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(record, JournalJsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    private static HostingJournalRecord? ReadJournal(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<HostingJournalRecord>(
                File.ReadAllText(path), JournalJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static void DeleteJournal(string? path)
    {
        if (path is null) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A leftover marker is handled by the startup recovery check.
        }
    }

    private HostingJournalScan ScanJournalFiles()
    {
        try
        {
            if (File.Exists(JournalRoot))
                return new HostingJournalScan(
                    HostingJournalScanStatus.Invalid, [],
                    "存档事务记录路径不是目录；为避免误恢复，MK 已暂停存档托管与启动。",
                    [JournalRoot]);

            if (!Directory.Exists(JournalRoot))
                return new HostingJournalScan(HostingJournalScanStatus.None, [], string.Empty, []);

            var files = Directory.EnumerateFiles(JournalRoot, "*.json").ToList();
            if (files.Count == 0)
                return new HostingJournalScan(HostingJournalScanStatus.None, [], string.Empty, []);

            var records = new List<HostingJournalRecord>();
            var invalid = new List<string>();
            var errors = new List<string>();
            foreach (var path in files)
            {
                var record = ReadJournal(path);
                var error = string.Empty;
                if (record is null || !TryValidateJournal(record, path, out error))
                {
                    invalid.Add(path);
                    if (!string.IsNullOrWhiteSpace(error)) errors.Add($"{path}: {error}");
                    continue;
                }

                records.Add(record);
            }

            if (invalid.Count > 0)
            {
                var invalidMessage =
                    "检测到无法完整校验的存档事务记录；为避免误恢复，MK 已暂停存档托管与启动。" +
                    (errors.Count > 0 ? $" {string.Join("；", errors.Take(3))}" : string.Empty) +
                    " 请保留现场并检查事务目录。";
                return new HostingJournalScan(
                    HostingJournalScanStatus.Invalid, records, invalidMessage,
                    invalid.Concat(records.Select(record =>
                        Path.Combine(JournalRoot, record.Id + ".json"))).ToList());
            }

            var message = records.Count == 1
                ? $"检测到未完成的存档托管操作（{records[0].Operation} · {records[0].Phase}）；" +
                  "为避免覆盖存档，MK 已暂停存档托管与启动。请选择恢复托管或安全关闭。"
                : "检测到多个未完成的存档事务，无法确定应恢复哪一个；MK 已暂停存档托管与启动。";

            return new HostingJournalScan(
                HostingJournalScanStatus.Valid,
                records,
                message,
                records.Select(record => Path.Combine(JournalRoot, record.Id + ".json")).ToList());
        }
        catch (Exception exception)
        {
            _options.Logger?.Warn($"枚举事务记录失败：{exception.Message}");
            return new HostingJournalScan(
                HostingJournalScanStatus.Invalid, [],
                "无法读取存档事务记录目录；为避免误恢复，MK 已暂停存档托管与启动。" +
                $"（{exception.Message}）",
                [JournalRoot]);
        }
    }

    private bool TryValidateJournal(HostingJournalRecord record, string journalPath, out string error)
    {
        error = string.Empty;
        if (record.Schema != HostingJournalRecord.CurrentSchema)
        {
            error = $"不支持的事务记录 schema={record.Schema}";
            return false;
        }

        if (record.Id.Length == 0
            || record.InstallationKey.Length == 0
            || record.LivePath.Length == 0
            || record.SourcePath.Length == 0)
        {
            error = "事务记录缺少必要身份或路径字段";
            return false;
        }

        if (!string.Equals(
                Path.GetFileNameWithoutExtension(journalPath), record.Id,
                StringComparison.OrdinalIgnoreCase))
        {
            error = "事务记录的文件名与事务 ID 不一致";
            return false;
        }

        if (!PathEquals(record.LivePath, _options.LiveSaveDirectory))
        {
            error = "事务记录的 live 路径与当前配置不一致";
            return false;
        }

        if (!TryParsePreviousKind(record, out _))
        {
            error = "事务记录的原 live 类型无效";
            return false;
        }

        if (!HostingRecoveryStages.IsKnown(record.RecoveryStage))
        {
            error = "事务记录的恢复阶段无效";
            return false;
        }

        var phases = record.Operation switch
        {
            "disable" => new[] { "starting", "copy", "verify-source", "remove-link", "install-live", "prepared" },
            "enable-empty" or "enable-import" =>
                new[] { "starting", "copy", "verify-source", "backup-live", "link", "prepared" },
            "enable-overwrite" => new[]
            {
                "starting", "copy", "verify-source", "stage-old-target", "install-target",
                "backup-live", "link", "prepared"
            },
            _ => []
        };
        if (phases.Length == 0 || !phases.Contains(record.Phase, StringComparer.Ordinal))
        {
            error = $"未知的事务类型或阶段：{record.Operation}/{record.Phase}";
            return false;
        }

        if (!IsSafeRecoveryPath(record.StagingPath, record.Operation, record.TargetGroupPath)
            || !IsSafeRecoveryPath(record.RollbackPath, record.Operation, record.TargetGroupPath)
            || !IsSafeRecoveryPath(record.BackupPath, "backup", null))
        {
            error = "事务记录包含越界或重叠的临时路径";
            return false;
        }

        if (record.TargetGroupPath is not null
            && (!IsUnder(record.TargetGroupPath, ProfilesRoot)
                || PathEquals(record.TargetGroupPath, ProfilesRoot)
                || !TargetPathMatchesInstallation(record.TargetGroupPath, record.InstallationKey)
                || IsUnder(record.TargetGroupPath, _options.LiveSaveDirectory)
                || IsUnder(_options.LiveSaveDirectory, record.TargetGroupPath)
                || PathContainsReparsePoint(record.TargetGroupPath)))
        {
            error = "事务记录的目标组路径越界或指向链接";
            return false;
        }

        if (!PathEquals(record.SourcePath, record.LivePath)
            && (record.TargetGroupPath is null
                || !PathEquals(record.SourcePath, record.TargetGroupPath))
            && !IsUnder(record.SourcePath, ProfilesRoot))
        {
            error = "事务记录的源路径不属于当前存档范围";
            return false;
        }

        if (record.TargetGroupPath is not null
            && Directory.Exists(record.TargetGroupPath)
            && SaveGroupManifestStore.TryRead(record.TargetGroupPath, out var targetManifest)
            && (!string.Equals(targetManifest.InstallationKey, record.InstallationKey,
                    StringComparison.OrdinalIgnoreCase)
                || (record.GroupId is { } groupId && targetManifest.GroupId != groupId)))
        {
            error = "事务记录的目标组身份与清单不一致";
            return false;
        }

        var temporaryPaths = new[] { record.StagingPath, record.RollbackPath, record.BackupPath }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToList();
        for (var left = 0; left < temporaryPaths.Count; left++)
        for (var right = left + 1; right < temporaryPaths.Count; right++)
        {
            if (PathEquals(temporaryPaths[left], temporaryPaths[right])
                || IsUnder(temporaryPaths[left], temporaryPaths[right])
                || IsUnder(temporaryPaths[right], temporaryPaths[left]))
            {
                error = "事务记录的临时路径互相重叠";
                return false;
            }
        }

        if (record.Phase is not ("starting" or "copy") && record.Tree is null)
        {
            error = "事务记录缺少已校验的文件清单";
            return false;
        }

        if (record.Tree is not null && !IsValidTree(record.Tree, out error))
            return false;
        if (record.RollbackTree is not null && !IsValidTree(record.RollbackTree, out error))
            return false;

        return true;
    }

    private bool IsSafeRecoveryPath(string? path, string kind, string? targetGroupPath)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        if (!Path.IsPathFullyQualified(path)) return false;
        if (PathEquals(path, _options.LiveSaveDirectory)) return false;
        if (IsUnder(path, _options.LiveSaveDirectory)
            || IsUnder(_options.LiveSaveDirectory, path))
            return false;
        if (PathEquals(path, ProfilesRoot) || PathEquals(path, BackupsRoot)) return false;

        var allowed = kind switch
        {
            "backup" => IsUnder(path, BackupsRoot),
            "disable" => IsUnder(path, StagingRoot)
                          || IsUnder(path, Path.GetDirectoryName(_options.LiveSaveDirectory) ?? string.Empty),
            "enable-overwrite" => targetGroupPath is not null
                                   && IsUnder(path, Path.GetDirectoryName(targetGroupPath) ?? string.Empty),
            _ => IsUnder(path, StagingRoot)
        };
        if (!allowed) return false;

        return !PathContainsReparsePoint(path);
    }

    private static bool PathContainsReparsePoint(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full) ?? string.Empty;
            var current = root;
            foreach (var segment in full[root.Length..]
                         .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (!Directory.Exists(current) && !File.Exists(current)) break;
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            }
        }
        catch
        {
            return true;
        }

        return false;
    }

    private static bool IsValidTree(HostingJournalTree tree, out string error)
    {
        error = string.Empty;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in tree.Files)
        {
            if (!IsSafeRelativePath(file.RelativePath)
                || file.Length < 0
                || !Sha256Pattern.IsMatch(file.Sha256)
                || !seen.Add(file.RelativePath))
            {
                error = $"事务文件清单包含非法条目：{file.RelativePath}";
                return false;
            }
        }

        foreach (var directory in tree.Directories)
        {
            if (!IsSafeRelativePath(directory) || !seen.Add(directory))
            {
                error = $"事务目录清单包含非法条目：{directory}";
                return false;
            }
        }

        return true;
    }

    private static bool IsSafeRelativePath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return false;
        if (relative.Contains(':')) return false;
        return relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .All(segment => segment != "..");
    }

    private bool TreeMatches(string directory, HostingJournalTree expected)
    {
        try
        {
            if (IsReparsePoint(directory)) return false;
            var actual = CaptureTree(directory, rejectNestedLinks: true);
            if (actual.Files.Count != expected.Files.Count
                || actual.Directories.Count != expected.Directories.Count)
                return false;

            for (var index = 0; index < actual.Directories.Count; index++)
            {
                if (!string.Equals(actual.Directories[index], expected.Directories[index],
                        StringComparison.Ordinal))
                    return false;
            }

            for (var index = 0; index < actual.Files.Count; index++)
            {
                var file = actual.Files[index];
                var recorded = expected.Files[index];
                if (!string.Equals(file.RelativePath, recorded.RelativePath, StringComparison.Ordinal)
                    || file.Length != recorded.Length
                    || !string.Equals(file.Hash, recorded.Sha256, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static HostingJournalTree ToJournalTree(TreeSnapshot snapshot) =>
        new(
            snapshot.Files
                .Select(file => new HostingJournalFile(file.RelativePath, file.Length, file.Hash))
                .ToList(),
            snapshot.Directories.ToList());
}

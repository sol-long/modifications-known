using System.Text.Json;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Saves;

namespace ModificationsKnown.Infrastructure.Saves;

public sealed partial class SaveGroupService
{
    private static readonly JsonSerializerOptions JournalJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private bool _hostingEnabled = true;
    private bool _hostingTransition;
    private bool _recoveryRequired;
    private string _recoveryMessage = string.Empty;
    private PendingHostingChange? _pendingHosting;

    /// <summary>Committed hosting mode. The host sets this from settings at
    /// startup; tests may set it directly.</summary>
    public SaveHostingState HostingState
    {
        get
        {
            lock (_writeGate)
            {
                return ComputeHostingStateLocked();
            }
        }
    }

    private SaveHostingState ComputeHostingStateLocked()
    {
        if (_hostingTransition) return SaveHostingState.Transition;
        if (_recoveryRequired) return SaveHostingState.PendingRecovery;
        return _hostingEnabled ? SaveHostingState.Enabled : SaveHostingState.Disabled;
    }

    public string HostingStatusMessage
    {
        get
        {
            lock (_writeGate)
            {
                return _recoveryRequired ? _recoveryMessage : string.Empty;
            }
        }
    }

    public string JournalRoot => _options.JournalRoot
        ?? Path.Combine(Path.GetDirectoryName(ProfilesRoot) ?? ProfilesRoot, "save-groups-journal");

    public event EventHandler? HostingStateChanged;

    /// <summary>Initializes the committed state from settings. Does not write
    /// files or resolve an inconsistent live link by itself.</summary>
    public void SetHostingStateFromSettings(bool enabled)
    {
        lock (_writeGate)
        {
            _hostingEnabled = enabled;
            _recoveryRequired = false;
            _recoveryMessage = string.Empty;
        }

        HostingStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void MarkHostingPendingRecovery(string message)
    {
        lock (_writeGate)
        {
            _hostingEnabled = false;
            _recoveryRequired = true;
            _recoveryMessage = message;
        }

        HostingStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Scans transaction markers. Any valid or unreadable marker keeps
    /// the service in <c>PendingRecovery</c>; nothing is moved or deleted here.</summary>
    public bool ScanHostingRecovery()
    {
        HostingJournalScan scan;
        lock (_writeGate)
        {
            scan = ScanJournalFiles();
            if (scan.Status == HostingJournalScanStatus.None) return false;

            _recoveryRequired = true;
            _hostingEnabled = false;
            _recoveryMessage = scan.Message;
        }

        HostingStateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public HostingJournalScan GetHostingJournalScan()
    {
        lock (_writeGate)
        {
            return ScanJournalFiles();
        }
    }

    public IReadOnlyList<HostingRecoveryInfo> GetHostingRecoveryInfo()
    {
        lock (_writeGate)
        {
            var scan = ScanJournalFiles();
            if (scan.Status == HostingJournalScanStatus.None) return [];

            if (scan.Records.Count == 0)
            {
                return scan.Paths
                    .Select(path => new HostingRecoveryInfo(
                        path, "unknown", "unreadable", _options.LiveSaveDirectory,
                        null, null, null, DateTimeOffset.UtcNow))
                    .ToList();
            }

            var infos = scan.Records
                .Select(record => new HostingRecoveryInfo(
                    Path.Combine(JournalRoot, record.Id + ".json"),
                    record.Operation,
                    record.Phase,
                    record.LivePath,
                    record.StagingPath,
                    record.RollbackPath,
                    record.BackupPath,
                    record.StartedAt))
                .ToList();

            var known = infos.Select(info => info.JournalPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            infos.AddRange(scan.Paths
                .Where(path => !known.Contains(path))
                .Select(path => new HostingRecoveryInfo(
                    path, "unknown", "unreadable", _options.LiveSaveDirectory,
                    null, null, null, DateTimeOffset.UtcNow)));
            return infos;
        }
    }

    public HostingLiveInfo DescribeLive()
    {
        lock (_writeGate)
        {
            return DescribeLiveLocked();
        }
    }

    public HostingEnablePreview GetHostingEnablePreview(GameInstallation installation)
    {
        lock (_writeGate)
        {
            var live = DescribeLiveLocked();
            var (entry, groups) = Reconcile(installation, persist: false);
            var selected = SelectGroup(groups, entry?.NextLaunchGroupId);
            return new HostingEnablePreview(
                live,
                groups,
                selected?.Id,
                ResolveImportedGroupName(installation, null));
        }
    }

    public Task<HostingOperationResult> PrepareDisableHostingAsync(CancellationToken cancellationToken = default) =>
        Task.Run(PrepareDisableHosting, cancellationToken);

    public HostingOperationResult PrepareDisableHosting()
    {
        lock (_writeGate)
        {
            var gate = HostingOperationGate();
            if (gate is not null) return gate;

            if (!_hostingEnabled)
                return new HostingOperationResult(
                    HostingOperationState.AlreadyDisabled, "存档托管已经是关闭状态。");

            var probe = ProbeAnyRunning();
            if (probe == GameRunningState.Running)
                return new HostingOperationResult(
                    HostingOperationState.RunningLocked, "游戏正在运行；请先关闭游戏再关闭存档托管。");
            if (probe != GameRunningState.NotRunning)
                return new HostingOperationResult(
                    HostingOperationState.RunningLocked,
                    "无法确认游戏是否正在运行（进程信息不可读）；为避免损坏存档，已停止关闭托管。请关闭游戏后重试。");

            var live = _options.LiveSaveDirectory;
            var info = DescribeLiveLocked();
            try
            {
                switch (info.Kind)
                {
                    case HostingLiveKind.Unknown:
                        return new HostingOperationResult(
                            HostingOperationState.Failed,
                            $"无法确认存档目录 {live} 的当前状态；未改动任何文件，也未关闭托管。");

                    case HostingLiveKind.MkGroupLink:
                    case HostingLiveKind.MkProfileLink:
                        return PrepareDisableCopy(info);

                    default:
                    {
                        if (info.Kind == HostingLiveKind.DanglingLink
                            && !_options.Linker.RemoveJunction(live, out var danglingError))
                        {
                            return new HostingOperationResult(
                                HostingOperationState.Failed,
                                $"无法移除指向不存在目标的存档链接：{danglingError}；未关闭托管。");
                        }

                        var message = info.Kind switch
                        {
                            HostingLiveKind.ExternalLink =>
                                "已关闭托管；游戏存档目录指向 MK 之外的位置，该链接未被改动。",
                            HostingLiveKind.DanglingLink =>
                                "已关闭托管；已移除指向不存在目标的存档链接。",
                            _ => "已关闭托管；游戏会开始使用自己的存档目录。"
                        };

                        _pendingHosting = new PendingHostingChange
                        {
                            TargetEnabled = false,
                            FinalState = HostingOperationState.Disabled,
                            SuccessMessage = message,
                            LiveDirectory = live,
                            FileCount = info.FileCount
                        };
                        _hostingTransition = true;
                        return new HostingOperationResult(
                            HostingOperationState.Prepared, message,
                            LiveDirectory: live, FileCount: info.FileCount);
                    }
                }
            }
            catch (Exception exception)
            {
                _options.Logger?.Warn($"关闭存档托管失败：{exception.Message}");
                return new HostingOperationResult(
                    HostingOperationState.Failed,
                    $"关闭托管失败，未改动存档：{exception.Message}");
            }
        }
    }

    public Task<HostingOperationResult> PrepareEnableHostingAsync(
        GameInstallation installation,
        HostingEnableRequest? request = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => PrepareEnableHosting(installation, request), cancellationToken);

    public HostingOperationResult PrepareEnableHosting(
        GameInstallation installation,
        HostingEnableRequest? request = null)
    {
        lock (_writeGate)
        {
            var gate = HostingOperationGate();
            if (gate is not null) return gate;

            if (_hostingEnabled)
                return new HostingOperationResult(
                    HostingOperationState.AlreadyEnabled, "存档托管已经是开启状态。");

            var probe = ProbeAnyRunning();
            if (probe == GameRunningState.Running)
                return new HostingOperationResult(
                    HostingOperationState.RunningLocked, "游戏正在运行；请先关闭游戏再开启存档托管。");
            if (probe != GameRunningState.NotRunning)
                return new HostingOperationResult(
                    HostingOperationState.RunningLocked,
                    "无法确认游戏是否正在运行（进程信息不可读）；为避免损坏存档，已停止开启托管。请关闭游戏后重试。");

            var live = _options.LiveSaveDirectory;
            var info = DescribeLiveLocked();
            try
            {
                return info.Kind switch
                {
                    HostingLiveKind.Unknown => new HostingOperationResult(
                        HostingOperationState.Failed,
                        $"无法确认存档目录 {live} 的当前状态；未开启托管，也未改动任何文件。"),
                    HostingLiveKind.ExternalLink => new HostingOperationResult(
                        HostingOperationState.Failed,
                        "游戏存档目录指向 MK 之外的位置，未开启托管。请先将其恢复为普通目录后重试。"),
                    HostingLiveKind.MkGroupLink => PrepareEnableFromExistingLink(installation, info),
                    HostingLiveKind.MkProfileLink => PrepareEnableFromExistingLink(installation, info),
                    HostingLiveKind.DanglingLink => PrepareEnableOverDangling(installation, live),
                    HostingLiveKind.Missing or HostingLiveKind.EmptyDirectory =>
                        PrepareEnableEmpty(installation, info),
                    _ => request is null
                        ? NeedsEnableChoice(installation, info, live)
                        : request.Choice == HostingEnableChoice.ImportIntoNewGroup
                            ? PrepareEnableImport(installation, info, request)
                            : PrepareEnableOverwrite(installation, info, request)
                };
            }
            catch (Exception exception)
            {
                _options.Logger?.Warn($"开启存档托管失败：{exception.Message}");
                return new HostingOperationResult(
                    HostingOperationState.Failed,
                    $"开启托管失败，未改动存档：{exception.Message}");
            }
        }
    }

    /// <summary>Publishes or rolls back the prepared operation. Called by the
    /// host after it has persisted (or failed to persist) the switch.</summary>
    public HostingOperationResult CompleteHostingChange(bool persisted, bool enabled)
    {
        PendingHostingChange? pending;
        lock (_writeGate)
        {
            pending = _pendingHosting;
            _pendingHosting = null;
            if (pending is null)
                return new HostingOperationResult(
                    HostingOperationState.Failed, "没有待完成的存档托管切换。");

            if (pending.TargetEnabled != enabled)
            {
                _hostingTransition = false;
                return new HostingOperationResult(
                    HostingOperationState.Failed, "托管切换的期望状态不一致；未发布新状态。");
            }
        }

        var change = pending!;

        if (persisted)
        {
            lock (_writeGate)
            {
                _hostingEnabled = enabled;
                _recoveryRequired = false;
                _recoveryMessage = string.Empty;
                DeleteJournal(change.JournalPath);
            }

            // Commit-time cleanup (e.g. keeping the overwritten group) can be
            // slow; never do it while holding the write lock or before raising
            // state-changed to subscribers. Keep the transition gate closed so
            // a concurrent Reconcile cannot mistake the private rollback
            // directory for a real group mid-move.
            try
            {
                change.OnCommitted?.Invoke();
            }
            catch (Exception exception)
            {
                _options.Logger?.Warn($"托管切换后的清理失败（不影响已完成的切换）：{exception.Message}");
            }
            finally
            {
                lock (_writeGate)
                {
                    _hostingTransition = false;
                }
            }

            var message = change.SuccessMessageFactory?.Invoke() ?? change.SuccessMessage;
            HostingStateChanged?.Invoke(this, EventArgs.Empty);
            return new HostingOperationResult(
                change.FinalState,
                message,
                change.Group,
                change.Groups,
                change.LiveDirectory,
                change.FileCount);
        }

        // Roll back outside the lock. _hostingTransition stays true so normal
        // save-group writes cannot slip in while files move back.
        var undo = change.Undo?.Invoke() ?? RollbackResult.Ok("未改动文件");
        HostingOperationResult result;
        lock (_writeGate)
        {
            _hostingTransition = false;
            if (undo.Complete)
            {
                DeleteJournal(change.JournalPath);
                if (change.RestoreRecoveryMessage is not null)
                {
                    _hostingEnabled = false;
                    _recoveryRequired = true;
                    _recoveryMessage = change.RestoreRecoveryMessage;
                }

                result = new HostingOperationResult(
                    HostingOperationState.Failed,
                    $"未能保存托管开关；已恢复到操作前的状态。{undo.Description}");
            }
            else
            {
                _recoveryRequired = true;
                _hostingEnabled = false;
                _recoveryMessage =
                    $"未能保存托管开关，且自动恢复失败（{undo.Description}）。" +
                    $"请检查 {JournalRoot} 与存档目录后再操作。";
                result = new HostingOperationResult(
                    HostingOperationState.FailedUnknown, _recoveryMessage);
            }
        }

        HostingStateChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    /// <summary>User explicitly restores hosting while the link still points at
    /// a valid MK group of this installation. No files are moved.</summary>
    public HostingOperationResult AcceptExistingMkLink(
        GameInstallation installation,
        bool allowRecovery = false)
    {
        lock (_writeGate)
        {
            var recoveryMessage = _recoveryMessage;
            if (_recoveryRequired && !allowRecovery) return PendingRecoveryResult();

            if (allowRecovery)
            {
                var probe = ProbeAnyRunning();
                if (probe == GameRunningState.Running)
                    return new HostingOperationResult(
                        HostingOperationState.RunningLocked,
                        "游戏正在运行；请先关闭游戏再恢复。");
                if (probe != GameRunningState.NotRunning)
                    return new HostingOperationResult(
                        HostingOperationState.RunningLocked,
                        "无法确认游戏是否正在运行（进程信息不可读）；未改动任何文件。");
            }

            if (_pendingHosting is not null || _hostingTransition)
                return BusyResult();

            if (_hostingEnabled)
                return new HostingOperationResult(
                    HostingOperationState.AlreadyEnabled, "存档托管已经是开启状态。");

            var info = DescribeLiveLocked();
            if (info.Kind == HostingLiveKind.MkProfileLink)
            {
                var legacyKey = GetInstallationKey(installation);
                if (info.LinkTarget is null
                    || !TargetPathMatchesInstallation(info.LinkTarget, legacyKey))
                    return new HostingOperationResult(
                        HostingOperationState.Failed,
                        "当前存档链接属于另一个实例的旧 profile；未开启托管。");

                // A pre-group single-profile junction: enabling hosting lets
                // the next Initialize migrate it; no selection exists yet.
                _pendingHosting = new PendingHostingChange
                {
                    TargetEnabled = true,
                    FinalState = HostingOperationState.Enabled,
                    SuccessMessage = "已开启托管；下次启动会迁移现有存档。",
                    Groups = GetGroups(installation),
                    LiveDirectory = _options.LiveSaveDirectory,
                    FileCount = info.FileCount,
                    RestoreRecoveryMessage = allowRecovery ? recoveryMessage : null
                };
                _hostingTransition = true;
                return new HostingOperationResult(
                    HostingOperationState.Prepared,
                    _pendingHosting.SuccessMessage,
                    Groups: _pendingHosting.Groups,
                    LiveDirectory: _options.LiveSaveDirectory,
                    FileCount: info.FileCount);
            }

            if (info.Kind != HostingLiveKind.MkGroupLink || info.Owner is null)
                return new HostingOperationResult(
                    HostingOperationState.Failed, "当前存档目录没有指向可确认的 MK 存档组，未开启托管。");

            var key = GetInstallationKey(installation);
            if (!string.Equals(info.Owner.InstallationKey, key, StringComparison.OrdinalIgnoreCase))
                return new HostingOperationResult(
                    HostingOperationState.Failed,
                    $"当前存档链接属于另一个实例（{info.Owner.GameDirectory}），未开启托管。");

            var previous = GetNextLaunchGroupId(installation);
            if (!TryPersistSelection(installation, info.Owner.Group.Id, out var error))
                return new HostingOperationResult(
                    HostingOperationState.Failed, $"恢复托管失败：{error}");
            if (GetNextLaunchGroupId(installation) != info.Owner.Group.Id)
            {
                RestoreSelection(installation, previous);
                return new HostingOperationResult(
                    HostingOperationState.Failed,
                    "恢复托管失败：选定组读回与目标组不一致。");
            }

            _pendingHosting = new PendingHostingChange
            {
                TargetEnabled = true,
                FinalState = HostingOperationState.Enabled,
                SuccessMessage = $"已恢复存档托管；继续使用「{info.Owner.Group.Name}」。",
                Group = info.Owner.Group,
                Groups = GetGroups(installation),
                LiveDirectory = _options.LiveSaveDirectory,
                RestoreRecoveryMessage = allowRecovery ? recoveryMessage : null,
                Undo = () =>
                {
                    var restored = RestoreSelection(installation, previous);
                    return restored
                        ? RollbackResult.Ok("已恢复原来的选定组")
                        : RollbackResult.Failed("未能恢复原来的选定组", "restore-selection");
                }
            };
            _hostingTransition = true;
            return new HostingOperationResult(
                HostingOperationState.Prepared,
                _pendingHosting.SuccessMessage,
                Group: info.Owner.Group,
                Groups: _pendingHosting.Groups,
                LiveDirectory: _options.LiveSaveDirectory);
        }
    }

    // ---------------------------------------------------------------------
    // Prepare implementations
    // ---------------------------------------------------------------------

    private HostingOperationResult PrepareDisableCopy(HostingLiveInfo info)
    {
        var owner = info.Owner;
        var target = owner?.Group.DirectoryPath ?? info.LinkTarget
            ?? throw new InvalidOperationException("缺少存档链接目标。");
        var installation = owner is null ? null : FindInstallation(owner.InstallationKey);
        var live = _options.LiveSaveDirectory;
        var installationKey = owner?.InstallationKey
                              ?? (PathEquals(Path.GetDirectoryName(target) ?? string.Empty, ProfilesRoot)
                                  ? Path.GetFileName(target)
                                  : string.Empty);
        var staging = Path.Combine(
            Path.GetDirectoryName(live) ?? live,
            $".mk-disable-{Guid.NewGuid():N}");
        var journal = CreateJournal(
            "disable", installationKey, owner?.Group.Id,
            live, info.Kind.ToString(), target, live, staging, null, null, target);
        var liveRemoved = false;
        var installed = false;
        TreeSnapshot? snapshot = null;

        try
        {
            snapshot = CaptureTree(live, rejectNestedLinks: true);
            UpdateJournal(journal, "copy");
            CopyTree(snapshot, live, staging);
            VerifyTree(snapshot, staging);

            UpdateJournal(journal, "verify-source", ToJournalTree(snapshot));
            UpdateJournal(journal, record =>
            {
                record.PreviousSelectionGroupId = installation is null
                    ? null
                    : GetNextLaunchGroupId(installation);
            });
            if (!TreesEqual(snapshot, CaptureTree(live, rejectNestedLinks: true)))
                throw new IOException("复制期间源存档发生变化；未关闭托管。");

            if (ProbeAnyRunning() != GameRunningState.NotRunning)
                throw new IOException("复制期间检测到游戏正在运行或状态未知；未关闭托管。");

            UpdateJournal(journal, "remove-link");
            if (!_options.Linker.RemoveJunction(live, out var removeError))
                throw new IOException($"无法移除当前存档链接：{removeError}");
            liveRemoved = true;

            UpdateJournal(journal, "install-live");
            Directory.Move(staging, live);
            installed = true;
            _options.HostingFaultHook?.Invoke("disable-after-install");

            if (!TreesEqual(snapshot, CaptureTree(live, rejectNestedLinks: false)))
                throw new IOException("安装后的游戏存档目录与复制内容不一致。");

            UpdateJournal(journal, "prepared");
            _pendingHosting = new PendingHostingChange
            {
                TargetEnabled = false,
                FinalState = HostingOperationState.Disabled,
                SuccessMessage = "已关闭托管，存档已放回游戏目录。",
                Groups = installation is null ? [] : GetGroups(installation),
                LiveDirectory = live,
                FileCount = snapshot.Files.Count,
                JournalPath = journal,
                Undo = () => RestoreJunctionAfterDisable(live, target, snapshot)
            };
            _hostingTransition = true;
            return new HostingOperationResult(
                HostingOperationState.Prepared,
                _pendingHosting.SuccessMessage,
                Groups: _pendingHosting.Groups,
                LiveDirectory: live,
                FileCount: snapshot.Files.Count);
        }
        catch (Exception exception)
        {
            _options.Logger?.Warn($"关闭托管时复制存档失败：{exception.Message}");
            var failedSteps = new List<string>();

            // A real copy may already be installed. It may only be removed when
            // it is still exactly the verified snapshot; any new or changed
            // content is the only copy and must be preserved.
            if (installed && Directory.Exists(live) && !IsReparsePoint(live))
            {
                if (snapshot is null)
                {
                    failedSteps.Add("缺少关闭前的树快照，不能证明 live 可删除");
                }
                else
                {
                    try
                    {
                        if (!TreesEqual(snapshot, CaptureTree(live, rejectNestedLinks: false)))
                            failedSteps.Add("live 已出现新增或变化内容，拒绝删除");
                        else
                            Directory.Delete(live, recursive: true);
                    }
                    catch (Exception cleanupError)
                    {
                        failedSteps.Add($"清理 live 副本失败：{cleanupError.Message}");
                    }
                }
            }

            if (liveRemoved && !installed && Directory.Exists(live)
                && (!IsReparsePoint(live)
                    || !_options.Linker.TryGetTarget(live, out var existingTarget)
                    || !PathEquals(existingTarget, target)))
            {
                failedSteps.Add("live 已被意外替换，未删除新现场");
            }

            if ((installed || liveRemoved) && !Directory.Exists(live))
            {
                if (!_options.Linker.CreateJunction(live, target, out var restoreError))
                {
                    failedSteps.Add($"重建原存档链接失败：{restoreError}");
                }
                else if (!_options.Linker.TryGetTarget(live, out var readBack)
                         || !PathEquals(readBack, target))
                {
                    failedSteps.Add("重建链接后读回目标不一致");
                }
            }

            if (failedSteps.Count == 0
                && (!_options.Linker.TryGetTarget(live, out var finalTarget)
                    || !PathEquals(finalTarget, target)))
            {
                failedSteps.Add("操作结束后 live 不是原存档链接");
            }

            if (failedSteps.Count > 0)
            {
                _hostingTransition = false;
                MarkHostingPendingRecovery(
                    $"关闭托管中断，恢复未完成：{string.Join("；", failedSteps)}。" +
                    $"请检查 {live}、{target}、{JournalRoot}；未删除事务记录。");
                return new HostingOperationResult(
                    HostingOperationState.FailedUnknown,
                    $"关闭托管失败：{exception.Message}；{string.Join("；", failedSteps)}。已保留现场。");
            }

            if (!installed) DeleteDirectoryQuietly(staging);
            DeleteJournal(journal);
            _hostingTransition = false;
            return new HostingOperationResult(
                HostingOperationState.Failed,
                $"关闭托管失败，已恢复托管开启：{exception.Message}");
        }
    }

    private RollbackResult RestoreJunctionAfterDisable(
        string live,
        string target,
        TreeSnapshot? expected)
    {
        try
        {
            if (Directory.Exists(live) && !IsReparsePoint(live))
            {
                if (expected is null
                    || !TreesEqual(expected, CaptureTree(live, rejectNestedLinks: false)))
                {
                    return RollbackResult.Failed(
                        "live 已出现新增或变化内容，拒绝删除",
                        "live-changed");
                }

                Directory.Delete(live, recursive: true);
            }

            if (!_options.Linker.CreateJunction(live, target, out var linkError))
                return RollbackResult.Failed($"未能恢复原存档链接：{linkError}", "restore-live-link");
            if (!_options.Linker.TryGetTarget(live, out var readBack)
                || !PathEquals(readBack, target))
                return RollbackResult.Failed("重建链接后读回目标不一致", "readback");
            return RollbackResult.Ok("已恢复原存档链接");
        }
        catch (Exception exception)
        {
            return RollbackResult.Failed($"未能恢复原存档链接：{exception.Message}", "restore-live-link");
        }
    }

    private HostingOperationResult PrepareEnableFromExistingLink(
        GameInstallation installation,
        HostingLiveInfo info)
    {
        if (info.Owner is null)
        {
            if (info.Kind != HostingLiveKind.MkProfileLink)
                return new HostingOperationResult(
                    HostingOperationState.Failed, "无法确认当前存档链接指向的存档组；未开启托管。");

            // The legacy single-profile layout: MK owns the data, but there is
            // no group selection yet. Initialize will migrate it.
            _pendingHosting = new PendingHostingChange
            {
                TargetEnabled = true,
                FinalState = HostingOperationState.Enabled,
                SuccessMessage = "已开启托管；下次启动会迁移现有存档。",
                Groups = GetGroups(installation),
                LiveDirectory = _options.LiveSaveDirectory,
                FileCount = info.FileCount
            };
            _hostingTransition = true;
            return new HostingOperationResult(
                HostingOperationState.Prepared,
                _pendingHosting.SuccessMessage,
                Groups: _pendingHosting.Groups,
                LiveDirectory: _options.LiveSaveDirectory,
                FileCount: info.FileCount);
        }

        var key = GetInstallationKey(installation);
        if (!string.Equals(info.Owner.InstallationKey, key, StringComparison.OrdinalIgnoreCase))
            return new HostingOperationResult(
                HostingOperationState.Failed,
                $"当前存档链接属于另一个实例（{info.Owner.GameDirectory}）；未开启托管。");

        var previous = GetNextLaunchGroupId(installation);
        if (!TryPersistSelection(installation, info.Owner.Group.Id, out var error))
            return new HostingOperationResult(
                HostingOperationState.Failed, $"开启托管失败：{error}");

        _pendingHosting = new PendingHostingChange
        {
            TargetEnabled = true,
            FinalState = HostingOperationState.Enabled,
            SuccessMessage = $"已开启托管；游戏会继续使用「{info.Owner.Group.Name}」。",
            Group = info.Owner.Group,
            Groups = GetGroups(installation),
            LiveDirectory = _options.LiveSaveDirectory,
            FileCount = info.FileCount,
            Undo = () =>
            {
                var restored = RestoreSelection(installation, previous);
                return restored
                    ? RollbackResult.Ok("已恢复原来的选定组")
                    : RollbackResult.Failed("未能恢复原来的选定组", "restore-selection");
            }
        };
        _hostingTransition = true;
        return new HostingOperationResult(
            HostingOperationState.Prepared,
            _pendingHosting.SuccessMessage,
            Group: info.Owner.Group,
            Groups: _pendingHosting.Groups,
            LiveDirectory: _options.LiveSaveDirectory,
            FileCount: info.FileCount);
    }

    private HostingOperationResult NeedsEnableChoice(
        GameInstallation installation,
        HostingLiveInfo info,
        string live)
    {
        var (_, groups) = Reconcile(installation, persist: true);
        return new HostingOperationResult(
            HostingOperationState.NeedsChoice,
            "检测到游戏目录里有未托管的存档；请选择导入为新组，或覆盖已有组。取消不会修改任何文件。",
            Groups: groups,
            LiveDirectory: live,
            FileCount: info.FileCount);
    }

    private HostingOperationResult PrepareEnableOverDangling(
        GameInstallation installation,
        string live)
    {
        if (!_options.Linker.RemoveJunction(live, out var removeError))
            return new HostingOperationResult(
                HostingOperationState.Failed,
                $"无法移除指向不存在目标的存档链接：{removeError}；未开启托管。");

        return PrepareEnableEmpty(
            installation,
            new HostingLiveInfo(HostingLiveKind.Missing, live, 0));
    }

    private HostingOperationResult PrepareEnableEmpty(GameInstallation installation, HostingLiveInfo info)
    {
        var live = _options.LiveSaveDirectory;
        var wasMissing = info.Kind == HostingLiveKind.Missing;
        var previous = GetNextLaunchGroupId(installation);
        var staging = NewStagingDirectory(out var groupId);
        SaveGroup? group = null;
        var journal = CreateJournal(
            "enable-empty", GetInstallationKey(installation), null, live,
            info.Kind.ToString(), info.LinkTarget, live, staging, null, null, null);

        try
        {
            group = CommitGroup(
                installation, staging, groupId, "默认", isDefault: true, _options.UtcNow());
            UpdateJournal(journal, record =>
            {
                record.GroupId = group.Id;
                record.TargetGroupPath = group.DirectoryPath;
                record.PreviousSelectionGroupId = previous;
                record.Phase = "link";
                record.Tree = ToJournalTree(CaptureTree(group.DirectoryPath, rejectNestedLinks: true));
            });

            var switched = SwitchLiveLink(group, live);
            if (!switched.Success)
            {
                if (!switched.PreviousStateIntact)
                {
                    // The link state is unknown; keep the journal so startup
                    // blocks instead of silently adopting a half-switch.
                    MarkHostingPendingRecovery(
                        $"开启托管时切换存档链接后状态未知：{switched.Message}。请检查 {live}。");
                    return new HostingOperationResult(
                        HostingOperationState.FailedUnknown,
                        $"开启托管失败：{switched.Message}");
                }

                DeleteDirectoryQuietly(staging);
                if (CanDiscardGroup(live, group))
                    RollbackGroup(installation, group);
                DeleteJournal(journal);
                return new HostingOperationResult(
                    HostingOperationState.Failed, $"开启托管失败：{switched.Message}");
            }

            if (!TryPersistSelection(installation, group.Id, out var error))
            {
                var undo = UndoEmptyEnable(live, group, previous, wasMissing);
                if (!undo.Complete)
                    MarkHostingPendingRecovery(
                        $"开启托管后未能保存选择，且回滚失败：{undo.Description}。请检查 {live}。");
                else
                    DeleteJournal(journal);
                return new HostingOperationResult(
                    undo.Complete ? HostingOperationState.Failed : HostingOperationState.FailedUnknown,
                    undo.Complete
                        ? $"开启托管失败：{error}；已恢复操作前的存档状态。"
                        : $"开启托管失败：{error}；且未能恢复原状态（{undo.Description}），请手动检查 {live}。");
            }

            UpdateJournal(journal, "prepared");
            _pendingHosting = new PendingHostingChange
            {
                TargetEnabled = true,
                FinalState = HostingOperationState.Enabled,
                SuccessMessage = "已开启托管，并创建了「默认」存档组。",
                Group = group,
                Groups = GetGroups(installation),
                LiveDirectory = live,
                JournalPath = journal,
                Undo = () => UndoEmptyEnable(live, group, previous, wasMissing)
            };
            _hostingTransition = true;
            return new HostingOperationResult(
                HostingOperationState.Prepared,
                _pendingHosting.SuccessMessage,
                Group: group,
                Groups: _pendingHosting.Groups,
                LiveDirectory: live);
        }
        catch (Exception exception)
        {
            DeleteDirectoryQuietly(staging);
            if (group is not null)
            {
                var undo = UndoEmptyEnable(live, group, previous, wasMissing);
                if (undo.Complete)
                {
                    DeleteJournal(journal);
                }
                else
                {
                    MarkHostingPendingRecovery(
                        $"开启托管失败且未能回滚：{undo.Description}。请检查 {live}。");
                }
            }
            else
            {
                DeleteJournal(journal);
            }
            _options.Logger?.Warn($"开启托管（空目录）失败：{exception.Message}");
            return new HostingOperationResult(
                group is not null && _recoveryRequired
                    ? HostingOperationState.FailedUnknown
                    : HostingOperationState.Failed,
                group is not null && _recoveryRequired
                    ? $"开启托管失败：{exception.Message}；未能完整回滚，请检查后重试。"
                    : $"开启托管失败，未改动存档：{exception.Message}");
        }
    }

    private RollbackResult UndoEmptyEnable(
        string live,
        SaveGroup group,
        Guid? previousSelection,
        bool wasMissing)
    {
        var failed = new List<string>();
        try
        {
            if (_options.Linker.Exists(live)
                && _options.Linker.TryGetTarget(live, out var current)
                && PathEquals(current, group.DirectoryPath)
                && !_options.Linker.RemoveJunction(live, out var removeError))
            {
                failed.Add($"移除新链接失败：{removeError}");
            }

            if (wasMissing)
            {
                if (Directory.Exists(live)
                    && !IsReparsePoint(live)
                    && !Directory.EnumerateFileSystemEntries(live).Any())
                {
                    Directory.Delete(live, recursive: false);
                }
            }
            else
            {
                TryRecreateEmptyDirectory(live);
                if (!Directory.Exists(live))
                    failed.Add("恢复空目录失败");
            }

            var installation = FindInstallationForGroup(group);
            if (installation is null)
            {
                failed.Add("找不到目标组所属实例");
            }
            else
            {
                if (CanDiscardGroup(live, group)) RollbackGroup(installation, group);
                if (!RestoreSelection(installation, previousSelection))
                    failed.Add("恢复选定组失败");
            }

            return failed.Count == 0
                ? RollbackResult.Ok("已恢复操作前的状态")
                : RollbackResult.Failed(
                    $"未完整恢复：{string.Join("；", failed)}", failed.ToArray());
        }
        catch (Exception exception)
        {
            return RollbackResult.Failed(
                $"恢复操作前状态失败：{exception.Message}", "undo-empty-enable");
        }
    }

    private HostingOperationResult PrepareEnableImport(
        GameInstallation installation,
        HostingLiveInfo info,
        HostingEnableRequest request)
    {
        var name = ResolveImportedGroupName(installation, request.NewGroupName);

        var live = _options.LiveSaveDirectory;
        var previous = GetNextLaunchGroupId(installation);
        var staging = NewStagingDirectory(out var groupId);
        var journal = CreateJournal(
            "enable-import", GetInstallationKey(installation), null, live,
            info.Kind.ToString(), info.LinkTarget, live, staging, null, null, null);
        string? backup = null;
        SaveGroup? group = null;

        try
        {
            var snapshot = CaptureTree(live, rejectNestedLinks: true);
            UpdateJournal(journal, "copy");
            CopyTree(snapshot, live, staging);
            VerifyTree(snapshot, staging);

            UpdateJournal(journal, "verify-source", ToJournalTree(snapshot));
            if (!TreesEqual(snapshot, CaptureTree(live, rejectNestedLinks: true)))
                throw new IOException("复制期间源存档发生变化；未开启托管。");

            if (ProbeAnyRunning() != GameRunningState.NotRunning)
                throw new IOException("复制期间检测到游戏正在运行或状态未知；未开启托管。");

            group = CommitGroup(installation, staging, groupId, name, isDefault: false, _options.UtcNow());
            backup = ReserveBackupPath(live);
            UpdateJournal(journal, record =>
            {
                record.GroupId = group.Id;
                record.TargetGroupPath = group.DirectoryPath;
                record.PreviousSelectionGroupId = previous;
                record.BackupPath = backup;
                record.Phase = "backup-live";
            });

            backup = MoveLiveToBackup(live, backup);

            UpdateJournal(journal, "link");
            if (!_options.Linker.CreateJunction(live, group.DirectoryPath, out var linkError))
            {
                var restore = RestoreBackupWithFallback(backup, live);
                if (!restore.Success)
                {
                    MarkHostingPendingRecovery(
                        $"开启托管时创建存档链接失败（{linkError}），且原目录未能恢复（{restore.Error}）。请检查 {live} 与 {backup}。");
                    return new HostingOperationResult(
                        HostingOperationState.FailedUnknown,
                        $"开启托管失败：{linkError}；原目录未能恢复（{restore.Error}），仍在 {backup}，请手动处理。",
                        group);
                }

                DeleteJournal(journal);
                return new HostingOperationResult(
                    HostingOperationState.Failed,
                    $"开启托管失败：{linkError}；原存档目录已恢复，导入的副本「{group.Name}」保留在 {group.DirectoryPath}。",
                    group);
            }

            if (!TryPersistSelection(installation, group.Id, out var error))
            {
                var undo = UndoImportEnable(live, group, backup, previous);
                if (undo.Complete) DeleteJournal(journal);
                else MarkHostingPendingRecovery(
                    $"开启托管后未能保存选择，且回滚失败：{undo.Description}。请检查 {live}。");
                return new HostingOperationResult(
                    undo.Complete ? HostingOperationState.Failed : HostingOperationState.FailedUnknown,
                    undo.Complete
                        ? $"开启托管失败：{error}；已恢复原存档状态，导入的副本仍保留。"
                        : $"开启托管失败：{error}；且未能恢复原存档状态（{undo.Description}），请手动检查。",
                    group);
            }

            UpdateJournal(journal, "prepared");
            _pendingHosting = new PendingHostingChange
            {
                TargetEnabled = true,
                FinalState = HostingOperationState.Enabled,
                SuccessMessage = $"已开启托管，并导入为「{group.Name}」；原目录备份在 {backup}。",
                Group = group,
                Groups = GetGroups(installation),
                LiveDirectory = live,
                FileCount = snapshot.Files.Count,
                JournalPath = journal,
                Undo = () => UndoImportEnable(live, group, backup, previous)
            };
            _hostingTransition = true;
            return new HostingOperationResult(
                HostingOperationState.Prepared,
                _pendingHosting.SuccessMessage,
                Group: group,
                Groups: _pendingHosting.Groups,
                LiveDirectory: live,
                FileCount: snapshot.Files.Count);
        }
        catch (Exception exception)
        {
            DeleteDirectoryQuietly(staging);
            var undo = group is not null && backup is not null
                ? UndoImportEnable(live, group, backup, previous)
                : RollbackResult.Ok("未改动原目录");
            if (undo.Complete) DeleteJournal(journal);
            else MarkHostingPendingRecovery(
                $"开启托管失败且未能回滚：{undo.Description}。请检查 {live} 与 {backup}。");
            _options.Logger?.Warn($"开启托管（导入）失败：{exception.Message}");
            return new HostingOperationResult(
                HostingOperationState.Failed,
                group is null
                    ? undo.Complete
                        ? $"开启托管失败，未改动原目录：{exception.Message}"
                        : $"开启托管失败：{exception.Message}；原目录仍在 {backup}，请手动检查。"
                    : undo.Complete
                        ? $"开启托管失败：{exception.Message}；已恢复原目录，导入的副本「{group.Name}」保留。"
                        : $"开启托管失败：{exception.Message}；导入的副本保留在 {group.DirectoryPath}，原目录状态需手动检查。",
                group);
        }
    }

    private RollbackResult UndoImportEnable(
        string live,
        SaveGroup group,
        string? backup,
        Guid? previousSelection)
    {
        var failed = new List<string>();
        try
        {
            if (IsReparsePoint(live) || _options.Linker.TryGetTarget(live, out _))
            {
                if (!_options.Linker.RemoveJunction(live, out var removeError))
                    failed.Add($"移除新链接失败：{removeError}");
            }

            if (backup is not null)
            {
                var restored = RestoreBackupWithFallback(backup, live);
                if (!restored.Success)
                    failed.Add($"原存档目录未能恢复：{restored.Error}");
            }

            if (!Directory.Exists(live))
                failed.Add("游戏存档目录仍不存在");

            var installation = FindInstallationForGroup(group);
            if (installation is null)
            {
                failed.Add("找不到导入组所属实例");
            }
            else if (!RestoreSelection(installation, previousSelection))
            {
                failed.Add("恢复选定组失败");
            }

            return failed.Count == 0
                ? RollbackResult.Ok("已恢复原存档目录与选定组；导入的副本保留")
                : RollbackResult.Failed(
                    $"未完整恢复：{string.Join("；", failed)}", failed.ToArray());
        }
        catch (Exception exception)
        {
            return RollbackResult.Failed(
                $"恢复操作前状态失败：{exception.Message}", "undo-import-enable");
        }
    }

    private HostingOperationResult PrepareEnableOverwrite(
        GameInstallation installation,
        HostingLiveInfo info,
        HostingEnableRequest request)
    {
        if (request.TargetGroupId is not { } targetId)
            return new HostingOperationResult(
                HostingOperationState.Failed, "没有选择要覆盖的存档组。");

        var (_, groups) = Reconcile(installation, persist: true);
        var target = groups.FirstOrDefault(group => group.Id == targetId);
        if (target is null)
            return new HostingOperationResult(
                HostingOperationState.Failed, "要覆盖的存档组不存在，或不属于当前游戏实例。");

        if (IsReparsePoint(target.DirectoryPath))
            return new HostingOperationResult(
                HostingOperationState.Failed,
                "要覆盖的存档组根目录是链接，不能把目标组身份写到链接之外；未开启托管。");

        if (!SaveGroupManifestStore.TryRead(target.DirectoryPath, out var originalManifest))
            return new HostingOperationResult(
                HostingOperationState.Failed, $"要覆盖的存档组清单缺失或损坏：{target.DirectoryPath}");

        try
        {
            // The target is moved aside as a whole; a nested junction would
            // make the rollback copy ambiguous. Refuse before touching it.
            _ = CaptureTree(target.DirectoryPath, rejectNestedLinks: true);
        }
        catch (Exception exception)
        {
            return new HostingOperationResult(
                HostingOperationState.Failed,
                $"要覆盖的存档组目录包含链接或无法读取，未开启托管：{exception.Message}");
        }

        var installationKey = GetInstallationKey(installation);
        if (!string.Equals(originalManifest.InstallationKey, installationKey, StringComparison.OrdinalIgnoreCase))
            return new HostingOperationResult(
                HostingOperationState.Failed, "要覆盖的存档组不属于当前游戏实例。");

        var live = _options.LiveSaveDirectory;
        if (IsUnder(target.DirectoryPath, live) || IsUnder(live, target.DirectoryPath))
            return new HostingOperationResult(
                HostingOperationState.Failed, "目标存档组与游戏存档目录重叠；未开启托管。");

        var parent = Path.GetDirectoryName(target.DirectoryPath)!;
        var staging = Path.Combine(parent, $".mk-overwrite-{Guid.NewGuid():N}");
        var rollback = Path.Combine(parent, $".mk-old-{Guid.NewGuid():N}");
        var previous = GetNextLaunchGroupId(installation);
        var journal = CreateJournal(
            "enable-overwrite", installationKey, target.Id, live,
            info.Kind.ToString(), info.LinkTarget, live, staging, rollback, null, target.DirectoryPath);
        string? backup = null;
        var targetReplaced = false;
        var liveMoved = false;
        var linked = false;

        try
        {
            var snapshot = CaptureTree(live, rejectNestedLinks: true);
            UpdateJournal(journal, "copy");
            CopyTree(snapshot, live, staging);
            VerifyTree(snapshot, staging);

            UpdateJournal(journal, "verify-source", ToJournalTree(snapshot));
            UpdateJournal(journal, record => record.PreviousSelectionGroupId = previous);
            if (!TreesEqual(snapshot, CaptureTree(live, rejectNestedLinks: true)))
                throw new IOException("复制期间源存档发生变化；未开启托管。");

            if (ProbeAnyRunning() != GameRunningState.NotRunning)
                throw new IOException("复制期间检测到游戏正在运行或状态未知；未开启托管。");

            var rollbackSnapshot = CaptureTree(target.DirectoryPath, rejectNestedLinks: true);
            UpdateJournal(journal, record =>
            {
                record.Phase = "stage-old-target";
                record.RollbackTree = ToJournalTree(rollbackSnapshot);
            });
            Directory.Move(target.DirectoryPath, rollback);

            UpdateJournal(journal, "install-target");
            Directory.Move(staging, target.DirectoryPath);
            targetReplaced = true;

            // Keep the group's identity: content comes from the live folder,
            // identity comes from the group being overwritten.
            SaveGroupManifestStore.Write(target.DirectoryPath, originalManifest);
            if (!SaveGroupManifestStore.TryRead(target.DirectoryPath, out var afterManifest)
                || afterManifest.GroupId != originalManifest.GroupId
                || afterManifest.Name != originalManifest.Name
                || afterManifest.CreatedAt != originalManifest.CreatedAt
                || afterManifest.InstallationKey != originalManifest.InstallationKey)
                throw new IOException("覆盖后存档组身份校验失败。");

            backup = ReserveBackupPath(live);
            UpdateJournal(journal, record =>
            {
                record.BackupPath = backup;
                record.Phase = "backup-live";
            });
            backup = MoveLiveToBackup(live, backup);
            liveMoved = true;

            UpdateJournal(journal, "link");
            if (!_options.Linker.CreateJunction(live, target.DirectoryPath, out var linkError))
                throw new IOException($"创建存档链接失败：{linkError}");
            linked = true;

            if (!TryPersistSelection(installation, target.Id, out var error))
                throw new IOException($"保存存档索引失败：{error}");

            UpdateJournal(journal, "prepared");
            var backupPath = backup;
            var rollbackPath = rollback;
            string? overwrittenBackup = null;
            _pendingHosting = new PendingHostingChange
            {
                TargetEnabled = true,
                FinalState = HostingOperationState.Enabled,
                SuccessMessage = $"已开启托管，并用游戏目录的存档覆盖了「{target.Name}」。",
                SuccessMessageFactory = () => overwrittenBackup is null
                    ? $"已开启托管，并用游戏目录的存档覆盖了「{target.Name}」。"
                    : $"已开启托管，并用游戏目录的存档覆盖了「{target.Name}」；被覆盖的组备份在 {overwrittenBackup}。",
                Group = target,
                Groups = GetGroups(installation),
                LiveDirectory = live,
                FileCount = snapshot.Files.Count,
                JournalPath = journal,
                Undo = () => TryRestoreOverwriteState(
                    live, target, rollbackPath, backupPath, previous,
                    targetReplaced, linked, liveMoved),
                OnCommitted = () => overwrittenBackup =
                    FinalizeOverwriteRollback(target, rollbackPath, request.BackupTargetGroup)
            };
            _hostingTransition = true;
            return new HostingOperationResult(
                HostingOperationState.Prepared,
                _pendingHosting.SuccessMessage,
                Group: target,
                Groups: _pendingHosting.Groups,
                LiveDirectory: live,
                FileCount: snapshot.Files.Count);
        }
        catch (Exception exception)
        {
            _options.Logger?.Warn($"开启托管（覆盖）失败：{exception.Message}");
            var restored = TryRestoreOverwriteState(
                live, target, rollback, backup, previous, targetReplaced, linked, liveMoved);
            DeleteDirectoryQuietly(staging);
            if (restored.Complete) DeleteJournal(journal);
            else MarkHostingPendingRecovery(
                $"覆盖存档组失败且未能完全恢复：{restored.Description}。请检查 {JournalRoot}。");

            return new HostingOperationResult(
                restored.Complete ? HostingOperationState.Failed : HostingOperationState.FailedUnknown,
                restored.Complete
                    ? $"开启托管失败，已恢复原状态：{exception.Message}"
                    : $"开启托管失败：{exception.Message}；{restored.Description}",
                target);
        }
    }

    private string? FinalizeOverwriteRollback(
        SaveGroup target,
        string rollback,
        bool keepBackup)
    {
        if (!Directory.Exists(rollback)) return null;

        if (!keepBackup)
        {
            DeleteDirectoryQuietly(rollback);
            return null;
        }

        try
        {
            Directory.CreateDirectory(BackupsRoot);
            var stamp = _options.UtcNow().ToString("yyyyMMddHHmmss");
            var destination = Path.Combine(
                BackupsRoot,
                $"{Path.GetFileName(target.DirectoryPath)}-{stamp}-overwritten");
            var suffix = 2;
            while (Directory.Exists(destination))
                destination = Path.Combine(
                    BackupsRoot,
                    $"{Path.GetFileName(target.DirectoryPath)}-{stamp}-overwritten-{suffix++}");

            MoveDirectoryWithFallback(rollback, destination);
            return destination;
        }
        catch (Exception exception)
        {
            _options.Logger?.Warn($"保留被覆盖存档组失败，旧内容仍在 {rollback}：{exception.Message}");
            return rollback;
        }
    }

    private RollbackResult TryRestoreOverwriteState(
        string live,
        SaveGroup target,
        string rollback,
        string? backup,
        Guid? previousSelection,
        bool targetReplaced,
        bool linked,
        bool liveMoved)
    {
        var failed = new List<string>();
        try
        {
            if (linked && _options.Linker.Exists(live)
                && !_options.Linker.RemoveJunction(live, out var removeError))
                failed.Add($"移除新链接失败：{removeError}");

            if (liveMoved && backup is not null)
            {
                var restoredLive = RestoreBackupWithFallback(backup, live);
                if (!restoredLive.Success)
                    failed.Add($"原存档目录未能恢复：{restoredLive.Error}");
            }

            if (targetReplaced && !Directory.Exists(rollback))
            {
                failed.Add("旧目标副本不存在，无法证明目标组已恢复");
            }
            else if (Directory.Exists(rollback))
            {
                try
                {
                    var failedPath = Path.Combine(
                        BackupsRoot,
                        $"{Path.GetFileName(target.DirectoryPath)}-{_options.UtcNow():yyyyMMddHHmmss}-{Guid.NewGuid():N}-failed");
                    Directory.CreateDirectory(BackupsRoot);
                    if (Directory.Exists(target.DirectoryPath))
                        MoveDirectoryWithFallback(target.DirectoryPath, failedPath);
                    MoveDirectoryWithFallback(rollback, target.DirectoryPath);
                }
                catch (Exception exception)
                {
                    failed.Add($"恢复目标组失败：{exception.Message}");
                }
            }

            if (!SaveGroupManifestStore.TryRead(target.DirectoryPath, out var manifest)
                || manifest.GroupId != target.Id)
                failed.Add("目标组清单未恢复为原身份");

            if (!Directory.Exists(live))
                failed.Add("游戏存档目录仍不存在");

            var installation = FindInstallationForGroup(target);
            if (installation is null)
                failed.Add("找不到目标组所属实例");
            else if (!RestoreSelection(installation, previousSelection))
                failed.Add("恢复选定组失败");

            return failed.Count == 0
                ? RollbackResult.Ok("已恢复被覆盖的存档组与原存档目录")
                : RollbackResult.Failed(
                    $"未完整恢复：{string.Join("；", failed)}", failed.ToArray());
        }
        catch (Exception exception)
        {
            return RollbackResult.Failed($"恢复失败：{exception.Message}", "undo-overwrite");
        }
    }

    private HostingOperationResult? HostingOperationGate()
    {
        if (_pendingHosting is not null || _hostingTransition) return BusyResult();
        if (_recoveryRequired) return PendingRecoveryResult();
        return null;
    }

    private HostingOperationResult BusyResult() => new(
        HostingOperationState.Failed, "另一个存档托管操作正在进行；请稍候再试。");

    private HostingOperationResult PendingRecoveryResult() => new(
        HostingOperationState.PendingRecovery,
        string.IsNullOrWhiteSpace(_recoveryMessage)
            ? "存档托管状态待恢复；请先处理后再操作。"
            : _recoveryMessage);

    // ---------------------------------------------------------------------
    // Shared helpers
    // ---------------------------------------------------------------------

    private GameRunningState ProbeAnyRunning()
    {
        try
        {
            return _options.ProbeAnyGameRunningState?.Invoke() ?? GameRunningState.Unknown;
        }
        catch (Exception exception)
        {
            _options.Logger?.Warn($"读取游戏运行状态失败：{exception.Message}");
            return GameRunningState.Unknown;
        }
    }

    private string? HostingWriteBlock()
    {
        switch (ComputeHostingStateLocked())
        {
            case SaveHostingState.Transition:
                return "正在切换存档托管状态；请稍候再操作。";
            case SaveHostingState.PendingRecovery:
                return string.IsNullOrWhiteSpace(_recoveryMessage)
                    ? "存档托管状态待恢复；请先在存档管理中处理。"
                    : _recoveryMessage;
            case SaveHostingState.Disabled:
                return "托管已关闭；请先在存档管理中重新开启托管。";
            default:
                return null;
        }
    }

    private HostingLiveInfo DescribeLiveLocked()
    {
        var live = _options.LiveSaveDirectory;
        try
        {
            if (!_options.Linker.Exists(live))
                return new HostingLiveInfo(HostingLiveKind.Missing, live, 0);

            if (_options.Linker.TryGetTarget(live, out var target))
            {
                var full = Path.GetFullPath(target);
                if (!Directory.Exists(full))
                    return new HostingLiveInfo(HostingLiveKind.DanglingLink, live, 0, LinkTarget: full);
                if (!IsUnder(full, ProfilesRoot))
                    return new HostingLiveInfo(
                        HostingLiveKind.ExternalLink, live, CountDataFilesQuietly(full), LinkTarget: full);

                var owner = FindOwnerByPath(full);
                if (owner is null)
                {
                    // Legacy single-profile junction (ProfilesRoot/<key>), not
                    // a group yet. It is still MK-owned; the profiles root
                    // itself is not.
                    var parent = Path.GetDirectoryName(full);
                    if (!PathEquals(full, ProfilesRoot)
                        && parent is not null
                        && PathEquals(parent, ProfilesRoot))
                    {
                        return new HostingLiveInfo(
                            HostingLiveKind.MkProfileLink, live,
                            CountDataFilesQuietly(full), LinkTarget: full);
                    }

                    return new HostingLiveInfo(HostingLiveKind.Unknown, live, 0, LinkTarget: full);
                }

                return new HostingLiveInfo(
                    HostingLiveKind.MkGroupLink, live, CountDataFilesQuietly(full), owner, full);
            }

            if (IsReparsePoint(live))
                return new HostingLiveInfo(HostingLiveKind.Unknown, live, 0);

            var entries = Directory.EnumerateFileSystemEntries(live).ToList();
            return entries.Count == 0
                ? new HostingLiveInfo(HostingLiveKind.EmptyDirectory, live, 0)
                : new HostingLiveInfo(HostingLiveKind.Data, live, CountDataFilesQuietly(live));
        }
        catch (Exception exception)
        {
            _options.Logger?.Warn($"读取存档目录状态失败：{exception.Message}");
            return new HostingLiveInfo(HostingLiveKind.Unknown, live, 0);
        }
    }

    private SaveGroupOwner? FindOwnerByPath(string fullPath)
    {
        foreach (var entry in _catalog.Load().Installations)
        {
            try
            {
                var group = _scanner.Scan(entry.InstallationKey)
                    .FirstOrDefault(candidate => PathEquals(candidate.DirectoryPath, fullPath));
                if (group is not null)
                    return new SaveGroupOwner(entry.InstallationKey, group, entry.GameDirectory);
            }
            catch (Exception exception)
            {
                _options.Logger?.Warn($"扫描存档组失败：{exception.Message}");
            }
        }

        // The catalog is rebuildable and may be missing while the live link and
        // group manifests are intact. Fall back to scanning the profiles root
        // so a lost index does not turn a valid MK link into “unknown”.
        try
        {
            if (!Directory.Exists(ProfilesRoot)) return null;
            foreach (var installationDirectory in Directory.EnumerateDirectories(ProfilesRoot))
            {
                var key = Path.GetFileName(installationDirectory);
                var group = _scanner.Scan(key)
                    .FirstOrDefault(candidate => PathEquals(candidate.DirectoryPath, fullPath));
                if (group is not null)
                    return new SaveGroupOwner(key, group, string.Empty);
            }
        }
        catch (Exception exception)
        {
            _options.Logger?.Warn($"扫描存档组根目录失败：{exception.Message}");
        }

        return null;
    }

    private int CountDataFilesQuietly(string directory)
    {
        try
        {
            return CollectDataFiles(directory).Count;
        }
        catch
        {
            return 0;
        }
    }

    private bool RestoreSelection(GameInstallation installation, Guid? previousSelection)
    {
        try
        {
            var catalog = _catalog.Load();
            var entry = FindCatalogEntry(catalog, installation);
            if (entry is null) return false;
            entry.NextLaunchGroupId = previousSelection;
            _catalog.Save(catalog);
            return true;
        }
        catch (Exception exception)
        {
            _options.Logger?.Warn($"恢复存档组选择失败：{exception.Message}");
            return false;
        }
    }

    private GameInstallation? FindInstallation(string installationKey)
    {
        var entry = _catalog.Load().Installations.FirstOrDefault(candidate =>
            string.Equals(candidate.InstallationKey, installationKey, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;

        return new GameInstallation(
            KnownGameProfiles.Full, entry.GameDirectory, null, false, false);
    }

    private GameInstallation? FindInstallationForGroup(SaveGroup group)
    {
        var parent = Path.GetDirectoryName(group.DirectoryPath) ?? ProfilesRoot;
        return FindInstallation(Path.GetFileName(parent));
    }

    private string ReserveBackupPath(string live)
    {
        Directory.CreateDirectory(_options.BackupsRoot);
        var key = Path.GetFileName(Path.TrimEndingDirectorySeparator(live));
        var stamp = _options.UtcNow().ToString("yyyyMMddHHmmss");
        var backup = Path.Combine(_options.BackupsRoot, $"{key}-{stamp}");
        var suffix = 2;
        while (Directory.Exists(backup))
            backup = Path.Combine(_options.BackupsRoot, $"{key}-{stamp}-{suffix++}");
        return backup;
    }

    private string MoveLiveToBackup(string live, string backup)
    {
        MoveDirectoryWithFallback(live, backup);
        _options.Logger?.Info($"原存档目录已备份到 {backup}");
        return backup;
    }

    private (bool Success, string? Error) RestoreBackupWithFallback(string backup, string live)
    {
        try
        {
            if (!Directory.Exists(backup))
                return (false, $"备份目录不存在：{backup}");

            if (Directory.Exists(live))
            {
                if (IsReparsePoint(live))
                {
                    if (!_options.Linker.RemoveJunction(live, out var removeError))
                        return (false, $"残留链接无法移除：{removeError}");
                }
                else if (Directory.EnumerateFileSystemEntries(live).Any())
                {
                    return (false, "目标路径已存在且非空，未覆盖。");
                }
                else
                {
                    Directory.Delete(live, recursive: false);
                }
            }

            MoveDirectoryWithFallback(backup, live);
            return (true, null);
        }
        catch (Exception exception)
        {
            _options.Logger?.Warn($"恢复原存档目录失败：{exception.Message}");
            return (false, exception.Message);
        }
    }

    private void MoveDirectoryWithFallback(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            Directory.Move(source, destination);
            return;
        }
        catch (IOException)
        {
            // Cross-volume (or a lock that only affects rename): copy, verify,
            // and only then delete the source.
            var snapshot = CaptureTree(source, rejectNestedLinks: false);
            CopyTree(snapshot, source, destination);
            VerifyTree(snapshot, destination);

            // CopyTree intentionally excludes MK metadata, but a move (unlike
            // a live-folder copy) must preserve the group's identity files.
            foreach (var fileName in new[]
                     {
                         SaveGroupFormat.ManifestFileName,
                         SaveGroupFormat.LegacyProfileMarkerFileName
                     })
            {
                var metadata = Path.Combine(source, fileName);
                if (!File.Exists(metadata)) continue;
                var metadataTarget = Path.Combine(destination, fileName);
                if (!File.Exists(metadataTarget)) File.Copy(metadata, metadataTarget);
            }

            Directory.Delete(source, recursive: true);
        }
    }

    private sealed record TreeFile(string RelativePath, long Length, string Hash);

    private sealed record TreeSnapshot(
        IReadOnlyList<TreeFile> Files,
        IReadOnlyList<string> Directories)
    {
        public int FileCount => Files.Count;
    }

    private TreeSnapshot CaptureTree(string root, bool rejectNestedLinks)
    {
        var files = new List<TreeFile>();
        var directories = new List<string>();
        var stack = new Stack<(string Path, string Relative)>();
        stack.Push((root, string.Empty));

        while (stack.Count > 0)
        {
            var (path, relative) = stack.Pop();
            foreach (var directory in Directory.EnumerateDirectories(path))
            {
                var info = new DirectoryInfo(directory);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    if (rejectNestedLinks)
                        throw new IOException($"存档目录里包含链接或联接点：{directory}");
                    continue;
                }

                var child = CombineRelative(relative, info.Name);
                if (IsLegacyGroupEntry(root, child)) continue;
                directories.Add(child);
                stack.Push((directory, child));
            }

            foreach (var file in Directory.EnumerateFiles(path))
            {
                var name = Path.GetFileName(file);
                if (SaveGroupFormat.IsInternalFile(name)) continue;
                var child = CombineRelative(relative, name);
                if (IsLegacyGroupEntry(root, child)) continue;

                var info = new FileInfo(file);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    if (rejectNestedLinks)
                        throw new IOException($"存档目录里包含文件链接或符号链接：{file}");
                    continue;
                }

                files.Add(new TreeFile(
                    child,
                    info.Length,
                    SaveFileHasher.ComputeSha256(file)));
            }
        }

        files.Sort((left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));
        directories.Sort(StringComparer.Ordinal);
        return new TreeSnapshot(files, directories);
    }

    private static bool IsLegacyGroupEntry(string root, string relative)
    {
        var separatorIndex = relative.IndexOfAny(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        if (separatorIndex < 0) return false;

        var first = relative[..separatorIndex];
        return IsGroupDirectory(Path.Combine(root, first));
    }

    private void CopyTree(TreeSnapshot snapshot, string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var relative in snapshot.Directories)
            Directory.CreateDirectory(Path.Combine(destination, relative));

        foreach (var file in snapshot.Files)
        {
            var target = Path.Combine(destination, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(source, file.RelativePath), target, overwrite: false);
        }
    }

    private void VerifyTree(TreeSnapshot expected, string destination)
    {
        var actual = CaptureTree(destination, rejectNestedLinks: false);
        if (!TreesEqual(expected, actual))
        {
            throw new IOException(
                $"存档复制校验失败：期望 {expected.Files.Count} 个文件 / {expected.Directories.Count} 个目录，" +
                $"实际 {actual.Files.Count} 个文件 / {actual.Directories.Count} 个目录。");
        }
    }

    private static bool TreesEqual(TreeSnapshot left, TreeSnapshot right)
    {
        if (left.Files.Count != right.Files.Count) return false;
        if (left.Directories.Count != right.Directories.Count) return false;

        for (var index = 0; index < left.Directories.Count; index++)
        {
            if (!string.Equals(left.Directories[index], right.Directories[index], StringComparison.Ordinal))
                return false;
        }

        for (var index = 0; index < left.Files.Count; index++)
        {
            var a = left.Files[index];
            var b = right.Files[index];
            if (!string.Equals(a.RelativePath, b.RelativePath, StringComparison.Ordinal)
                || a.Length != b.Length
                || !string.Equals(a.Hash, b.Hash, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static string CombineRelative(string directory, string name) =>
        directory.Length == 0 ? name : Path.Combine(directory, name);

    // ---------------------------------------------------------------------
    // Journal
    // ---------------------------------------------------------------------

    private sealed record PendingHostingChange
    {
        public bool TargetEnabled { get; init; }
        public HostingOperationState FinalState { get; init; }
        public string SuccessMessage { get; init; } = string.Empty;
        public Func<string>? SuccessMessageFactory { get; init; }
        public SaveGroup? Group { get; init; }
        public IReadOnlyList<SaveGroup>? Groups { get; init; }
        public string LiveDirectory { get; init; } = string.Empty;
        public int FileCount { get; init; }
        public string? JournalPath { get; init; }
        public Func<RollbackResult>? Undo { get; init; }
        public Action? OnCommitted { get; init; }
        public string? RestoreRecoveryMessage { get; init; }
    }

}

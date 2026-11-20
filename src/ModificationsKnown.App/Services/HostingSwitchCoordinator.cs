using ModificationsKnown.Core.Diagnostics;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Saves;
using ModificationsKnown.Core.Settings;
using ModificationsKnown.Infrastructure.Saves;

namespace ModificationsKnown.App.Services;

/// <summary>
/// The single owner of the hosting switch: it reads the persisted preference
/// once at startup, classifies “settings missing/corrupt but the live link is
/// still MK's”, persists every mode change on the UI thread, and only then
/// publishes the new state through <see cref="SaveGroupService"/>.
/// </summary>
public sealed class HostingSwitchCoordinator
{
    private readonly SaveGroupService _saveGroups;
    private readonly IAppSettingsService _settings;
    private readonly IAppLogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public HostingSwitchCoordinator(
        SaveGroupService saveGroups,
        IAppSettingsService settings,
        IAppLogger logger)
    {
        _saveGroups = saveGroups;
        _settings = settings;
        _logger = logger;
        _saveGroups.HostingStateChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;

    public SaveHostingState State => _saveGroups.HostingState;

    public string StatusMessage => _saveGroups.HostingStatusMessage;

    public bool HasCorruptSettings =>
        _settings.Hosting.Source is HostingPreferenceSource.Invalid
            or HostingPreferenceSource.ParseFailed;

    public string? CorruptSettingsBackup => _settings.Hosting.CorruptBackupPath;

    /// <summary>
    /// Resolves the persisted preference into exactly one committed state. A
    /// leftover transaction marker wins over everything; a live link that
    /// still points at an MK group while settings say missing/invalid becomes
    /// <c>PendingRecovery</c> instead of silently adopting or discarding it.
    /// </summary>
    public Task InitializeAsync()
    {
        if (_saveGroups.ScanHostingRecovery()) return Task.CompletedTask;

        var preference = _settings.Hosting;
        switch (preference.Source)
        {
            case HostingPreferenceSource.Enabled:
                _settings.Current.HostingEnabled = true;
                if (_saveGroups.DescribeLive().Kind == HostingLiveKind.Unknown)
                {
                    _saveGroups.MarkHostingPendingRecovery(
                        "无法确认游戏存档目录当前指向哪里；为避免误切链接或覆盖存档，" +
                        "已暂停托管与启动。请检查该目录后再选择恢复托管或安全关闭。");
                }
                else
                {
                    _saveGroups.SetHostingStateFromSettings(true);
                }

                break;

            case HostingPreferenceSource.Disabled:
                if (_saveGroups.DescribeLive().Kind == HostingLiveKind.Unknown)
                {
                    _saveGroups.MarkHostingPendingRecovery(
                        "配置显示存档托管已关闭，但无法确认游戏存档目录当前指向哪里；" +
                        "为避免误接管或覆盖存档，已暂停托管与启动。请检查该目录后再处理。");
                }
                else if (_saveGroups.DescribeLive().Kind is HostingLiveKind.MkGroupLink
                    or HostingLiveKind.MkProfileLink)
                {
                    _saveGroups.MarkHostingPendingRecovery(
                        "配置显示存档托管已关闭，但游戏存档目录仍指向 MK 的存档。" +
                        "请选择“恢复托管”，或“安全关闭”把存档还原到游戏目录。");
                }
                else
                {
                    _settings.Current.HostingEnabled = false;
                    _saveGroups.SetHostingStateFromSettings(false);
                }

                break;

            case HostingPreferenceSource.MissingFile:
                if (_saveGroups.DescribeLive().Kind == HostingLiveKind.Unknown)
                {
                    _saveGroups.MarkHostingPendingRecovery(
                        "找不到存档托管配置，且无法确认游戏存档目录当前指向哪里；" +
                        "为避免误接管或覆盖存档，已暂停托管与启动。请检查该目录后再处理。");
                }
                else if (_saveGroups.DescribeLive().Kind is HostingLiveKind.MkGroupLink
                    or HostingLiveKind.MkProfileLink)
                {
                    _saveGroups.MarkHostingPendingRecovery(
                        "找不到存档托管配置，但游戏存档目录仍指向 MK 的存档。" +
                        "请选择“恢复托管”，或“安全关闭”把存档还原到游戏目录。");
                }
                else
                {
                    _settings.Current.HostingEnabled = false;
                    _saveGroups.SetHostingStateFromSettings(false);
                }

                break;

            case HostingPreferenceSource.MissingField:
                var migrated = _settings.Current.HostingEnabled ?? true;
                if (_saveGroups.DescribeLive().Kind == HostingLiveKind.Unknown)
                {
                    _settings.Current.HostingEnabled = migrated;
                    _saveGroups.MarkHostingPendingRecovery(
                        "旧配置缺少存档托管字段，且无法确认游戏存档目录当前指向哪里；" +
                        "为避免误接管或覆盖存档，已暂停托管与启动。请检查该目录后再处理。");
                    break;
                }

                _settings.Current.HostingEnabled = migrated;
                _saveGroups.SetHostingStateFromSettings(migrated);
                break;

            default:
                // Keep a settings-page save from turning an unreadable value
                // into “old config” (which would auto-enable next start).
                _settings.Current.HostingEnabled = false;
                var backup = string.IsNullOrWhiteSpace(preference.CorruptBackupPath)
                    ? string.Empty
                    : $"原文件已保留在 {preference.CorruptBackupPath}。";
                _saveGroups.MarkHostingPendingRecovery(
                    $"存档托管配置无法读取；为避免错误接管或覆盖存档，已暂停托管与启动。{backup}");
                break;
        }

        return Task.CompletedTask;
    }

    public async Task<HostingOperationResult> DisableAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var prepared = await _saveGroups.PrepareDisableHostingAsync(cancellationToken);
            return prepared.NeedsCommit
                ? await PersistAndCompleteAsync(false, prepared)
                : prepared;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<HostingOperationResult> RestoreFromRecoveryAsync(
        GameInstallation? installation = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var journal = _saveGroups.GetHostingJournalScan();
            if (journal.Status == HostingJournalScanStatus.Invalid)
                return MapRecovery(new HostingRecoveryResult(
                    HostingRecoveryOutcome.Refused, journal.Message, Paths: journal.Paths));

            if (journal.Status == HostingJournalScanStatus.None)
            {
                if (installation is null)
                    return new HostingOperationResult(
                        HostingOperationState.PendingRecovery,
                        "没有事务记录，但也没有可确认的接收实例；未改动任何文件。");

                var kind = _saveGroups.DescribeLive().Kind;
                if (kind is not (HostingLiveKind.MkGroupLink or HostingLiveKind.MkProfileLink))
                    return new HostingOperationResult(
                        HostingOperationState.PendingRecovery,
                        "没有事务记录，且当前 live 不是可确认的 MK 存档链接；未改动任何文件。");

                var accepted = _saveGroups.AcceptExistingMkLink(installation, allowRecovery: true);
                return accepted.NeedsCommit
                    ? await PersistAndCompleteAsync(true, accepted)
                    : accepted;
            }

            var prepared = await Task.Run(
                () => _saveGroups.PrepareHostingRecovery(HostingRecoveryAction.RestoreHosting),
                cancellationToken);
            return await FinishRecoveryAsync(prepared);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<HostingOperationResult> DisableFromRecoveryAsync(
        GameInstallation? installation = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var journal = _saveGroups.GetHostingJournalScan();
            if (journal.Status == HostingJournalScanStatus.Invalid)
                return MapRecovery(new HostingRecoveryResult(
                    HostingRecoveryOutcome.Refused, journal.Message, Paths: journal.Paths));

            if (journal.Status == HostingJournalScanStatus.None)
            {
                if (installation is null)
                    return new HostingOperationResult(
                        HostingOperationState.PendingRecovery,
                        "没有可用于确认归属的游戏实例；未改动任何文件。");

                var prepared = await Task.Run(
                    () => _saveGroups.PrepareDisableHostingWithoutJournalForRecovery(installation),
                    cancellationToken);
                return prepared.NeedsCommit
                    ? await PersistAndCompleteAsync(false, prepared)
                    : prepared;
            }

            var withJournal = await Task.Run(
                () => _saveGroups.PrepareHostingRecovery(HostingRecoveryAction.DisableHosting),
                cancellationToken);
            return await FinishRecoveryAsync(withJournal);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HostingOperationResult> FinishRecoveryAsync(HostingRecoveryResult prepared)
    {
        if (!prepared.NeedsCommit)
            return MapRecovery(prepared);

        var enabled = prepared.RequiredEnabled == true;
        var previous = _settings.Current.HostingEnabled;
        var persisted = true;
        try
        {
            _settings.Current.HostingEnabled = enabled;
            await _settings.SaveAsync();
            if (_settings.Current.HostingEnabled != enabled)
                throw new IOException("托管设置读回与写入值不一致。");
        }
        catch (Exception exception)
        {
            persisted = false;
            _settings.Current.HostingEnabled = previous;
            _logger.Warn($"保存恢复后的存档托管开关失败：{exception.Message}");
        }

        var completed = _saveGroups.CompleteHostingRecovery(persisted, enabled);
        return MapRecovery(completed);
    }

    private static HostingOperationResult MapRecovery(HostingRecoveryResult result)
    {
        var state = result.Outcome switch
        {
            HostingRecoveryOutcome.NeedsSettings => HostingOperationState.Prepared,
            HostingRecoveryOutcome.Completed => result.RequiredEnabled == true
                ? HostingOperationState.Enabled
                : HostingOperationState.Disabled,
            HostingRecoveryOutcome.Refused => HostingOperationState.PendingRecovery,
            _ => HostingOperationState.FailedUnknown
        };
        return new HostingOperationResult(state, result.Message);
    }

    public async Task<HostingOperationResult> EnableAsync(
        GameInstallation installation,
        HostingEnableRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var prepared = await _saveGroups.PrepareEnableHostingAsync(
                installation, request, cancellationToken);
            return prepared.NeedsCommit
                ? await PersistAndCompleteAsync(true, prepared)
                : prepared;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<HostingOperationResult> AcceptExistingLinkAsync(
        GameInstallation installation,
        bool fromRecovery = false,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var prepared = _saveGroups.AcceptExistingMkLink(installation, fromRecovery);
            return prepared.NeedsCommit
                ? await PersistAndCompleteAsync(true, prepared)
                : prepared;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HostingOperationResult> PersistAndCompleteAsync(
        bool enabled,
        HostingOperationResult prepared)
    {
        string? error = null;
        var previous = _settings.Current.HostingEnabled;
        try
        {
            _settings.Current.HostingEnabled = enabled;
            await _settings.SaveAsync();
            if (_settings.Current.HostingEnabled != enabled)
                throw new IOException("托管设置读回与写入值不一致。");
        }
        catch (Exception exception)
        {
            error = exception.Message;
            _settings.Current.HostingEnabled = previous;
            _logger.Warn($"保存存档托管开关失败：{exception.Message}");
        }

        var result = await Task.Run(() =>
            _saveGroups.CompleteHostingChange(error is null, enabled));
        if (error is not null)
        {
            return result with
            {
                Message = $"{result.Message} 原因：{error}"
            };
        }

        return result;
    }
}

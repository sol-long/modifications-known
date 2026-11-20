using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Saves;
using ModificationsKnown.Infrastructure.Saves;

namespace ModificationsKnown.Tests;

/// <summary>Second-review recovery cases: consumed staging, changed live,
/// retry after settings failure, overwrite preflight and selection readback.</summary>
public sealed class HostingRecoverySecondReviewTests
{
    [Fact]
    public async Task Recover_StagingConsumedButLiveComplete_CompletesDisable()
    {
        using var context = new SaveGroupTestContext();
        var group = await CreateEnabledGroupAsync(context);
        context.Linker.RemoveJunction(context.Live, out _);
        CopyDirectory(group.DirectoryPath, context.Live);
        var journal = WriteJournal(context, DisableRecord(context, group, ".missing-staging", TreeOf(context.Live)));

        var fresh = context.BuildService();
        var prepared = fresh.PrepareHostingRecovery(HostingRecoveryAction.DisableHosting);
        Assert.True(prepared.Outcome == HostingRecoveryOutcome.NeedsSettings, prepared.Message);
        var done = fresh.CompleteHostingRecovery(true, false);

        Assert.True(done.Outcome == HostingRecoveryOutcome.Completed, done.Message);
        Assert.False(File.Exists(journal));
        Assert.Equal("world", File.ReadAllText(Path.Combine(context.Live, "save.sv")));
    }

    [Fact]
    public async Task Recover_LiveChangedFromJournalTree_RefusesWithoutDeletingIt()
    {
        using var context = new SaveGroupTestContext();
        var group = await CreateEnabledGroupAsync(context);
        context.Linker.RemoveJunction(context.Live, out _);
        CopyDirectory(group.DirectoryPath, context.Live);
        var tree = TreeOf(context.Live);
        File.WriteAllText(Path.Combine(context.Live, "new-user-save.sv"), "new");
        var journal = WriteJournal(context, DisableRecord(context, group, ".missing-staging-2", tree));

        var fresh = context.BuildService();
        var result = fresh.PrepareHostingRecovery(HostingRecoveryAction.DisableHosting);

        Assert.Equal(HostingRecoveryOutcome.Refused, result.Outcome);
        Assert.Equal("new", File.ReadAllText(Path.Combine(context.Live, "new-user-save.sv")));
        Assert.True(File.Exists(journal));
    }

    [Fact]
    public async Task Recover_RestoreHostingWhenTargetChanged_Refuses()
    {
        using var context = new SaveGroupTestContext();
        var group = await CreateEnabledGroupAsync(context);
        context.Linker.RemoveJunction(context.Live, out _);
        CopyDirectory(group.DirectoryPath, context.Live);
        var tree = TreeOf(context.Live);
        File.WriteAllText(Path.Combine(group.DirectoryPath, "changed.sv"), "changed");
        var journal = WriteJournal(context, DisableRecord(context, group, ".missing-staging-3", tree));

        var fresh = context.BuildService();
        var result = fresh.PrepareHostingRecovery(HostingRecoveryAction.RestoreHosting);

        Assert.Equal(HostingRecoveryOutcome.Refused, result.Outcome);
        Assert.Equal("world", File.ReadAllText(Path.Combine(context.Live, "save.sv")));
        Assert.True(File.Exists(journal));
    }

    [Fact]
    public async Task Recover_DisableSettingsFail_CanRetryWithFreshService()
    {
        using var context = new SaveGroupTestContext();
        var group = await CreateEnabledGroupAsync(context);
        context.Linker.RemoveJunction(context.Live, out _);
        CopyDirectory(group.DirectoryPath, context.Live);
        var journal = WriteJournal(context, DisableRecord(context, group, ".missing-staging-4", TreeOf(context.Live)));

        var first = context.BuildService();
        Assert.True(first.PrepareHostingRecovery(HostingRecoveryAction.DisableHosting).NeedsCommit);
        var failed = first.CompleteHostingRecovery(false, false);
        Assert.NotEqual(HostingRecoveryOutcome.Completed, failed.Outcome);
        Assert.True(File.Exists(journal));

        var second = context.BuildService();
        var retry = second.PrepareHostingRecovery(HostingRecoveryAction.DisableHosting);
        Assert.True(retry.Outcome == HostingRecoveryOutcome.NeedsSettings, retry.Message);
        var done = second.CompleteHostingRecovery(true, false);
        Assert.True(done.Outcome == HostingRecoveryOutcome.Completed, done.Message);
        Assert.False(File.Exists(journal));
    }

    [Fact]
    public async Task Recover_OverwriteBackupMissing_RefusesBeforeUnlinking()
    {
        using var context = new SaveGroupTestContext();
        var group = await CreateEnabledGroupAsync(context);
        var journal = WriteJournal(context, new HostingJournalRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Operation = "enable-overwrite",
            Phase = "backup-live",
            InstallationKey = context.InstallationKey,
            GroupId = group.Id,
            LivePath = context.Live,
            LivePreviousKind = "Data",
            PreviousSelectionGroupId = group.Id,
            SourcePath = context.Live,
            BackupPath = Path.Combine(context.BackupsRoot, "missing-backup"),
            TargetGroupPath = group.DirectoryPath,
            Tree = TreeOf(group.DirectoryPath)
        });

        var fresh = context.BuildService();
        var result = fresh.PrepareHostingRecovery(HostingRecoveryAction.DisableHosting);

        Assert.Equal(HostingRecoveryOutcome.Refused, result.Outcome);
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(group.DirectoryPath, target);
        Assert.True(File.Exists(journal));
    }


    [Fact]
    public async Task Recover_OverwriteRestoreWithRealLive_RefusesInsteadOfDisabling()
    {
        using var context = new SaveGroupTestContext();
        var group = await CreateEnabledGroupAsync(context);
        context.Linker.RemoveJunction(context.Live, out _);
        Directory.CreateDirectory(context.Live);
        File.WriteAllText(Path.Combine(context.Live, "new-live.sv"), "keep");
        var journal = WriteJournal(context, new HostingJournalRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Operation = "enable-overwrite",
            Phase = "install-target",
            InstallationKey = context.InstallationKey,
            GroupId = group.Id,
            LivePath = context.Live,
            LivePreviousKind = "Data",
            PreviousSelectionGroupId = group.Id,
            SourcePath = context.Live,
            TargetGroupPath = group.DirectoryPath,
            Tree = TreeOf(group.DirectoryPath)
        });

        var fresh = context.BuildService();
        var result = fresh.PrepareHostingRecovery(HostingRecoveryAction.RestoreHosting);

        Assert.True(result.Outcome == HostingRecoveryOutcome.Refused, result.Message);
        Assert.Equal(SaveHostingState.PendingRecovery, fresh.HostingState);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(context.Live, "new-live.sv")));
        Assert.True(File.Exists(journal));
    }


    [Fact]
    public async Task Recover_OverwriteRollbackWrongGroup_RefusesBeforeUnlink()
    {
        using var context = new SaveGroupTestContext();
        var group = await CreateEnabledGroupAsync(context);
        var rollback = Path.Combine(Path.GetDirectoryName(group.DirectoryPath)!, ".mk-wrong-group");
        Directory.CreateDirectory(rollback);
        CopyDirectory(group.DirectoryPath, rollback);
        Assert.True(SaveGroupManifestStore.TryRead(rollback, out var manifest));
        manifest.GroupId = Guid.NewGuid();
        SaveGroupManifestStore.Write(rollback, manifest);
        WriteJournal(context, new HostingJournalRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Operation = "enable-overwrite",
            Phase = "backup-live",
            InstallationKey = context.InstallationKey,
            GroupId = group.Id,
            LivePath = context.Live,
            LivePreviousKind = "Data",
            PreviousSelectionGroupId = group.Id,
            SourcePath = context.Live,
            RollbackPath = rollback,
            TargetGroupPath = group.DirectoryPath,
            RollbackTree = TreeOf(rollback)
        });

        var fresh = context.BuildService();
        var result = fresh.PrepareHostingRecovery(HostingRecoveryAction.DisableHosting);

        Assert.Equal(HostingRecoveryOutcome.Refused, result.Outcome);
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(group.DirectoryPath, target);
    }

    [Fact]
    public async Task Recover_EmptyPreviousKind_RestoresEmptyDirectory()
    {
        using var context = new SaveGroupTestContext();
        var previous = await CreateEnabledGroupAsync(context);
        var empty = await context.Service.CreateEmptyAsync(context.Installation, "empty-target");
        Assert.True(empty.Success, empty.Message);
        var target = Assert.IsType<SaveGroup>(empty.Group);
        context.Linker.RemoveJunction(context.Live, out _);
        context.Linker.CreateJunction(context.Live, target.DirectoryPath, out _);
        WriteJournal(context, new HostingJournalRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Operation = "enable-empty",
            Phase = "link",
            InstallationKey = context.InstallationKey,
            GroupId = target.Id,
            LivePath = context.Live,
            LivePreviousKind = "EmptyDirectory",
            PreviousSelectionGroupId = previous.Id,
            SourcePath = context.Live,
            TargetGroupPath = target.DirectoryPath,
            Tree = TreeOf(target.DirectoryPath)
        });

        var fresh = context.BuildService();
        var prepared = fresh.PrepareHostingRecovery(HostingRecoveryAction.DisableHosting);
        Assert.True(prepared.Outcome == HostingRecoveryOutcome.NeedsSettings, prepared.Message);
        var done = fresh.CompleteHostingRecovery(true, false);
        Assert.True(done.Outcome == HostingRecoveryOutcome.Completed, done.Message);
        Assert.True(Directory.Exists(context.Live));
        Assert.Empty(Directory.EnumerateFileSystemEntries(context.Live));
        Assert.False(context.Linker.TryGetTarget(context.Live, out _));
    }

    [Fact]
    public async Task Recover_EnableLinkToTarget_PersistsTargetSelectionBeforeSettings()
    {
        using var context = new SaveGroupTestContext();
        var a = await CreateEnabledGroupAsync(context);
        var bResult = await context.Service.CreateEmptyAsync(context.Installation, "B");
        Assert.True(bResult.Success, bResult.Message);
        var b = Assert.IsType<SaveGroup>(bResult.Group);
        Assert.True((await context.Service.SelectNextLaunchAsync(context.Installation, a.Id)).Success);
        context.Linker.RemoveJunction(context.Live, out _);
        context.Linker.CreateJunction(context.Live, b.DirectoryPath, out _);
        WriteJournal(context, new HostingJournalRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Operation = "enable-import",
            Phase = "link",
            InstallationKey = context.InstallationKey,
            GroupId = b.Id,
            LivePath = context.Live,
            LivePreviousKind = "Data",
            PreviousSelectionGroupId = a.Id,
            SourcePath = context.Live,
            TargetGroupPath = b.DirectoryPath,
            Tree = TreeOf(b.DirectoryPath)
        });

        var fresh = context.BuildService();
        var prepared = fresh.PrepareHostingRecovery(HostingRecoveryAction.RestoreHosting);
        Assert.True(prepared.Outcome == HostingRecoveryOutcome.NeedsSettings, prepared.Message);
        var done = fresh.CompleteHostingRecovery(true, true);

        Assert.True(done.Outcome == HostingRecoveryOutcome.Completed, done.Message);
        Assert.Equal(b.Id, fresh.GetNextLaunchGroupId(context.Installation));
        Assert.True(context.Linker.TryGetTarget(context.Live, out var liveTarget));
        Assert.Equal(b.DirectoryPath, liveTarget);
    }

    [Fact]
    public async Task Recover_OverwriteRollbackSettingsFail_RetryUsesProgress()
    {
        using var context = new SaveGroupTestContext();
        var group = await CreateEnabledGroupAsync(context);
        context.Linker.RemoveJunction(context.Live, out _);
        var old = Path.Combine(Path.GetDirectoryName(group.DirectoryPath)!, ".mk-old-review");
        Directory.CreateDirectory(old);
        CopyDirectory(group.DirectoryPath, old);
        File.WriteAllText(Path.Combine(group.DirectoryPath, "save.sv"), "new");
        var backup = context.Temp.CreateDirectory("backups", "overwrite-live");
        File.WriteAllText(Path.Combine(backup, "save.sv"), "new");
        var journal = WriteJournal(context, new HostingJournalRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Operation = "enable-overwrite",
            Phase = "backup-live",
            InstallationKey = context.InstallationKey,
            GroupId = group.Id,
            LivePath = context.Live,
            LivePreviousKind = "Data",
            PreviousSelectionGroupId = group.Id,
            SourcePath = context.Live,
            RollbackPath = old,
            BackupPath = backup,
            TargetGroupPath = group.DirectoryPath,
            Tree = TreeOf(group.DirectoryPath),
            RollbackTree = TreeOf(old)
        });

        var first = context.BuildService();
        var prepared = first.PrepareHostingRecovery(HostingRecoveryAction.DisableHosting);
        Assert.True(prepared.Outcome == HostingRecoveryOutcome.NeedsSettings, prepared.Message);
        var failed = first.CompleteHostingRecovery(false, false);
        Assert.NotEqual(HostingRecoveryOutcome.Completed, failed.Outcome);
        Assert.Contains("恢复步骤已完成", failed.Message);
        Assert.DoesNotContain("已补偿", failed.Message);
        Assert.True(File.Exists(journal));
        Assert.Equal("world", File.ReadAllText(Path.Combine(group.DirectoryPath, "save.sv")));

        var second = context.BuildService();
        var retry = second.PrepareHostingRecovery(HostingRecoveryAction.DisableHosting);
        Assert.True(retry.Outcome == HostingRecoveryOutcome.NeedsSettings, retry.Message);
        var done = second.CompleteHostingRecovery(true, false);
        Assert.True(done.Outcome == HostingRecoveryOutcome.Completed, done.Message);
        Assert.False(File.Exists(journal));
    }


    [Fact]
    public async Task Recover_SafeCloseWhenTargetMissing_StillCompletesFromLiveTree()
    {
        using var context = new SaveGroupTestContext();
        var group = await CreateEnabledGroupAsync(context);
        context.Linker.RemoveJunction(context.Live, out _);
        CopyDirectory(group.DirectoryPath, context.Live);
        Directory.Delete(group.DirectoryPath, recursive: true);
        var journal = WriteJournal(context, DisableRecord(context, group, ".missing-staging-5", TreeOf(context.Live)));

        var fresh = context.BuildService();
        var prepared = fresh.PrepareHostingRecovery(HostingRecoveryAction.DisableHosting);
        Assert.True(prepared.Outcome == HostingRecoveryOutcome.NeedsSettings, prepared.Message);
        var done = fresh.CompleteHostingRecovery(true, false);
        Assert.True(done.Outcome == HostingRecoveryOutcome.Completed, done.Message);
        Assert.False(File.Exists(journal));
        Assert.Equal("world", File.ReadAllText(Path.Combine(context.Live, "save.sv")));
    }

    [Fact]
    public async Task Recover_OverwriteLinkToTarget_PersistsTargetSelection()
    {
        using var context = new SaveGroupTestContext();
        var a = await CreateEnabledGroupAsync(context);
        var bResult = await context.Service.CreateEmptyAsync(context.Installation, "B");
        var b = Assert.IsType<SaveGroup>(bResult.Group);
        Assert.True((await context.Service.SelectNextLaunchAsync(context.Installation, a.Id)).Success);
        context.Linker.RemoveJunction(context.Live, out _);
        context.Linker.CreateJunction(context.Live, b.DirectoryPath, out _);
        WriteJournal(context, new HostingJournalRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Operation = "enable-overwrite",
            Phase = "link",
            InstallationKey = context.InstallationKey,
            GroupId = b.Id,
            LivePath = context.Live,
            LivePreviousKind = "Data",
            PreviousSelectionGroupId = a.Id,
            SourcePath = context.Live,
            TargetGroupPath = b.DirectoryPath,
            Tree = TreeOf(b.DirectoryPath)
        });

        var fresh = context.BuildService();
        var prepared = fresh.PrepareHostingRecovery(HostingRecoveryAction.RestoreHosting);
        Assert.True(prepared.Outcome == HostingRecoveryOutcome.NeedsSettings, prepared.Message);
        var done = fresh.CompleteHostingRecovery(true, true);

        Assert.True(done.Outcome == HostingRecoveryOutcome.Completed, done.Message);
        Assert.Equal(b.Id, fresh.GetNextLaunchGroupId(context.Installation));
    }

    [Fact]
    public async Task Recover_MissingPreviousKind_RestoresMissingPath()
    {
        using var context = new SaveGroupTestContext();
        var a = await CreateEnabledGroupAsync(context);
        var bResult = await context.Service.CreateEmptyAsync(context.Installation, "B");
        var b = Assert.IsType<SaveGroup>(bResult.Group);
        Assert.True((await context.Service.SelectNextLaunchAsync(context.Installation, a.Id)).Success);
        context.Linker.RemoveJunction(context.Live, out _);
        context.Linker.CreateJunction(context.Live, b.DirectoryPath, out _);
        WriteJournal(context, new HostingJournalRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Operation = "enable-empty",
            Phase = "link",
            InstallationKey = context.InstallationKey,
            GroupId = b.Id,
            LivePath = context.Live,
            LivePreviousKind = "Missing",
            PreviousSelectionGroupId = a.Id,
            SourcePath = context.Live,
            TargetGroupPath = b.DirectoryPath,
            Tree = TreeOf(b.DirectoryPath)
        });

        var fresh = context.BuildService();
        var prepared = fresh.PrepareHostingRecovery(HostingRecoveryAction.DisableHosting);
        Assert.True(prepared.Outcome == HostingRecoveryOutcome.NeedsSettings, prepared.Message);
        var done = fresh.CompleteHostingRecovery(true, false);

        Assert.True(done.Outcome == HostingRecoveryOutcome.Completed, done.Message);
        Assert.False(Directory.Exists(context.Live));
        Assert.False(context.Linker.TryGetTarget(context.Live, out _));
    }


    [Fact]
    public async Task Recover_EnableSelectionSaveFails_KeepsJournalAndRetrySucceeds()
    {
        using var context = new SaveGroupTestContext();
        var a = await CreateEnabledGroupAsync(context);
        var bResult = await context.Service.CreateEmptyAsync(context.Installation, "B");
        var b = Assert.IsType<SaveGroup>(bResult.Group);
        Assert.True((await context.Service.SelectNextLaunchAsync(context.Installation, a.Id)).Success);
        context.Linker.RemoveJunction(context.Live, out _);
        context.Linker.CreateJunction(context.Live, b.DirectoryPath, out _);
        var journal = WriteJournal(context, new HostingJournalRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Operation = "enable-import",
            Phase = "link",
            InstallationKey = context.InstallationKey,
            GroupId = b.Id,
            LivePath = context.Live,
            LivePreviousKind = "Data",
            PreviousSelectionGroupId = a.Id,
            SourcePath = context.Live,
            TargetGroupPath = b.DirectoryPath,
            Tree = TreeOf(b.DirectoryPath)
        });
        File.SetAttributes(context.CatalogPath, FileAttributes.ReadOnly);
        var first = context.BuildService();
        HostingRecoveryResult failed;
        try
        {
            failed = first.PrepareHostingRecovery(HostingRecoveryAction.RestoreHosting);
        }
        finally
        {
            File.SetAttributes(context.CatalogPath, FileAttributes.Normal);
        }

        Assert.Equal(HostingRecoveryOutcome.FailedUnknown, failed.Outcome);
        Assert.True(File.Exists(journal));
        Assert.True(context.Linker.TryGetTarget(context.Live, out var stillTarget));
        Assert.Equal(b.DirectoryPath, stillTarget);

        var second = context.BuildService();
        var retry = second.PrepareHostingRecovery(HostingRecoveryAction.RestoreHosting);
        Assert.True(retry.Outcome == HostingRecoveryOutcome.NeedsSettings, retry.Message);
        var done = second.CompleteHostingRecovery(true, true);
        Assert.True(done.Outcome == HostingRecoveryOutcome.Completed, done.Message);
        Assert.Equal(b.Id, second.GetNextLaunchGroupId(context.Installation));
    }

    [Fact]
    public async Task Recover_OverwriteTargetAlreadyRestored_RetryDoesNotRequireRollback()
    {
        using var context = new SaveGroupTestContext();
        var group = await CreateEnabledGroupAsync(context);
        context.Linker.RemoveJunction(context.Live, out _);
        var old = Path.Combine(Path.GetDirectoryName(group.DirectoryPath)!, ".mk-old-restored");
        Directory.CreateDirectory(old);
        CopyDirectory(group.DirectoryPath, old);
        var backup = context.Temp.CreateDirectory("backups", "overwrite-restored-live");
        File.WriteAllText(Path.Combine(backup, "save.sv"), "world");
        File.WriteAllText(Path.Combine(group.DirectoryPath, "save.sv"), "world");
        WriteJournal(context, new HostingJournalRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Operation = "enable-overwrite",
            Phase = "backup-live",
            InstallationKey = context.InstallationKey,
            GroupId = group.Id,
            LivePath = context.Live,
            LivePreviousKind = "Data",
            PreviousSelectionGroupId = group.Id,
            SourcePath = context.Live,
            BackupPath = backup,
            TargetGroupPath = group.DirectoryPath,
            Tree = TreeOf(group.DirectoryPath),
            RollbackTree = TreeOf(old),
            RecoveryStage = HostingRecoveryStages.TargetRestored
        });
        Directory.Delete(old, recursive: true);

        var fresh = context.BuildService();
        var prepared = fresh.PrepareHostingRecovery(HostingRecoveryAction.DisableHosting);
        Assert.True(prepared.Outcome == HostingRecoveryOutcome.NeedsSettings, prepared.Message);
        var done = fresh.CompleteHostingRecovery(true, false);
        Assert.True(done.Outcome == HostingRecoveryOutcome.Completed, done.Message);
    }


    [Fact]
    public async Task Recover_OverwriteWithLiveLink_RestoresBackupBeforeCompleting()
    {
        using var context = new SaveGroupTestContext();
        var group = await CreateEnabledGroupAsync(context);
        var old = Path.Combine(Path.GetDirectoryName(group.DirectoryPath)!, ".mk-old-live-link");
        Directory.CreateDirectory(old);
        CopyDirectory(group.DirectoryPath, old);
        File.WriteAllText(Path.Combine(group.DirectoryPath, "save.sv"), "new");
        var backup = context.Temp.CreateDirectory("backups", "overwrite-link-live");
        File.WriteAllText(Path.Combine(backup, "save.sv"), "new");
        context.Linker.RemoveJunction(context.Live, out _);
        context.Linker.CreateJunction(context.Live, group.DirectoryPath, out _);
        var journal = WriteJournal(context, new HostingJournalRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Operation = "enable-overwrite",
            Phase = "link",
            InstallationKey = context.InstallationKey,
            GroupId = group.Id,
            LivePath = context.Live,
            LivePreviousKind = "Data",
            PreviousSelectionGroupId = group.Id,
            SourcePath = context.Live,
            RollbackPath = old,
            BackupPath = backup,
            TargetGroupPath = group.DirectoryPath,
            Tree = TreeOf(group.DirectoryPath),
            RollbackTree = TreeOf(old)
        });

        var fresh = context.BuildService();
        var prepared = fresh.PrepareHostingRecovery(HostingRecoveryAction.DisableHosting);
        Assert.True(prepared.Outcome == HostingRecoveryOutcome.NeedsSettings, prepared.Message);
        var done = fresh.CompleteHostingRecovery(true, false);

        Assert.True(done.Outcome == HostingRecoveryOutcome.Completed, done.Message);
        Assert.True(Directory.Exists(context.Live));
        Assert.False(context.Linker.TryGetTarget(context.Live, out _));
        Assert.Equal("new", File.ReadAllText(Path.Combine(context.Live, "save.sv")));
        Assert.Equal("world", File.ReadAllText(Path.Combine(group.DirectoryPath, "save.sv")));
        Assert.False(File.Exists(journal));
    }

    private static HostingJournalRecord DisableRecord(
        SaveGroupTestContext context,
        SaveGroup group,
        string stagingName,
        HostingJournalTree tree) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Operation = "disable",
        Phase = "install-live",
        InstallationKey = context.InstallationKey,
        GroupId = group.Id,
        LivePath = context.Live,
        LivePreviousKind = "MkGroupLink",
        LivePreviousTarget = group.DirectoryPath,
        PreviousSelectionGroupId = group.Id,
        SourcePath = context.Live,
        StagingPath = Path.Combine(Path.GetDirectoryName(context.Live)!, stagingName),
        Tree = tree
    };

    private static async Task<SaveGroup> CreateEnabledGroupAsync(SaveGroupTestContext context)
    {
        context.CreateLiveDirectory();
        File.WriteAllText(Path.Combine(context.Live, "save.sv"), "world");
        context.Service.SetHostingStateFromSettings(false);
        var prepared = await context.Service.PrepareEnableHostingAsync(
            context.Installation,
            new HostingEnableRequest(HostingEnableChoice.ImportIntoNewGroup, "g1"));
        Assert.True(prepared.NeedsCommit, prepared.Message);
        var done = context.Service.CompleteHostingChange(true, true);
        Assert.Equal(HostingOperationState.Enabled, done.State);
        return Assert.IsType<SaveGroup>(done.Group);
    }

    private static string WriteJournal(SaveGroupTestContext context, HostingJournalRecord record)
    {
        var path = Path.Combine(context.JournalRoot, record.Id + ".json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(record, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        }));
        return path;
    }

    private static HostingJournalTree TreeOf(string root)
    {
        var files = new List<HostingJournalFile>();
        var directories = new List<string>();
        Walk(root, string.Empty);
        files.Sort((left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));
        directories.Sort(StringComparer.Ordinal);
        return new HostingJournalTree(files, directories);

        void Walk(string directory, string relative)
        {
            foreach (var file in Directory.GetFiles(directory))
            {
                var name = Path.GetFileName(file);
                if (SaveGroupFormat.IsInternalFile(name)) continue;
                var rel = string.IsNullOrEmpty(relative) ? name : Path.Combine(relative, name);
                var bytes = File.ReadAllBytes(file);
                files.Add(new HostingJournalFile(
                    rel, bytes.Length,
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant()));
            }

            foreach (var child in Directory.GetDirectories(directory))
            {
                var name = Path.GetFileName(child);
                var rel = string.IsNullOrEmpty(relative) ? name : Path.Combine(relative, name);
                directories.Add(rel);
                Walk(child, rel);
            }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var directory in Directory.GetDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
}

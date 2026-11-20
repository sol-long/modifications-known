using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Saves;
using ModificationsKnown.Infrastructure.Saves;

namespace ModificationsKnown.Tests;

public sealed class HostingRecoverySafetyTests
{
    [Fact]
    public void Recover_WhenGameRunning_RefusesBeforeChangingAnyPath()
    {
        using var context = new SaveGroupTestContext();
        var staging = context.Temp.CreateDirectory("staging", "partial");
        File.WriteAllText(Path.Combine(staging, "save.sv"), "partial");
        var journal = WriteJournal(context, new HostingJournalRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Operation = "disable",
            Phase = "copy",
            InstallationKey = context.InstallationKey,
            LivePath = context.Live,
            LivePreviousKind = "Missing",
            SourcePath = context.Live,
            StagingPath = staging
        });
        context.Running = true;

        var result = context.Service.PrepareHostingRecovery(HostingRecoveryAction.DisableHosting);

        Assert.Equal(HostingRecoveryOutcome.Refused, result.Outcome);
        Assert.False(Directory.Exists(context.Live));
        Assert.Equal("partial", File.ReadAllText(Path.Combine(staging, "save.sv")));
        Assert.True(File.Exists(journal));
    }

    [Fact]
    public void Recover_WhenRunningStateUnknown_RefusesBeforeChangingAnyPath()
    {
        using var context = new SaveGroupTestContext();
        var staging = context.Temp.CreateDirectory("staging", "partial");
        File.WriteAllText(Path.Combine(staging, "save.sv"), "partial");
        WriteJournal(context, new HostingJournalRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Operation = "disable",
            Phase = "copy",
            InstallationKey = context.InstallationKey,
            LivePath = context.Live,
            LivePreviousKind = "Missing",
            SourcePath = context.Live,
            StagingPath = staging
        });
        context.ProbeAnyState = GameRunningState.Unknown;

        var result = context.Service.PrepareHostingRecovery(HostingRecoveryAction.DisableHosting);

        Assert.Equal(HostingRecoveryOutcome.Refused, result.Outcome);
        Assert.False(Directory.Exists(context.Live));
    }

    [Fact]
    public async Task Recover_CopyPhasePartialStaging_IsNeverInstalled()
    {
        using var context = new SaveGroupTestContext();
        var group = await CreateEnabledGroupAsync(context);
        context.Linker.RemoveJunction(context.Live, out _);
        var staging = context.Temp.CreateDirectory("staging", "partial");
        File.WriteAllText(Path.Combine(staging, "save.sv"), "partial");
        WriteJournal(context, new HostingJournalRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Operation = "disable",
            Phase = "copy",
            InstallationKey = context.InstallationKey,
            GroupId = group.Id,
            LivePath = context.Live,
            LivePreviousKind = "MkGroupLink",
            LivePreviousTarget = group.DirectoryPath,
            SourcePath = context.Live,
            StagingPath = staging
        });

        var result = context.Service.PrepareHostingRecovery(HostingRecoveryAction.DisableHosting);

        Assert.Equal(HostingRecoveryOutcome.Refused, result.Outcome);
        Assert.False(Directory.Exists(context.Live));
        Assert.True(File.Exists(Path.Combine(staging, "save.sv")));
    }

    [Fact]
    public void Recover_JournalNullOrCorrupt_KeepsRecoveryGate()
    {
        using var context = new SaveGroupTestContext();
        var journal = Path.Combine(context.JournalRoot, "bad.json");
        File.WriteAllText(journal, "null");

        Assert.True(context.Service.ScanHostingRecovery());
        var result = context.Service.PrepareHostingRecovery(HostingRecoveryAction.DisableHosting);

        Assert.Equal(HostingRecoveryOutcome.Refused, result.Outcome);
        Assert.True(File.Exists(journal));
        Assert.False(Directory.Exists(context.Live));

        File.WriteAllText(journal, "{ not json");
        Assert.True(context.Service.ScanHostingRecovery());
        Assert.Equal(
            HostingRecoveryOutcome.Refused,
            context.Service.PrepareHostingRecovery(HostingRecoveryAction.DisableHosting).Outcome);
        Assert.True(File.Exists(journal));
    }

    [Fact]
    public void Recover_LockedJournal_IsTreatedAsUnreadable()
    {
        using var context = new SaveGroupTestContext();
        var journal = Path.Combine(context.JournalRoot, "locked.json");
        File.WriteAllText(journal, "{}");
        using var lockStream = new FileStream(
            journal, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.True(context.Service.ScanHostingRecovery());
        Assert.Equal(
            HostingRecoveryOutcome.Refused,
            context.Service.PrepareHostingRecovery(HostingRecoveryAction.DisableHosting).Outcome);
    }

    [Fact]
    public void Recover_WhenJournalRootIsAFile_RefusesInsteadOfIgnoringIt()
    {
        using var context = new SaveGroupTestContext();
        Directory.Delete(context.JournalRoot, recursive: true);
        File.WriteAllText(context.JournalRoot, "not a directory");

        Assert.True(context.Service.ScanHostingRecovery());
        Assert.Equal(
            HostingRecoveryOutcome.Refused,
            context.Service.PrepareHostingRecovery(HostingRecoveryAction.DisableHosting).Outcome);
        Assert.False(Directory.Exists(context.Live));
    }

    [Fact]
    public async Task Recover_VerifiedStaging_CompletesDisableFromFreshServiceInstance()
    {
        using var context = new SaveGroupTestContext();
        var group = await CreateEnabledGroupAsync(context);
        context.Linker.RemoveJunction(context.Live, out _);
        var staging = context.Temp.CreateDirectory("staging", "verified");
        CopyDirectory(group.DirectoryPath, staging);
        var journal = WriteJournal(context, new HostingJournalRecord
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
            SourcePath = group.DirectoryPath,
            StagingPath = staging,
            Tree = TreeOf(staging)
        });

        var fresh = context.BuildService();
        Assert.True(fresh.ScanHostingRecovery());
        var prepared = fresh.PrepareHostingRecovery(HostingRecoveryAction.DisableHosting);
        Assert.True(prepared.Outcome == HostingRecoveryOutcome.NeedsSettings, prepared.Message);
        Assert.False(prepared.RequiredEnabled);
        var done = fresh.CompleteHostingRecovery(true, false);

        Assert.True(done.Outcome == HostingRecoveryOutcome.Completed, done.Message);
        Assert.Equal("world", File.ReadAllText(Path.Combine(context.Live, "save.sv")));
        Assert.False(context.Linker.TryGetTarget(context.Live, out _));
        Assert.True(File.Exists(Path.Combine(group.DirectoryPath, "save.sv")));
        Assert.False(File.Exists(journal));
    }

    [Fact]
    public async Task Recover_VerifiedStaging_CanRestoreHostingInsteadOfInstalling()
    {
        using var context = new SaveGroupTestContext();
        var group = await CreateEnabledGroupAsync(context);
        context.Linker.RemoveJunction(context.Live, out _);
        var staging = context.Temp.CreateDirectory("staging", "verified");
        CopyDirectory(group.DirectoryPath, staging);
        var journal = WriteJournal(context, new HostingJournalRecord
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
            SourcePath = group.DirectoryPath,
            StagingPath = staging,
            Tree = TreeOf(staging)
        });

        var fresh = context.BuildService();
        var prepared = fresh.PrepareHostingRecovery(HostingRecoveryAction.RestoreHosting);
        Assert.True(prepared.Outcome == HostingRecoveryOutcome.NeedsSettings, prepared.Message);
        Assert.True(prepared.RequiredEnabled);
        var done = fresh.CompleteHostingRecovery(true, true);

        Assert.True(done.Outcome == HostingRecoveryOutcome.Completed, done.Message);
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(group.DirectoryPath, target);
        Assert.False(File.Exists(journal));
    }

    [Fact]
    public async Task Recover_EnableImportAfterBackup_RestoresOriginalDirectoryOnDisable()
    {
        using var context = new SaveGroupTestContext();
        var group = await CreateEnabledGroupAsync(context);
        context.Linker.RemoveJunction(context.Live, out _);
        var backup = context.Temp.CreateDirectory("backups", "live-backup");
        File.WriteAllText(Path.Combine(backup, "save.sv"), "world");
        var journal = WriteJournal(context, new HostingJournalRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Operation = "enable-import",
            Phase = "backup-live",
            InstallationKey = context.InstallationKey,
            GroupId = group.Id,
            LivePath = context.Live,
            LivePreviousKind = "Data",
            PreviousSelectionGroupId = group.Id,
            SourcePath = context.Live,
            BackupPath = backup,
            TargetGroupPath = group.DirectoryPath,
            Tree = TreeOf(group.DirectoryPath)
        });

        var fresh = context.BuildService();
        var prepared = fresh.PrepareHostingRecovery(HostingRecoveryAction.DisableHosting);
        Assert.True(prepared.Outcome == HostingRecoveryOutcome.NeedsSettings, prepared.Message);
        Assert.False(prepared.RequiredEnabled);
        var done = fresh.CompleteHostingRecovery(true, false);

        Assert.True(done.Outcome == HostingRecoveryOutcome.Completed, done.Message);
        Assert.Equal("world", File.ReadAllText(Path.Combine(context.Live, "save.sv")));
        Assert.False(context.Linker.TryGetTarget(context.Live, out _));
        Assert.False(File.Exists(journal));
    }

    [Fact]
    public async Task Recover_EnableImportAfterBackup_CanFinishEnabling()
    {
        using var context = new SaveGroupTestContext();
        var group = await CreateEnabledGroupAsync(context);
        context.Linker.RemoveJunction(context.Live, out _);
        var backup = context.Temp.CreateDirectory("backups", "live-backup-2");
        File.WriteAllText(Path.Combine(backup, "save.sv"), "original");
        var journal = WriteJournal(context, new HostingJournalRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Operation = "enable-import",
            Phase = "backup-live",
            InstallationKey = context.InstallationKey,
            GroupId = group.Id,
            LivePath = context.Live,
            LivePreviousKind = "Data",
            SourcePath = context.Live,
            BackupPath = backup,
            TargetGroupPath = group.DirectoryPath,
            Tree = TreeOf(group.DirectoryPath)
        });

        var fresh = context.BuildService();
        var prepared = fresh.PrepareHostingRecovery(HostingRecoveryAction.RestoreHosting);
        Assert.True(prepared.Outcome == HostingRecoveryOutcome.NeedsSettings, prepared.Message);
        Assert.True(prepared.RequiredEnabled);
        var done = fresh.CompleteHostingRecovery(true, true);

        Assert.True(done.Outcome == HostingRecoveryOutcome.Completed, done.Message);
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(group.DirectoryPath, target);
        Assert.False(File.Exists(journal));
    }

    [Fact]
    public async Task Recover_OverwriteStagedTarget_RestoresOldTargetAndLiveBackup()
    {
        using var context = new SaveGroupTestContext();
        var group = await CreateEnabledGroupAsync(context);
        context.Linker.RemoveJunction(context.Live, out _);
        var old = Path.Combine(Path.GetDirectoryName(group.DirectoryPath)!, ".mk-old-test");
        Directory.CreateDirectory(old);
        CopyDirectory(group.DirectoryPath, old);
        var oldContent = File.ReadAllText(Path.Combine(group.DirectoryPath, "save.sv"));
        File.WriteAllText(Path.Combine(group.DirectoryPath, "save.sv"), "new-content");
        var backup = context.Temp.CreateDirectory("backups", "live-before-overwrite");
        File.WriteAllText(Path.Combine(backup, "save.sv"), "new-content");
        var journal = WriteJournal(context, new HostingJournalRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Operation = "enable-overwrite",
            Phase = "backup-live",
            InstallationKey = context.InstallationKey,
            GroupId = group.Id,
            LivePath = context.Live,
            LivePreviousKind = "Data",
            LivePreviousTarget = group.DirectoryPath,
            PreviousSelectionGroupId = group.Id,
            SourcePath = context.Live,
            RollbackPath = old,
            BackupPath = backup,
            TargetGroupPath = group.DirectoryPath,
            Tree = TreeOf(group.DirectoryPath)
        });

        var fresh = context.BuildService();
        var prepared = fresh.PrepareHostingRecovery(HostingRecoveryAction.DisableHosting);
        Assert.True(prepared.Outcome == HostingRecoveryOutcome.NeedsSettings, prepared.Message);
        var done = fresh.CompleteHostingRecovery(true, false);

        Assert.True(done.Outcome == HostingRecoveryOutcome.Completed, done.Message);
        Assert.Equal(oldContent, File.ReadAllText(Path.Combine(group.DirectoryPath, "save.sv")));
        Assert.Equal("new-content", File.ReadAllText(Path.Combine(context.Live, "save.sv")));
        Assert.False(File.Exists(journal));
    }

    [Fact]
    public async Task Recover_VerifiedStagingWithLinkStillPresent_FinishesDisable()
    {
        using var context = new SaveGroupTestContext();
        var group = await CreateEnabledGroupAsync(context);
        var staging = context.Temp.CreateDirectory("staging", "verified-link");
        CopyDirectory(group.DirectoryPath, staging);
        WriteJournal(context, new HostingJournalRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Operation = "disable",
            Phase = "verify-source",
            InstallationKey = context.InstallationKey,
            GroupId = group.Id,
            LivePath = context.Live,
            LivePreviousKind = "MkGroupLink",
            LivePreviousTarget = group.DirectoryPath,
            PreviousSelectionGroupId = group.Id,
            SourcePath = group.DirectoryPath,
            StagingPath = staging,
            Tree = TreeOf(staging)
        });

        var fresh = context.BuildService();
        var prepared = fresh.PrepareHostingRecovery(HostingRecoveryAction.DisableHosting);
        Assert.True(prepared.Outcome == HostingRecoveryOutcome.NeedsSettings, prepared.Message);
        var done = fresh.CompleteHostingRecovery(true, false);

        Assert.True(done.Outcome == HostingRecoveryOutcome.Completed, done.Message);
        Assert.False(context.Linker.TryGetTarget(context.Live, out _));
        Assert.Equal("world", File.ReadAllText(Path.Combine(context.Live, "save.sv")));
    }

    [Fact]
    public async Task Recover_WhenSettingsFail_CompensatesAndKeepsJournal()
    {
        using var context = new SaveGroupTestContext();
        var group = await CreateEnabledGroupAsync(context);
        var staging = context.Temp.CreateDirectory("staging", "compensate");
        CopyDirectory(group.DirectoryPath, staging);
        var journal = WriteJournal(context, new HostingJournalRecord
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
            SourcePath = group.DirectoryPath,
            StagingPath = staging,
            Tree = TreeOf(staging)
        });
        context.Linker.RemoveJunction(context.Live, out _);

        var fresh = context.BuildService();
        var prepared = fresh.PrepareHostingRecovery(HostingRecoveryAction.DisableHosting);
        Assert.True(prepared.Outcome == HostingRecoveryOutcome.NeedsSettings, prepared.Message);
        var failed = fresh.CompleteHostingRecovery(false, false);

        Assert.NotEqual(HostingRecoveryOutcome.Completed, failed.Outcome);
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(group.DirectoryPath, target);
        Assert.True(File.Exists(journal));
        Assert.True(Directory.Exists(staging));
    }

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
        File.WriteAllText(path, JsonSerializer.Serialize(record, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));
        return path;
    }

    private static HostingJournalTree TreeOf(string root)
    {
        var files = new List<HostingJournalFile>();
        var directories = new List<string>();
        Walk(root, string.Empty);
        files.Sort((left, right) =>
            string.CompareOrdinal(left.RelativePath, right.RelativePath));
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
                    rel, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
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

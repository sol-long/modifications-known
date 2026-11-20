using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Saves;
using ModificationsKnown.Infrastructure.Saves;
using System.Text.Json;

namespace ModificationsKnown.Tests;

/// <summary>
/// The global hosting switch: disabling turns the live junction into a real
/// directory that keeps the current group's content; enabling takes the game
/// folder back (new group, import or overwrite). These tests assert file trees,
/// link targets, catalog selection and the committed state, not just messages.
/// </summary>
public sealed class SaveGroupHostingTests
{
    [Fact]
    public async Task Disable_WhenLiveIsMkGroup_CopiesTreeAndKeepsGroup()
    {
        using var context = new SaveGroupTestContext();
        var group = await InitializeAsync(context);
        SeedLive(context, group, "mp_save/save.sv", "world");
        SeedLive(context, group, "slot_9.mp/save.sv", "bundle");
        Directory.CreateDirectory(Path.Combine(context.Live, "empty-dir"));
        Directory.CreateDirectory(Path.Combine(group.DirectoryPath, "empty-dir"));
        File.WriteAllText(Path.Combine(context.Live, SaveGroupFormat.ManifestFileName), "{}");
        context.Service.SetHostingStateFromSettings(true);

        var prepared = await context.Service.PrepareDisableHostingAsync();

        Assert.True(prepared.NeedsCommit, prepared.Message);
        var done = context.Service.CompleteHostingChange(persisted: true, enabled: false);

        Assert.Equal(HostingOperationState.Disabled, done.State);
        Assert.Equal(SaveHostingState.Disabled, context.Service.HostingState);
        Assert.False(context.Linker.TryGetTarget(context.Live, out _));
        Assert.Equal("world", File.ReadAllText(Path.Combine(context.Live, "mp_save", "save.sv")));
        Assert.Equal("bundle", File.ReadAllText(Path.Combine(context.Live, "slot_9.mp", "save.sv")));
        Assert.True(Directory.Exists(Path.Combine(context.Live, "empty-dir")));
        Assert.False(File.Exists(Path.Combine(context.Live, SaveGroupFormat.ManifestFileName)));
        Assert.True(File.Exists(Path.Combine(group.DirectoryPath, "mp_save", "save.sv")));
        Assert.Equal(group.Id, context.Service.GetNextLaunchGroupId(context.Installation));
        Assert.Empty(Directory.GetFiles(context.JournalRoot));
    }

    [Fact]
    public async Task Disable_WhenGameRunning_LeavesEverythingUntouched()
    {
        using var context = new SaveGroupTestContext();
        var group = await InitializeAsync(context);
        SeedLive(context, group, "save.sv", "data");
        context.Service.SetHostingStateFromSettings(true);
        context.Running = true;

        var result = await context.Service.PrepareDisableHostingAsync();

        Assert.Equal(HostingOperationState.RunningLocked, result.State);
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(group.DirectoryPath, target);
        Assert.True(File.Exists(Path.Combine(context.Live, "save.sv")));
        Assert.Empty(Directory.GetFiles(context.JournalRoot));
    }

    [Fact]
    public async Task Disable_WhenProbeUnknown_IsFailClosed()
    {
        using var context = new SaveGroupTestContext();
        var group = await InitializeAsync(context);
        context.Service.SetHostingStateFromSettings(true);
        context.ProbeAnyState = GameRunningState.Unknown;

        var result = await context.Service.PrepareDisableHostingAsync();

        Assert.Equal(HostingOperationState.RunningLocked, result.State);
        Assert.Contains("无法确认", result.Message);
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(group.DirectoryPath, target);
    }

    [Fact]
    public async Task Disable_WhenPrepared_ButSettingsFail_RollsBackToJunction()
    {
        using var context = new SaveGroupTestContext();
        var group = await InitializeAsync(context);
        SeedLive(context, group, "save.sv", "data");
        context.Service.SetHostingStateFromSettings(true);

        var prepared = await context.Service.PrepareDisableHostingAsync();
        Assert.True(prepared.NeedsCommit, prepared.Message);

        var failed = context.Service.CompleteHostingChange(persisted: false, enabled: false);

        Assert.Equal(HostingOperationState.Failed, failed.State);
        Assert.Equal(SaveHostingState.Enabled, context.Service.HostingState);
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(group.DirectoryPath, target);
        Assert.Empty(Directory.GetFiles(context.JournalRoot));
    }

    [Fact]
    public async Task Disable_WhenLiveIsExternalLink_KeepsTheLinkAndOnlySwitchesMode()
    {
        using var context = new SaveGroupTestContext();
        await InitializeAsync(context);
        var external = context.Temp.CreateDirectory("external");
        File.WriteAllText(Path.Combine(external, "save.sv"), "outside");
        context.Linker.RemoveJunction(context.Live, out _);
        context.Linker.CreateJunction(context.Live, external, out _);
        context.Service.SetHostingStateFromSettings(true);

        var prepared = await context.Service.PrepareDisableHostingAsync();
        Assert.True(prepared.NeedsCommit, prepared.Message);
        var done = context.Service.CompleteHostingChange(persisted: true, enabled: false);

        Assert.Equal(HostingOperationState.Disabled, done.State);
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(Path.GetFullPath(external), Path.GetFullPath(target));
        Assert.Equal("outside", File.ReadAllText(Path.Combine(external, "save.sv")));
        Assert.DoesNotContain("放回游戏目录", done.Message);
    }

    [Fact]
    public async Task Disable_WhenLiveIsARealDirectory_DoesNotCopyOrDelete()
    {
        using var context = new SaveGroupTestContext();
        await InitializeAsync(context);
        context.Linker.RemoveJunction(context.Live, out _);
        Directory.CreateDirectory(context.Live);
        File.WriteAllText(Path.Combine(context.Live, "save.sv"), "unmanaged");
        context.Service.SetHostingStateFromSettings(true);

        var prepared = await context.Service.PrepareDisableHostingAsync();
        var done = context.Service.CompleteHostingChange(persisted: true, enabled: false);

        Assert.Equal(HostingOperationState.Disabled, done.State);
        Assert.Equal("unmanaged", File.ReadAllText(Path.Combine(context.Live, "save.sv")));
        Assert.Empty(Directory.GetDirectories(context.BackupsRoot));
    }

    [Fact]
    public async Task Disable_GatesAllNormalSaveGroupWrites()
    {
        using var context = new SaveGroupTestContext();
        await InitializeAsync(context);
        context.Service.SetHostingStateFromSettings(true);
        var prepared = await context.Service.PrepareDisableHostingAsync();
        context.Service.CompleteHostingChange(true, false);

        var created = await context.Service.CreateEmptyAsync(context.Installation, "新组");
        var selected = await context.Service.SelectNextLaunchAsync(
            context.Installation, Guid.NewGuid());
        var initialized = await context.Service.InitializeAsync(context.Installation);

        Assert.False(created.Success);
        Assert.Contains("托管已关闭", created.Message);
        Assert.False(selected.Success);
        Assert.False(initialized.Success);
    }

    [Fact]
    public async Task Enable_WhenLiveIsEmpty_CreatesDefaultAndOverridesOldSelection()
    {
        using var context = new SaveGroupTestContext();
        await InitializeAsync(context);
        var other = await context.Service.CreateEmptyAsync(context.Installation, "旧组");
        Assert.True(other.Success, other.Message);
        Assert.True((await context.Service.ApplyCurrentAsync(context.Installation, other.Group!.Id)).Success);

        context.Linker.RemoveJunction(context.Live, out _);
        Directory.CreateDirectory(context.Live);
        context.Service.SetHostingStateFromSettings(false);

        var prepared = await context.Service.PrepareEnableHostingAsync(context.Installation);
        Assert.True(prepared.NeedsCommit, prepared.Message);
        var done = context.Service.CompleteHostingChange(persisted: true, enabled: true);

        Assert.Equal(HostingOperationState.Enabled, done.State);
        Assert.Equal(SaveHostingState.Enabled, context.Service.HostingState);
        Assert.NotNull(done.Group);
        Assert.Equal("默认", done.Group!.Name);
        Assert.Equal(done.Group.Id, context.Service.GetNextLaunchGroupId(context.Installation));
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(done.Group.DirectoryPath, target);
    }

    [Fact]
    public async Task Enable_Import_WithDuplicateName_GetsAutoNumbered()
    {
        using var context = new SaveGroupTestContext();
        await InitializeAsync(context);
        var existing = await context.Service.CreateEmptyAsync(context.Installation, "导入的存档");
        Assert.True(existing.Success, existing.Message);
        context.Linker.RemoveJunction(context.Live, out _);
        Directory.CreateDirectory(context.Live);
        File.WriteAllText(Path.Combine(context.Live, "save.sv"), "data");
        context.Service.SetHostingStateFromSettings(false);

        var prepared = await context.Service.PrepareEnableHostingAsync(
            context.Installation,
            new HostingEnableRequest(HostingEnableChoice.ImportIntoNewGroup, "导入的存档"));
        var done = context.Service.CompleteHostingChange(true, true);

        Assert.Equal(HostingOperationState.Enabled, done.State);
        Assert.Equal("导入的存档 2", done.Group?.Name);
    }

    [Fact]
    public async Task Enable_WhenLiveHasData_ReturnsNeedsChoiceWithoutWriting()
    {
        using var context = new SaveGroupTestContext();
        await InitializeAsync(context);
        context.Linker.RemoveJunction(context.Live, out _);
        Directory.CreateDirectory(context.Live);
        File.WriteAllText(Path.Combine(context.Live, "save.sv"), "unmanaged");
        context.Service.SetHostingStateFromSettings(false);

        var result = await context.Service.PrepareEnableHostingAsync(context.Installation);

        Assert.Equal(HostingOperationState.NeedsChoice, result.State);
        Assert.Equal(1, result.FileCount);
        Assert.Equal("unmanaged", File.ReadAllText(Path.Combine(context.Live, "save.sv")));
        Assert.False(context.Linker.TryGetTarget(context.Live, out _));
        Assert.Empty(Directory.GetFiles(context.JournalRoot));
    }

    [Fact]
    public async Task Enable_Import_CreatesNamedGroupAndBacksUpTheOriginal()
    {
        using var context = new SaveGroupTestContext();
        var first = await InitializeAsync(context);
        context.Linker.RemoveJunction(context.Live, out _);
        Directory.CreateDirectory(Path.Combine(context.Live, "mp_save"));
        Directory.CreateDirectory(Path.Combine(context.Live, "empty-dir"));
        File.WriteAllText(Path.Combine(context.Live, "mp_save", "save.sv"), "imported");
        context.Service.SetHostingStateFromSettings(false);

        var prepared = await context.Service.PrepareEnableHostingAsync(
            context.Installation,
            new HostingEnableRequest(HostingEnableChoice.ImportIntoNewGroup, "我的存档"));
        Assert.True(prepared.NeedsCommit, prepared.Message);
        var done = context.Service.CompleteHostingChange(true, true);

        Assert.Equal(HostingOperationState.Enabled, done.State);
        Assert.Equal("我的存档", done.Group!.Name);
        Assert.True(File.Exists(Path.Combine(done.Group.DirectoryPath, "mp_save", "save.sv")));
        Assert.True(Directory.Exists(Path.Combine(done.Group.DirectoryPath, "empty-dir")));
        Assert.Equal(done.Group.Id, context.Service.GetNextLaunchGroupId(context.Installation));
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(done.Group.DirectoryPath, target);
        Assert.Contains(
            Directory.GetDirectories(context.BackupsRoot),
            dir => File.Exists(Path.Combine(dir, "mp_save", "save.sv")));
        Assert.NotEqual(first.Id, done.Group.Id);
    }

    [Fact]
    public async Task Enable_Overwrite_PreservesTargetIdentityAndKeepsBackups()
    {
        using var context = new SaveGroupTestContext();
        var first = await InitializeAsync(context);
        var target = await context.Service.CreateEmptyAsync(context.Installation, "目标组");
        Assert.True(target.Success, target.Message);
        File.WriteAllText(Path.Combine(target.Group!.DirectoryPath, "old.sv"), "old");
        SaveGroupManifestStore.TryRead(target.Group.DirectoryPath, out var before);

        context.Linker.RemoveJunction(context.Live, out _);
        Directory.CreateDirectory(context.Live);
        File.WriteAllText(Path.Combine(context.Live, "new.sv"), "new");
        context.Service.SetHostingStateFromSettings(false);

        var prepared = await context.Service.PrepareEnableHostingAsync(
            context.Installation,
            new HostingEnableRequest(
                HostingEnableChoice.OverwriteExistingGroup,
                TargetGroupId: target.Group.Id,
                BackupTargetGroup: true));
        Assert.True(prepared.NeedsCommit, prepared.Message);
        var done = context.Service.CompleteHostingChange(true, true);

        Assert.Equal(HostingOperationState.Enabled, done.State);
        Assert.Equal("new", File.ReadAllText(Path.Combine(target.Group.DirectoryPath, "new.sv")));
        Assert.False(File.Exists(Path.Combine(target.Group.DirectoryPath, "old.sv")));
        Assert.True(SaveGroupManifestStore.TryRead(target.Group.DirectoryPath, out var after));
        Assert.Equal(before.GroupId, after.GroupId);
        Assert.Equal(before.Name, after.Name);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
        Assert.Equal(before.InstallationKey, after.InstallationKey);
        Assert.Equal(target.Group.Id, context.Service.GetNextLaunchGroupId(context.Installation));
        Assert.True(context.Linker.TryGetTarget(context.Live, out var liveTarget));
        Assert.Equal(target.Group.DirectoryPath, liveTarget);
        Assert.Contains(
            Directory.GetDirectories(context.BackupsRoot),
            dir => File.Exists(Path.Combine(dir, "old.sv")));
        Assert.Contains(
            Directory.GetDirectories(context.BackupsRoot),
            dir => File.Exists(Path.Combine(dir, "new.sv")));
        Assert.NotEqual(first.Id, target.Group.Id);
    }

    [Fact]
    public async Task Enable_Overwrite_WithoutLongTermBackup_StillCommits()
    {
        using var context = new SaveGroupTestContext();
        await InitializeAsync(context);
        var target = await context.Service.CreateEmptyAsync(context.Installation, "目标组");
        File.WriteAllText(Path.Combine(target.Group!.DirectoryPath, "old.sv"), "old");
        context.Linker.RemoveJunction(context.Live, out _);
        Directory.CreateDirectory(context.Live);
        File.WriteAllText(Path.Combine(context.Live, "new.sv"), "new");
        context.Service.SetHostingStateFromSettings(false);

        var prepared = await context.Service.PrepareEnableHostingAsync(
            context.Installation,
            new HostingEnableRequest(
                HostingEnableChoice.OverwriteExistingGroup,
                TargetGroupId: target.Group.Id,
                BackupTargetGroup: false));
        var done = context.Service.CompleteHostingChange(true, true);

        Assert.Equal(HostingOperationState.Enabled, done.State);
        Assert.False(File.Exists(Path.Combine(target.Group.DirectoryPath, "old.sv")));
        Assert.DoesNotContain(
            Directory.GetDirectories(context.BackupsRoot),
            dir => File.Exists(Path.Combine(dir, "old.sv")));
        Assert.Contains(
            Directory.GetDirectories(context.BackupsRoot),
            dir => File.Exists(Path.Combine(dir, "new.sv")));
    }

    [Fact]
    public async Task Enable_Overwrite_WithUnknownTarget_FailsWithoutChanges()
    {
        using var context = new SaveGroupTestContext();
        await InitializeAsync(context);
        context.Linker.RemoveJunction(context.Live, out _);
        Directory.CreateDirectory(context.Live);
        File.WriteAllText(Path.Combine(context.Live, "new.sv"), "new");
        context.Service.SetHostingStateFromSettings(false);

        var result = await context.Service.PrepareEnableHostingAsync(
            context.Installation,
            new HostingEnableRequest(
                HostingEnableChoice.OverwriteExistingGroup,
                TargetGroupId: Guid.NewGuid()));

        Assert.Equal(HostingOperationState.Failed, result.State);
        Assert.Equal("new", File.ReadAllText(Path.Combine(context.Live, "new.sv")));
        Assert.False(context.Linker.TryGetTarget(context.Live, out _));
    }

    [Fact]
    public async Task Enable_Overwrite_WhenSettingsFail_RestoresTargetAndLive()
    {
        using var context = new SaveGroupTestContext();
        await InitializeAsync(context);
        var target = await context.Service.CreateEmptyAsync(context.Installation, "目标组");
        File.WriteAllText(Path.Combine(target.Group!.DirectoryPath, "old.sv"), "old");
        context.Linker.RemoveJunction(context.Live, out _);
        Directory.CreateDirectory(context.Live);
        File.WriteAllText(Path.Combine(context.Live, "new.sv"), "new");
        context.Service.SetHostingStateFromSettings(false);

        var prepared = await context.Service.PrepareEnableHostingAsync(
            context.Installation,
            new HostingEnableRequest(
                HostingEnableChoice.OverwriteExistingGroup,
                TargetGroupId: target.Group.Id,
                BackupTargetGroup: true));
        Assert.True(prepared.NeedsCommit, prepared.Message);

        var failed = context.Service.CompleteHostingChange(persisted: false, enabled: true);

        Assert.Equal(HostingOperationState.Failed, failed.State);
        Assert.Equal(SaveHostingState.Disabled, context.Service.HostingState);
        Assert.Equal("new", File.ReadAllText(Path.Combine(context.Live, "new.sv")));
        Assert.False(context.Linker.TryGetTarget(context.Live, out _));
        Assert.Equal("old", File.ReadAllText(Path.Combine(target.Group.DirectoryPath, "old.sv")));
        Assert.False(File.Exists(Path.Combine(target.Group.DirectoryPath, "new.sv")));
        Assert.Empty(Directory.GetFiles(context.JournalRoot));
    }

    [Fact]
    public async Task Enable_WhenLiveIsExternalLink_RefusesAndKeepsIt()
    {
        using var context = new SaveGroupTestContext();
        await InitializeAsync(context);
        var external = context.Temp.CreateDirectory("external");
        File.WriteAllText(Path.Combine(external, "save.sv"), "outside");
        context.Linker.RemoveJunction(context.Live, out _);
        context.Linker.CreateJunction(context.Live, external, out _);
        context.Service.SetHostingStateFromSettings(false);

        var result = await context.Service.PrepareEnableHostingAsync(context.Installation);

        Assert.Equal(HostingOperationState.Failed, result.State);
        Assert.Contains("MK 之外", result.Message);
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(Path.GetFullPath(external), Path.GetFullPath(target));
        Assert.Equal("outside", File.ReadAllText(Path.Combine(external, "save.sv")));
    }

    [Fact]
    public async Task Enable_WhenSettingsFail_RollsBackToDisabledState()
    {
        using var context = new SaveGroupTestContext();
        await InitializeAsync(context);
        context.Linker.RemoveJunction(context.Live, out _);
        Directory.CreateDirectory(context.Live);
        File.WriteAllText(Path.Combine(context.Live, "save.sv"), "unmanaged");
        context.Service.SetHostingStateFromSettings(false);

        var prepared = await context.Service.PrepareEnableHostingAsync(
            context.Installation,
            new HostingEnableRequest(HostingEnableChoice.ImportIntoNewGroup, "导入"));
        Assert.True(prepared.NeedsCommit, prepared.Message);

        var failed = context.Service.CompleteHostingChange(persisted: false, enabled: true);

        Assert.Equal(HostingOperationState.Failed, failed.State);
        Assert.Equal(SaveHostingState.Disabled, context.Service.HostingState);
        Assert.Equal("unmanaged", File.ReadAllText(Path.Combine(context.Live, "save.sv")));
        Assert.False(context.Linker.TryGetTarget(context.Live, out _));
    }

    [Fact]
    public async Task Recovery_LeftoverJournal_BlocksWritesAndLaunch()
    {
        using var context = new SaveGroupTestContext();
        await InitializeAsync(context);
        var journal = Path.Combine(context.JournalRoot, "tx.json");
        File.WriteAllText(journal, "{\"schema\":1,\"operation\":\"disable\",\"phase\":\"rename-live\"}");

        var found = context.Service.ScanHostingRecovery();

        Assert.True(found);
        Assert.Equal(SaveHostingState.PendingRecovery, context.Service.HostingState);
        Assert.Single(context.Service.GetHostingRecoveryInfo());
        var launched = await context.Service.PrepareLaunchAsync(context.Installation);
        Assert.False(launched.Success);
        Assert.Contains("恢复", launched.Message);
        File.Delete(journal);
    }

    [Fact]
    public async Task Disable_WhenLiveIsDanglingLink_RemovesItAndDisables()
    {
        using var context = new SaveGroupTestContext();
        var gone = Path.Combine(context.Temp.Path, "gone-target");
        Directory.CreateDirectory(gone);
        context.Linker.CreateJunction(context.Live, gone, out _);
        Directory.Delete(gone, recursive: true);
        context.Service.SetHostingStateFromSettings(true);
        Assert.Equal(HostingLiveKind.DanglingLink, context.Service.DescribeLive().Kind);

        var prepared = await context.Service.PrepareDisableHostingAsync();
        Assert.True(prepared.NeedsCommit, prepared.Message);
        var done = context.Service.CompleteHostingChange(true, false);

        Assert.Equal(HostingOperationState.Disabled, done.State);
        Assert.False(context.Linker.TryGetTarget(context.Live, out _));
    }

    [Fact]
    public async Task Enable_WhenLiveIsDanglingLink_ReplacesItWithDefaultGroup()
    {
        using var context = new SaveGroupTestContext();
        var gone = Path.Combine(context.Temp.Path, "gone-target");
        Directory.CreateDirectory(gone);
        context.Linker.CreateJunction(context.Live, gone, out _);
        Directory.Delete(gone, recursive: true);
        context.Service.SetHostingStateFromSettings(false);

        var prepared = await context.Service.PrepareEnableHostingAsync(context.Installation);
        Assert.True(prepared.NeedsCommit, prepared.Message);
        var done = context.Service.CompleteHostingChange(true, true);

        Assert.Equal(HostingOperationState.Enabled, done.State);
        Assert.Equal("默认", done.Group?.Name);
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(done.Group!.DirectoryPath, target);
    }

    [Fact]
    public void Recovery_LegacyJournalWithoutProof_IsRefusedAndKept()
    {
        using var context = new SaveGroupTestContext();
        context.Linker.RemoveJunction(context.Live, out _);
        var staging = context.Temp.CreateDirectory("staging", "interrupted");
        File.WriteAllText(Path.Combine(staging, "save.sv"), "recovered");
        var journal = Path.Combine(context.JournalRoot, "interrupted.json");
        File.WriteAllText(journal, JsonSerializer.Serialize(new
        {
            schema = 1,
            operation = "disable",
            phase = "install-live",
            livePath = context.Live,
            stagingPath = staging
        }));
        Assert.True(context.Service.ScanHostingRecovery());

        var prepared = context.Service.PrepareHostingRecovery(HostingRecoveryAction.DisableHosting);

        Assert.Equal(HostingRecoveryOutcome.Refused, prepared.Outcome);
        Assert.False(Directory.Exists(context.Live));
        Assert.True(File.Exists(Path.Combine(staging, "save.sv")));
        Assert.True(File.Exists(journal));
    }

    [Fact]
    public async Task ArchiveImport_WhenHostingDisabled_IsRejectedBeforeReadingTheArchive()
    {
        using var context = new SaveGroupTestContext();
        context.Service.SetHostingStateFromSettings(false);
        var archive = new SaveGroupArchiveService(
            isGameRunning: () => false,
            coordinator: context.Service);

        var result = await archive.ImportAsync(
            Path.Combine(context.Temp.Path, "missing.zip"),
            context.Installation,
            context.Service,
            "导入");

        Assert.False(result.Success);
        Assert.Contains("托管已关闭", result.Message);
    }

    [Fact]
    public async Task Disable_WhenLiveIsLegacyProfileLink_CopiesOutAndKeepsLegacyData()
    {
        using var context = new SaveGroupTestContext();
        var legacy = context.Temp.CreateDirectory("profiles", context.InstallationKey);
        File.WriteAllText(Path.Combine(legacy, "save.sv"), "legacy");
        context.Linker.RemoveJunction(context.Live, out _);
        context.Linker.CreateJunction(context.Live, legacy, out _);
        // The fake linker is a plain folder; mirror what a real junction maps.
        File.WriteAllText(Path.Combine(context.Live, "save.sv"), "legacy");
        context.Service.SetHostingStateFromSettings(true);

        var info = context.Service.DescribeLive();
        Assert.Equal(HostingLiveKind.MkProfileLink, info.Kind);

        var prepared = await context.Service.PrepareDisableHostingAsync();
        Assert.True(prepared.NeedsCommit, prepared.Message);
        var done = context.Service.CompleteHostingChange(true, false);

        Assert.Equal(HostingOperationState.Disabled, done.State);
        Assert.Equal("legacy", File.ReadAllText(Path.Combine(context.Live, "save.sv")));
        Assert.False(context.Linker.TryGetTarget(context.Live, out _));
        Assert.Equal("legacy", File.ReadAllText(Path.Combine(legacy, "save.sv")));
    }

    [Fact]
    public void DescribeLive_ClassifiesExternalDanglingAndMissing()
    {
        using var context = new SaveGroupTestContext();
        Assert.Equal(HostingLiveKind.Missing, context.Service.DescribeLive().Kind);

        context.CreateLiveDirectory();
        Assert.Equal(HostingLiveKind.EmptyDirectory, context.Service.DescribeLive().Kind);

        File.WriteAllText(Path.Combine(context.Live, "save.sv"), "x");
        Assert.Equal(HostingLiveKind.Data, context.Service.DescribeLive().Kind);

        var external = context.Temp.CreateDirectory("external-classify");
        File.WriteAllText(Path.Combine(external, "save.sv"), "x");
        context.Linker.SetTarget(context.Live, external);
        Assert.Equal(HostingLiveKind.ExternalLink, context.Service.DescribeLive().Kind);

        var gone = Path.Combine(context.Temp.Path, "gone");
        context.Linker.SetTarget(context.Live, gone);
        Assert.Equal(HostingLiveKind.DanglingLink, context.Service.DescribeLive().Kind);
    }

    private static async Task<SaveGroup> InitializeAsync(SaveGroupTestContext context)
    {
        var init = await context.Service.InitializeAsync(context.Installation);
        Assert.True(init.Success, init.Message);
        return init.Groups.Single();
    }

    /// <summary>The fake linker does not map link contents, so a test that wants
    /// “the group the junction points at” writes the same file on both sides.</summary>
    private static void SeedLive(SaveGroupTestContext context, SaveGroup group, string relative, string content)
    {
        foreach (var root in new[] { context.Live, group.DirectoryPath })
        {
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
    }
}

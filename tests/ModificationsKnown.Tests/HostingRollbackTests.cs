using ModificationsKnown.Core.Saves;
using ModificationsKnown.Infrastructure.Saves;

namespace ModificationsKnown.Tests;

public sealed class HostingRollbackTests
{
    [Fact]
    public async Task Import_WhenSettingsFailAndSelectionRestoreFails_ReturnsFailedUnknown()
    {
        using var context = new SaveGroupTestContext();
        context.CreateLiveDirectory();
        File.WriteAllText(Path.Combine(context.Live, "save.sv"), "original");
        context.Service.SetHostingStateFromSettings(false);
        var prepared = await context.Service.PrepareEnableHostingAsync(
            context.Installation,
            new HostingEnableRequest(HostingEnableChoice.ImportIntoNewGroup, "g1"));
        Assert.True(prepared.NeedsCommit, prepared.Message);
        var journal = Directory.GetFiles(context.JournalRoot).Single();

        // First catalog save succeeded during prepare; now make the restore
        // selection fail, and make removing the new link fail too.
        File.Delete(context.CatalogPath);
        Directory.CreateDirectory(context.CatalogPath);
        context.Linker.FailRemove = true;

        var result = context.Service.CompleteHostingChange(false, true);

        Assert.Equal(HostingOperationState.FailedUnknown, result.State);
        Assert.Contains("未完整恢复", result.Message);
        Assert.True(File.Exists(journal));
        Assert.Equal("original", File.ReadAllText(Path.Combine(context.Live, "save.sv")));
        Assert.True(context.Service.GetHostingJournalScan()
            .Status == HostingJournalScanStatus.Valid);
    }

    [Fact]
    public async Task Import_WhenFirstCatalogSaveFailsAndRemoveFails_KeepsJournal()
    {
        using var context = new SaveGroupTestContext();
        var init = await context.Service.InitializeAsync(context.Installation);
        Assert.True(init.Success, init.Message);
        context.Linker.RemoveJunction(context.Live, out _);
        context.CreateLiveDirectory();
        File.WriteAllText(Path.Combine(context.Live, "save.sv"), "original");
        context.Service.SetHostingStateFromSettings(false);
        File.SetAttributes(context.CatalogPath, FileAttributes.ReadOnly);
        context.Linker.FailRemove = true;

        HostingOperationResult prepared;
        try
        {
            prepared = await context.Service.PrepareEnableHostingAsync(
                context.Installation,
                new HostingEnableRequest(HostingEnableChoice.ImportIntoNewGroup, "g1"));
        }
        finally
        {
            File.SetAttributes(context.CatalogPath, FileAttributes.Normal);
        }

        Assert.Equal(HostingOperationState.FailedUnknown, prepared.State);
        Assert.Contains("未完整恢复", prepared.Message);
        Assert.Contains("移除新链接失败", prepared.Message);
        Assert.Single(Directory.GetFiles(context.JournalRoot));
        Assert.Equal("original", File.ReadAllText(Path.Combine(context.Live, "save.sv")));
    }

    [Fact]
    public async Task Import_WhenOriginalBackupIsMissing_ReturnsFailedUnknownAndKeepsJournal()
    {
        using var context = new SaveGroupTestContext();
        context.CreateLiveDirectory();
        File.WriteAllText(Path.Combine(context.Live, "save.sv"), "original");
        context.Service.SetHostingStateFromSettings(false);
        var prepared = await context.Service.PrepareEnableHostingAsync(
            context.Installation,
            new HostingEnableRequest(HostingEnableChoice.ImportIntoNewGroup, "g1"));
        Assert.True(prepared.NeedsCommit, prepared.Message);
        var journal = Directory.GetFiles(context.JournalRoot).Single();
        foreach (var directory in Directory.GetDirectories(context.BackupsRoot))
            Directory.Delete(directory, recursive: true);

        var result = context.Service.CompleteHostingChange(false, true);

        Assert.Equal(HostingOperationState.FailedUnknown, result.State);
        Assert.True(File.Exists(journal));
        Assert.False(Directory.Exists(Path.Combine(context.Live, "save.sv")));
    }

    [Fact]
    public async Task Overwrite_WhenRollbackCopyIsMissing_DoesNotClaimSuccess()
    {
        using var context = new SaveGroupTestContext();
        var init = await context.Service.InitializeAsync(context.Installation);
        var target = init.Groups.Single();
        context.Linker.RemoveJunction(context.Live, out _);
        context.CreateLiveDirectory();
        File.WriteAllText(Path.Combine(context.Live, "save.sv"), "new-live");
        context.Service.SetHostingStateFromSettings(false);
        var prepared = await context.Service.PrepareEnableHostingAsync(
            context.Installation,
            new HostingEnableRequest(
                HostingEnableChoice.OverwriteExistingGroup,
                TargetGroupId: target.Id));
        Assert.True(prepared.NeedsCommit, prepared.Message);
        var journal = Directory.GetFiles(context.JournalRoot).Single();
        var parent = Path.GetDirectoryName(target.DirectoryPath)!;
        foreach (var rollback in Directory.GetDirectories(parent, ".mk-old-*"))
            Directory.Delete(rollback, recursive: true);

        var result = context.Service.CompleteHostingChange(false, true);

        Assert.Equal(HostingOperationState.FailedUnknown, result.State);
        Assert.Contains("未完整恢复", result.Message);
        Assert.True(File.Exists(journal));
        Assert.Equal("new-live", File.ReadAllText(Path.Combine(context.Live, "save.sv")));
        Assert.True(File.Exists(Path.Combine(target.DirectoryPath, "save.sv")));
    }
    [Fact]
    public async Task Disable_WhenInstalledCopyGainsNewData_DoesNotDeleteItOrClaimRollback()
    {
        using var context = new SaveGroupTestContext();
        await CreateRealLiveGroupAsync(context);
        context.HostingFaultHook = stage =>
        {
            if (stage == "disable-after-install")
            {
                File.WriteAllText(Path.Combine(context.Live, "new-after-install.txt"), "new");
                throw new IOException("simulated post-install failure");
            }
        };

        var service = context.BuildService();
        var prepared = await service.PrepareDisableHostingAsync();

        Assert.Equal(HostingOperationState.FailedUnknown, prepared.State);
        Assert.Equal("new", File.ReadAllText(Path.Combine(context.Live, "new-after-install.txt")));
        Assert.Single(Directory.GetFiles(context.JournalRoot));
        Assert.Equal(SaveHostingState.PendingRecovery, service.HostingState);
        Assert.Equal(
            HostingOperationState.PendingRecovery,
            service.PrepareDisableHosting().State);
    }

    [Fact]
    public async Task Disable_WhenLinkReadBackFails_KeepsJournalAndPending()
    {
        using var context = new SaveGroupTestContext();
        await CreateRealLiveGroupAsync(context);
        context.HostingFaultHook = stage =>
        {
            if (stage == "disable-after-install")
            {
                context.Linker.FailReadBack = true;
                throw new IOException("simulated post-install failure");
            }
        };

        var service = context.BuildService();
        var prepared = await service.PrepareDisableHostingAsync();

        Assert.Equal(HostingOperationState.FailedUnknown, prepared.State);
        Assert.Single(Directory.GetFiles(context.JournalRoot));
        Assert.Equal(SaveHostingState.PendingRecovery, service.HostingState);
        Assert.True(Directory.Exists(context.Live));
    }


    [Fact]
    public async Task Disable_WhenLiveChangesBeforeSettingsFail_DoesNotDeleteIt()
    {
        using var context = new SaveGroupTestContext();
        await CreateRealLiveGroupAsync(context);
        var prepared = await context.Service.PrepareDisableHostingAsync();
        Assert.True(prepared.NeedsCommit, prepared.Message);
        File.WriteAllText(Path.Combine(context.Live, "new-after-prepare.sv"), "new");
        var journal = Directory.GetFiles(context.JournalRoot).Single();

        var result = context.Service.CompleteHostingChange(persisted: false, enabled: false);

        Assert.Equal(HostingOperationState.FailedUnknown, result.State);
        Assert.True(File.Exists(journal));
        Assert.Equal("new", File.ReadAllText(Path.Combine(context.Live, "new-after-prepare.sv")));
        Assert.Equal(SaveHostingState.PendingRecovery, context.Service.HostingState);
    }


    [Fact]
    public async Task Disable_WhenLinkRecreationFails_KeepsJournalAndPending()
    {
        using var context = new SaveGroupTestContext();
        await CreateRealLiveGroupAsync(context);
        context.HostingFaultHook = stage =>
        {
            if (stage == "disable-after-install")
            {
                context.Linker.FailNextCreate = 1;
                throw new IOException("simulated post-install failure");
            }
        };

        var service = context.BuildService();
        var prepared = await service.PrepareDisableHostingAsync();

        Assert.Equal(HostingOperationState.FailedUnknown, prepared.State);
        Assert.Single(Directory.GetFiles(context.JournalRoot));
        Assert.Equal(SaveHostingState.PendingRecovery, service.HostingState);
        Assert.False(Directory.Exists(context.Live));
    }



    [Fact]
    public async Task Disable_WhenCleanupWouldDiscardNewLiveData_RefusesAndKeepsJournal()
    {
        using var context = new SaveGroupTestContext();
        await CreateRealLiveGroupAsync(context);
        context.HostingFaultHook = stage =>
        {
            if (stage == "disable-after-install")
            {
                File.WriteAllText(Path.Combine(context.Live, "new-live-data.sv"), "keep");
                throw new IOException("simulated post-install failure");
            }
        };

        var service = context.BuildService();
        var prepared = await service.PrepareDisableHostingAsync();

        Assert.Equal(HostingOperationState.FailedUnknown, prepared.State);
        Assert.Single(Directory.GetFiles(context.JournalRoot));
        Assert.Equal(SaveHostingState.PendingRecovery, service.HostingState);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(context.Live, "new-live-data.sv")));
    }

    private static async Task<SaveGroup> CreateRealLiveGroupAsync(SaveGroupTestContext context)
    {
        context.CreateLiveDirectory();
        File.WriteAllText(Path.Combine(context.Live, "save.sv"), "original");
        context.Service.SetHostingStateFromSettings(false);
        var prepared = await context.Service.PrepareEnableHostingAsync(
            context.Installation,
            new HostingEnableRequest(HostingEnableChoice.ImportIntoNewGroup, "g1"));
        Assert.True(prepared.NeedsCommit, prepared.Message);
        var done = context.Service.CompleteHostingChange(true, true);
        Assert.Equal(HostingOperationState.Enabled, done.State);
        return Assert.IsType<SaveGroup>(done.Group);
    }

}

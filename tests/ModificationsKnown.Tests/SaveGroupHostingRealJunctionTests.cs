using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Saves;
using ModificationsKnown.Infrastructure.Saves;

namespace ModificationsKnown.Tests;

/// <summary>
/// End-to-end hosting transaction against real NTFS junctions in a throwaway
/// root: disable must turn the link into a real folder with the same files,
/// and re-enable must put a real junction back. Never touches real LocalLow.
/// </summary>
public sealed class SaveGroupHostingRealJunctionTests
{
    [Fact]
    public async Task DisableThenImportBack_WithRealJunctions_RoundTrips()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var temp = new TempDirectory();
        var profiles = temp.CreateDirectory("profiles");
        var backups = temp.CreateDirectory("backups");
        var staging = temp.CreateDirectory("staging");
        var journal = temp.CreateDirectory("journal");
        var game = temp.CreateDirectory("game");
        var live = Path.Combine(temp.Path, "LocalLow", "Orsoniks", "CasualtiesUnknown");
        var linker = new WindowsJunctionLinker();
        var service = new SaveGroupService(new SaveGroupOptions
        {
            ProfilesRoot = profiles,
            CatalogPath = Path.Combine(temp.Path, "save-groups.json"),
            BackupsRoot = backups,
            StagingRoot = staging,
            JournalRoot = journal,
            LiveSaveDirectory = live,
            Linker = linker,
            RecycleBin = new RejectingRecycleBin(),
            ProbeGameRunningState = _ => GameRunningState.NotRunning,
            ProbeAnyGameRunningState = () => GameRunningState.NotRunning,
        });
        var installation = new GameInstallation(KnownGameProfiles.Full, game, null, false, false);

        var init = await service.InitializeAsync(installation);
        Assert.True(init.Success, init.Message);
        var group = init.Groups.Single();
        Assert.True(linker.TryGetTarget(live, out _));

        Directory.CreateDirectory(Path.Combine(live, "mp_save"));
        Directory.CreateDirectory(Path.Combine(live, "empty-dir"));
        File.WriteAllText(Path.Combine(live, "mp_save", "save.sv"), "world");
        Assert.True(File.Exists(Path.Combine(group.DirectoryPath, "mp_save", "save.sv")));

        var prepared = await service.PrepareDisableHostingAsync();
        Assert.True(prepared.NeedsCommit, prepared.Message);
        var disabled = service.CompleteHostingChange(persisted: true, enabled: false);

        Assert.Equal(HostingOperationState.Disabled, disabled.State);
        Assert.False((File.GetAttributes(live) & FileAttributes.ReparsePoint) != 0);
        Assert.Equal("world", File.ReadAllText(Path.Combine(live, "mp_save", "save.sv")));
        Assert.True(Directory.Exists(Path.Combine(live, "empty-dir")));
        Assert.True(File.Exists(Path.Combine(group.DirectoryPath, "mp_save", "save.sv")));
        Assert.Empty(Directory.GetFiles(journal));

        var reimport = await service.PrepareEnableHostingAsync(
            installation,
            new HostingEnableRequest(HostingEnableChoice.ImportIntoNewGroup, "收回的存档"));
        Assert.True(reimport.NeedsCommit, reimport.Message);
        var enabled = service.CompleteHostingChange(persisted: true, enabled: true);

        Assert.Equal(HostingOperationState.Enabled, enabled.State);
        Assert.Equal("收回的存档", enabled.Group?.Name);
        Assert.True(linker.TryGetTarget(live, out var target));
        Assert.Equal(Path.GetFullPath(enabled.Group!.DirectoryPath), Path.GetFullPath(target));
        Assert.Equal("world", File.ReadAllText(Path.Combine(live, "mp_save", "save.sv")));
        Assert.Equal("world", File.ReadAllText(Path.Combine(enabled.Group.DirectoryPath, "mp_save", "save.sv")));
        Assert.Contains(
            Directory.GetDirectories(backups),
            dir => File.Exists(Path.Combine(dir, "mp_save", "save.sv")));
    }

    [Fact]
    public async Task Disable_WithNestedJunction_RefusesBeforeTouchingTheGroup()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var temp = new TempDirectory();
        var profiles = temp.CreateDirectory("profiles");
        var backups = temp.CreateDirectory("backups");
        var staging = temp.CreateDirectory("staging");
        var journal = temp.CreateDirectory("journal");
        var game = temp.CreateDirectory("game");
        var live = Path.Combine(temp.Path, "LocalLow", "Orsoniks", "CasualtiesUnknown");
        var linker = new WindowsJunctionLinker();
        var service = new SaveGroupService(new SaveGroupOptions
        {
            ProfilesRoot = profiles,
            CatalogPath = Path.Combine(temp.Path, "save-groups.json"),
            BackupsRoot = backups,
            StagingRoot = staging,
            JournalRoot = journal,
            LiveSaveDirectory = live,
            Linker = linker,
            RecycleBin = new RejectingRecycleBin(),
            ProbeGameRunningState = _ => GameRunningState.NotRunning,
            ProbeAnyGameRunningState = () => GameRunningState.NotRunning,
        });
        var installation = new GameInstallation(KnownGameProfiles.Full, game, null, false, false);
        var init = await service.InitializeAsync(installation);
        Assert.True(init.Success, init.Message);
        var group = init.Groups.Single();

        var external = temp.CreateDirectory("external-link-target");
        File.WriteAllText(Path.Combine(external, "outside.txt"), "outside");
        Assert.True(linker.CreateJunction(Path.Combine(live, "nested-link"), external, out var linkError),
            linkError);

        var prepared = await service.PrepareDisableHostingAsync();

        Assert.Equal(HostingOperationState.Failed, prepared.State);
        Assert.Contains("链接", prepared.Message);
        Assert.True(linker.TryGetTarget(live, out var target));
        Assert.Equal(Path.GetFullPath(group.DirectoryPath), Path.GetFullPath(target));
        Assert.True(File.Exists(Path.Combine(group.DirectoryPath, "nested-link", "outside.txt")));
        Assert.Empty(Directory.GetFiles(journal));
        linker.RemoveJunction(Path.Combine(group.DirectoryPath, "nested-link"), out _);
    }

    [Fact]
    public async Task Disable_WhenLiveIsReplacedWithAnotherJunctionAfterInstall_DoesNotClaimRollback()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var temp = new TempDirectory();
        var live = Path.Combine(temp.Path, "LocalLow", "Orsoniks", "CasualtiesUnknown");
        var linker = new WindowsJunctionLinker();
        var external = temp.CreateDirectory("external-after-install");
        Action<string>? hook = null;
        hook = stage =>
        {
            if (stage != "disable-after-install") return;
            if (Directory.Exists(live))
            {
                var attributes = File.GetAttributes(live);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    linker.RemoveJunction(live, out _);
                else
                    Directory.Delete(live, recursive: true);
            }
            linker.CreateJunction(live, external, out _);
            throw new IOException("simulated external swap");
        };
        var service = BuildService(temp, live, linker, hook);
        var installation = new GameInstallation(
            KnownGameProfiles.Full, temp.CreateDirectory("game"), null, false, false);
        var init = await service.InitializeAsync(installation);
        Assert.True(init.Success, init.Message);
        Directory.CreateDirectory(Path.Combine(live, "mp_save"));
        File.WriteAllText(Path.Combine(live, "mp_save", "save.sv"), "world");

        var prepared = await service.PrepareDisableHostingAsync();

        Assert.True(prepared.State == HostingOperationState.FailedUnknown, prepared.Message);
        Assert.Single(Directory.GetFiles(Path.Combine(temp.Path, "journal")));
        Assert.Equal(SaveHostingState.PendingRecovery, service.HostingState);
        Assert.True(linker.TryGetTarget(live, out var target));
        Assert.Equal(Path.GetFullPath(external), Path.GetFullPath(target));
    }

    [Fact(Skip = "文件 symlink 需要管理员/开发者模式；本机无法创建，按未验证跳过，不用目录 junction 代替")]
    public async Task Enable_WithFileSymlink_RefusesBeforeReadingTheFile()
    {

        using var temp = new TempDirectory();
        var live = temp.CreateDirectory("live");
        var target = Path.Combine(temp.Path, "target.txt");
        File.WriteAllText(target, "outside");
        var link = Path.Combine(live, "save-link.txt");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "该测试被静态标记为跳过；若取消 Skip，环境必须允许创建文件 symlink。",
                exception);
        }

        var service = BuildService(temp, live);
        service.SetHostingStateFromSettings(false);
        var installation = new GameInstallation(
            KnownGameProfiles.Full, temp.CreateDirectory("game"), null, false, false);

        var prepared = await service.PrepareEnableHostingAsync(
            installation,
            new HostingEnableRequest(HostingEnableChoice.ImportIntoNewGroup, "g"));

        Assert.Equal(HostingOperationState.Failed, prepared.State);
        Assert.Contains("链接", prepared.Message);
        Assert.Equal("outside", File.ReadAllText(target));
        Assert.True(File.Exists(link));
    }

    [Fact]
    public async Task EnableOverwrite_WhenTargetRootIsJunction_RefusesBeforeReadingManifest()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var temp = new TempDirectory();
        var live = Path.Combine(temp.Path, "LocalLow", "Orsoniks", "CasualtiesUnknown");
        var linker = new WindowsJunctionLinker();
        var service = BuildService(temp, live, linker);
        var installation = new GameInstallation(
            KnownGameProfiles.Full, temp.CreateDirectory("game"), null, false, false);
        var init = await service.InitializeAsync(installation);
        var target = init.Groups.Single();

        var moved = Path.Combine(temp.Path, "moved-group");
        Directory.Move(target.DirectoryPath, moved);
        File.WriteAllText(Path.Combine(moved, "outside.txt"), "outside");
        Assert.True(linker.CreateJunction(target.DirectoryPath, moved, out var linkError), linkError);

        if (linker.TryGetTarget(live, out _)) linker.RemoveJunction(live, out _);
        Directory.CreateDirectory(live);
        File.WriteAllText(Path.Combine(live, "save.sv"), "world");
        service.SetHostingStateFromSettings(false);

        var prepared = await service.PrepareEnableHostingAsync(
            installation,
            new HostingEnableRequest(
                HostingEnableChoice.OverwriteExistingGroup,
                TargetGroupId: target.Id));

        Assert.Equal(HostingOperationState.Failed, prepared.State);
        Assert.Contains("链接", prepared.Message);
        Assert.Equal("outside", File.ReadAllText(Path.Combine(moved, "outside.txt")));
        linker.RemoveJunction(target.DirectoryPath, out _);
    }

    private static SaveGroupService BuildService(
        TempDirectory temp,
        string live,
        ISaveLinker? linker = null,
        Action<string>? faultHook = null)
    {
        return new SaveGroupService(new SaveGroupOptions
        {
            ProfilesRoot = temp.CreateDirectory("profiles"),
            CatalogPath = Path.Combine(temp.Path, "save-groups.json"),
            BackupsRoot = temp.CreateDirectory("backups"),
            StagingRoot = temp.CreateDirectory("staging"),
            JournalRoot = temp.CreateDirectory("journal"),
            LiveSaveDirectory = live,
            Linker = linker ?? new WindowsJunctionLinker(),
            RecycleBin = new RejectingRecycleBin(),
            ProbeGameRunningState = _ => GameRunningState.NotRunning,
            ProbeAnyGameRunningState = () => GameRunningState.NotRunning,
            HostingFaultHook = faultHook
        });
    }

    private sealed class RejectingRecycleBin : IRecycleBin
    {
        public bool TrySendDirectory(string path, out string error)
        {
            error = "recycle bin is not used in this test";
            return false;
        }
    }
}

using ModificationsKnown.App.Services;
using ModificationsKnown.Core.Diagnostics;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Saves;
using ModificationsKnown.Core.Settings;
using ModificationsKnown.Infrastructure.Saves;

namespace ModificationsKnown.ViewModels.Tests;

/// <summary>
/// The coordinator owns persistence and the “settings missing/corrupt but the
/// live link is still MK's” rule. These tests use a real service against a
/// temporary root; the fake linker only proves orchestration.
/// </summary>
public sealed class HostingSwitchCoordinatorTests
{
    [Fact]
    public async Task Initialize_MissingSettingsWithoutMkLink_Disables()
    {
        using var context = new HostingCoordinatorContext();
        context.Settings.Hosting = new HostingPreference(null, HostingPreferenceSource.MissingFile);

        await context.Coordinator.InitializeAsync();

        Assert.Equal(SaveHostingState.Disabled, context.Coordinator.State);
    }

    [Fact]
    public async Task Initialize_MissingSettingsWithMkLink_PendingRecovery()
    {
        using var context = new HostingCoordinatorContext();
        await context.CreateGroupAsync();
        context.Settings.Hosting = new HostingPreference(null, HostingPreferenceSource.MissingFile);

        await context.Coordinator.InitializeAsync();

        Assert.Equal(SaveHostingState.PendingRecovery, context.Coordinator.State);
        Assert.Contains("恢复", context.Coordinator.StatusMessage);
        var launched = await context.Service.PrepareLaunchAsync(context.Installation);
        Assert.False(launched.Success);
    }

    [Fact]
    public async Task Initialize_InvalidSettings_PendingRecoveryAndReportsBackup()
    {
        using var context = new HostingCoordinatorContext();
        context.Settings.Hosting = new HostingPreference(
            null, HostingPreferenceSource.Invalid, @"C:\backup\settings.json.corrupt.json");

        await context.Coordinator.InitializeAsync();

        Assert.Equal(SaveHostingState.PendingRecovery, context.Coordinator.State);
        Assert.Contains("settings.json.corrupt.json", context.Coordinator.StatusMessage);
    }

    [Fact]
    public async Task Initialize_DisabledSettingsWithUnknownLive_PendingRecovery()
    {
        using var context = new HostingCoordinatorContext();
        Assert.True(context.CreateUnknownLiveLink(), "真实目录联接不可用，无法验证 Unknown 分支");
        context.Settings.Hosting = new HostingPreference(false, HostingPreferenceSource.Disabled);

        await context.Coordinator.InitializeAsync();

        Assert.Equal(SaveHostingState.PendingRecovery, context.Coordinator.State);
        Assert.Contains("无法确认", context.Coordinator.StatusMessage);
    }

    [Fact]
    public async Task Initialize_MissingSettingsWithUnknownLive_PendingRecovery()
    {
        using var context = new HostingCoordinatorContext();
        Assert.True(context.CreateUnknownLiveLink(), "真实目录联接不可用，无法验证 Unknown 分支");
        context.Settings.Hosting = new HostingPreference(null, HostingPreferenceSource.MissingFile);

        await context.Coordinator.InitializeAsync();

        Assert.Equal(SaveHostingState.PendingRecovery, context.Coordinator.State);
    }

    [Fact]
    public async Task RestoreFromRecovery_NoJournalWithMkLink_Completes()
    {
        using var context = new HostingCoordinatorContext();
        await context.CreateGroupAsync();
        context.Settings.Hosting = new HostingPreference(null, HostingPreferenceSource.MissingFile);
        await context.Coordinator.InitializeAsync();

        var result = await context.Coordinator.RestoreFromRecoveryAsync(context.Installation);

        Assert.True(result.Success, result.Message);
        Assert.Equal(SaveHostingState.Enabled, context.Coordinator.State);
        Assert.True(context.Settings.Current.HostingEnabled);
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(Path.GetFullPath(context.GroupDirectory), Path.GetFullPath(target));
    }

    [Fact]
    public async Task DisableFromRecovery_NoJournalWithMkLink_CopiesBackAndCompletes()
    {
        using var context = new HostingCoordinatorContext();
        await context.CreateGroupAsync();
        File.WriteAllText(Path.Combine(context.Live, "save.sv"), "world");
        context.Settings.Hosting = new HostingPreference(null, HostingPreferenceSource.MissingFile);
        await context.Coordinator.InitializeAsync();

        var result = await context.Coordinator.DisableFromRecoveryAsync(context.Installation);

        Assert.True(result.Success, result.Message);
        Assert.Equal(SaveHostingState.Disabled, context.Coordinator.State);
        Assert.False(context.Settings.Current.HostingEnabled);
        Assert.False(context.Linker.TryGetTarget(context.Live, out _));
        Assert.Equal("world", File.ReadAllText(Path.Combine(context.Live, "save.sv")));
    }

    [Theory]
    [InlineData(GameRunningState.Running)]
    [InlineData(GameRunningState.Unknown)]
    public async Task RestoreFromRecovery_WhenRunningOrUnknown_RefusesWithoutChangingLink(
        GameRunningState state)
    {
        using var context = new HostingCoordinatorContext();
        await context.CreateGroupAsync();
        context.Settings.Hosting = new HostingPreference(null, HostingPreferenceSource.MissingFile);
        await context.Coordinator.InitializeAsync();
        context.ProbeAnyState = state;

        var result = await context.Coordinator.RestoreFromRecoveryAsync(context.Installation);

        Assert.False(result.Success, result.Message);
        Assert.Equal(HostingOperationState.RunningLocked, result.State);
        Assert.True(context.Linker.TryGetTarget(context.Live, out _));
    }

    [Theory]
    [InlineData(GameRunningState.Running)]
    [InlineData(GameRunningState.Unknown)]
    public async Task DisableFromRecovery_WhenRunningOrUnknown_RefusesWithoutChangingLink(
        GameRunningState state)
    {
        using var context = new HostingCoordinatorContext();
        await context.CreateGroupAsync();
        context.Settings.Hosting = new HostingPreference(null, HostingPreferenceSource.MissingFile);
        await context.Coordinator.InitializeAsync();
        context.ProbeAnyState = state;

        var result = await context.Coordinator.DisableFromRecoveryAsync(context.Installation);

        Assert.False(result.Success, result.Message);
        Assert.Equal(HostingOperationState.RunningLocked, result.State);
        Assert.True(context.Linker.TryGetTarget(context.Live, out _));
    }


    [Fact]
    public async Task NoJournalRecovery_ProfileLinkFromAnotherInstallation_RefusesBothButtons()
    {
        using var context = new HostingCoordinatorContext();
        var other = Directory.CreateDirectory(
            Path.Combine(context.ProfilesRoot, "0000000000000000")).FullName;
        Directory.CreateDirectory(Path.GetDirectoryName(context.Live)!);
        Assert.True(context.Linker.CreateJunction(context.Live, other, out var linkError), linkError);
        context.Service.MarkHostingPendingRecovery("test foreign profile");

        var restore = await context.Coordinator.RestoreFromRecoveryAsync(context.Installation);
        var disable = await context.Coordinator.DisableFromRecoveryAsync(context.Installation);

        Assert.False(restore.Success, restore.Message);
        Assert.False(disable.Success, disable.Message);
        Assert.DoesNotContain("没有指向", restore.Message);
        Assert.DoesNotContain("没有指向", disable.Message);
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(Path.GetFullPath(other), Path.GetFullPath(target));
    }

    [Fact]
    public async Task DisableFromRecovery_WhenSettingsFail_CanRetry()
    {
        using var context = new HostingCoordinatorContext();
        await context.CreateGroupAsync();
        File.WriteAllText(Path.Combine(context.Live, "save.sv"), "world");
        context.Settings.Hosting = new HostingPreference(null, HostingPreferenceSource.MissingFile);
        await context.Coordinator.InitializeAsync();
        context.Settings.FailSave = true;

        var failed = await context.Coordinator.DisableFromRecoveryAsync(context.Installation);
        Assert.False(failed.Success, failed.Message);
        Assert.True(context.Linker.TryGetTarget(context.Live, out _));
        // FakeSaveLinker does not map link contents; put the file back on the
        // fake link path before the retry.
        File.WriteAllText(Path.Combine(context.Live, "save.sv"), "world");

        context.Settings.FailSave = false;
        var retry = await context.Coordinator.DisableFromRecoveryAsync(context.Installation);
        Assert.True(retry.Success, retry.Message);
        Assert.Equal(SaveHostingState.Disabled, context.Coordinator.State);
        Assert.Equal("world", File.ReadAllText(Path.Combine(context.Live, "save.sv")));
    }

    [Fact]
    public async Task RestoreFromRecovery_ExplicitDisabledWithMkLink_Completes()
    {
        using var context = new HostingCoordinatorContext();
        await context.CreateGroupAsync();
        context.Settings.Hosting = new HostingPreference(false, HostingPreferenceSource.Disabled);
        await context.Coordinator.InitializeAsync();

        var result = await context.Coordinator.RestoreFromRecoveryAsync(context.Installation);

        Assert.True(result.Success, result.Message);
        Assert.Equal(SaveHostingState.Enabled, context.Coordinator.State);
    }

    [Fact]
    public async Task RestoreFromRecovery_WhenJournalInvalid_DoesNotFallBackToNoJournalPath()
    {
        using var context = new HostingCoordinatorContext();
        await context.CreateGroupAsync();
        Directory.CreateDirectory(context.JournalRoot);
        var journal = Path.Combine(context.JournalRoot, "invalid.json");
        File.WriteAllText(journal, "{broken");
        context.Settings.Hosting = new HostingPreference(null, HostingPreferenceSource.MissingFile);
        await context.Coordinator.InitializeAsync();

        var result = await context.Coordinator.RestoreFromRecoveryAsync(context.Installation);

        Assert.Equal(HostingOperationState.PendingRecovery, result.State);
        Assert.True(File.Exists(journal));
    }

    [Fact]
    public async Task RestoreFromRecovery_WhenExternalLink_DoesNotTakeOver()
    {
        using var context = new HostingCoordinatorContext();
        var external = Directory.CreateDirectory(Path.Combine(context.Root, "external")).FullName;
        Directory.CreateDirectory(Path.GetDirectoryName(context.Live)!);
        context.Linker.SetTarget(context.Live, external);
        context.Service.MarkHostingPendingRecovery("test anomaly");

        var result = await context.Coordinator.RestoreFromRecoveryAsync(context.Installation);

        Assert.Equal(HostingOperationState.PendingRecovery, result.State);
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(Path.GetFullPath(external), Path.GetFullPath(target));
    }

    [Fact]
    public async Task RestoreFromRecovery_WhenSettingsFail_CanRetry()
    {
        using var context = new HostingCoordinatorContext();
        await context.CreateGroupAsync();
        context.Settings.Hosting = new HostingPreference(null, HostingPreferenceSource.MissingFile);
        await context.Coordinator.InitializeAsync();
        context.Settings.FailSave = true;

        var failed = await context.Coordinator.RestoreFromRecoveryAsync(context.Installation);
        Assert.False(failed.Success);
        Assert.Equal(SaveHostingState.PendingRecovery, context.Coordinator.State);

        context.Settings.FailSave = false;
        var retry = await context.Coordinator.RestoreFromRecoveryAsync(context.Installation);
        Assert.True(retry.Success, retry.Message);
        Assert.Equal(SaveHostingState.Enabled, context.Coordinator.State);
    }

    [Fact]
    public async Task Disable_PersistsFalseAndPublishesDisabled()
    {
        using var context = new HostingCoordinatorContext();
        await context.CreateGroupAsync();
        context.Settings.Hosting = new HostingPreference(true, HostingPreferenceSource.Enabled);
        await context.Coordinator.InitializeAsync();

        var result = await context.Coordinator.DisableAsync();

        Assert.True(result.Success, result.Message);
        Assert.Equal(SaveHostingState.Disabled, context.Coordinator.State);
        Assert.False(context.Settings.Current.HostingEnabled);
        Assert.False(context.Linker.TryGetTarget(context.Live, out _));
    }

    [Fact]
    public async Task Disable_WhenSettingsSaveFails_StaysEnabledAndRestoresLink()
    {
        using var context = new HostingCoordinatorContext();
        await context.CreateGroupAsync();
        context.Settings.Hosting = new HostingPreference(true, HostingPreferenceSource.Enabled);
        await context.Coordinator.InitializeAsync();
        context.Settings.FailSave = true;

        var result = await context.Coordinator.DisableAsync();

        Assert.False(result.Success);
        Assert.Equal(SaveHostingState.Enabled, context.Coordinator.State);
        Assert.True(context.Settings.Current.HostingEnabled);
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(context.GroupDirectory, Path.GetFullPath(target));
    }

    [Fact]
    public async Task Enable_WhenLiveIsEmpty_CreatesDefaultAndPersistsTrue()
    {
        using var context = new HostingCoordinatorContext();
        context.Service.SetHostingStateFromSettings(false);
        Directory.CreateDirectory(context.Live);
        context.Settings.Hosting = new HostingPreference(false, HostingPreferenceSource.Disabled);

        var result = await context.Coordinator.EnableAsync(context.Installation);

        Assert.True(result.Success, result.Message);
        Assert.Equal(SaveHostingState.Enabled, context.Coordinator.State);
        Assert.True(context.Settings.Current.HostingEnabled);
        Assert.Equal("默认", result.Group?.Name);
    }

    private sealed class HostingCoordinatorContext : IDisposable
    {
        public HostingCoordinatorContext()
        {
            Root = Path.Combine(Path.GetTempPath(), "mk-hosting-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            ProfilesRoot = Directory.CreateDirectory(Path.Combine(Root, "profiles")).FullName;
            BackupsRoot = Directory.CreateDirectory(Path.Combine(Root, "backups")).FullName;
            StagingRoot = Directory.CreateDirectory(Path.Combine(Root, "staging")).FullName;
            JournalRoot = Directory.CreateDirectory(Path.Combine(Root, "journal")).FullName;
            Live = Path.Combine(Root, "LocalLow", "Orsoniks", "CasualtiesUnknown");
            GameDirectory = Directory.CreateDirectory(Path.Combine(Root, "game")).FullName;
            Installation = new GameInstallation(KnownGameProfiles.Full, GameDirectory, null, false, false);
            Service = new SaveGroupService(new SaveGroupOptions
            {
                ProfilesRoot = ProfilesRoot,
                CatalogPath = Path.Combine(Root, "save-groups.json"),
                BackupsRoot = BackupsRoot,
                StagingRoot = StagingRoot,
                JournalRoot = JournalRoot,
                LiveSaveDirectory = Live,
                Linker = Linker,
                RecycleBin = new NoopRecycleBin(),
                ProbeGameRunningState = _ => ProbeAnyState,
                ProbeAnyGameRunningState = () => ProbeAnyState
            });
            Settings = new FakeSettingsService();
            Coordinator = new HostingSwitchCoordinator(Service, Settings, new NoopLogger());
        }

        public string Root { get; }
        public string ProfilesRoot { get; }
        public string BackupsRoot { get; }
        public string StagingRoot { get; }
        public string JournalRoot { get; }
        public string Live { get; }
        public string GameDirectory { get; }
        public GameInstallation Installation { get; }
        public GameRunningState ProbeAnyState { get; set; } = GameRunningState.NotRunning;
        public FakeLinker Linker { get; } = new();
        public SaveGroupService Service { get; }
        public FakeSettingsService Settings { get; }
        public HostingSwitchCoordinator Coordinator { get; }
        public string GroupDirectory =>
            Path.Combine(ProfilesRoot, SaveGroupFormat.GetInstallationKey(GameDirectory),
                Service.GetNextLaunchGroupId(Installation)?.ToString("N").ToLowerInvariant() ?? "");

        public async Task CreateGroupAsync()
        {
            var init = await Service.InitializeAsync(Installation);
            Assert.True(init.Success, init.Message);
        }

        public bool CreateUnknownLiveLink()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Live)!);
                var target = Directory.CreateDirectory(Path.Combine(Root, "unknown-target"));
                return new WindowsJunctionLinker().CreateJunction(Live, target.FullName, out _)
                       && (File.GetAttributes(Live) & FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }

    private sealed class FakeSettingsService : IAppSettingsService
    {
        public AppSettings Current { get; } = new();
        public HostingPreference Hosting { get; set; } = new(null, HostingPreferenceSource.MissingFile);
        public bool FailSave { get; set; }
        public event EventHandler? Changed;

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveAsync(CancellationToken cancellationToken = default)
        {
            if (FailSave) throw new IOException("settings file is locked");
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLinker : ISaveLinker
    {
        private readonly Dictionary<string, string> _targets = new(StringComparer.OrdinalIgnoreCase);
        public bool IsSupported => true;
        public bool Exists(string path) => Directory.Exists(path);
        public bool TryGetTarget(string path, out string target) =>
            _targets.TryGetValue(Normalize(path), out target!);

        public void SetTarget(string linkPath, string targetPath) =>
            _targets[Normalize(linkPath)] = Normalize(targetPath);

        public bool CreateJunction(string linkPath, string targetPath, out string error)
        {
            _targets[Normalize(linkPath)] = Normalize(targetPath);
            Directory.CreateDirectory(linkPath);
            error = string.Empty;
            return true;
        }

        public bool RemoveJunction(string linkPath, out string error)
        {
            _targets.Remove(Normalize(linkPath));
            if (Directory.Exists(linkPath)) Directory.Delete(linkPath, recursive: true);
            error = string.Empty;
            return true;
        }

        private static string Normalize(string path) =>
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private sealed class NoopRecycleBin : IRecycleBin
    {
        public bool TrySendDirectory(string path, out string error)
        {
            error = string.Empty;
            return true;
        }
    }

    private sealed class NoopLogger : IAppLogger
    {
        public string LogDirectory => Path.GetTempPath();
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
        public Task<string> ReadRecentAsync(int maxCharacters = 60_000, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);
    }
}

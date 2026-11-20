using ModificationsKnown.App.Services;
using ModificationsKnown.App.ViewModels;
using ModificationsKnown.Core.Diagnostics;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Nexus;
using ModificationsKnown.Core.Saves;
using ModificationsKnown.Core.Settings;
using ModificationsKnown.Infrastructure.Games;
using ModificationsKnown.Infrastructure.Instances;
using ModificationsKnown.Infrastructure.Saves;
using ModificationsKnown.Infrastructure.Storage;

namespace ModificationsKnown.ViewModels.Tests;

/// <summary>
/// Page-level behaviour of the switch: disabled mode never scans or
/// initializes groups, cancel touches nothing, and only a committed transaction
/// flips the visible state. A fake linker cannot prove junction semantics.
/// </summary>
public sealed class SavesHostingViewModelTests
{
    [Fact]
    public async Task Refresh_WhenHostingDisabled_DoesNotInitializeGroups()
    {
        using var context = new ViewModelContext();
        Directory.CreateDirectory(context.Live);
        File.WriteAllText(Path.Combine(context.Live, "save.sv"), "unmanaged");
        context.Service.SetHostingStateFromSettings(false);

        await context.ViewModel.OnActivatedAsync();

        Assert.True(context.ViewModel.IsHostingDisabled);
        Assert.Empty(context.ViewModel.Instances);
        Assert.Empty(context.Service.GetGroups(context.Installation));
    }

    [Fact]
    public async Task Disable_Cancelled_KeepsEnabledAndUntouched()
    {
        using var context = new ViewModelContext();
        await context.CreateGroupAsync();
        await context.Coordinator.InitializeAsync();
        await context.ViewModel.OnActivatedAsync();
        Assert.True(context.ViewModel.IsHostingEnabled);
        context.Dialogs.ConfirmResult = false;

        await context.ViewModel.SetHostingAsync(false);

        Assert.True(context.ViewModel.IsHostingEnabled);
        Assert.Equal(SaveHostingState.Enabled, context.Coordinator.State);
        Assert.True(context.Linker.TryGetTarget(context.Live, out _));
    }

    [Fact]
    public async Task Disable_Confirmed_ClearsPageAndPersists()
    {
        using var context = new ViewModelContext();
        await context.CreateGroupAsync();
        await context.Coordinator.InitializeAsync();
        await context.ViewModel.OnActivatedAsync();
        context.Dialogs.ConfirmResult = true;

        await context.ViewModel.SetHostingAsync(false);

        Assert.True(context.ViewModel.IsHostingDisabled);
        Assert.Empty(context.ViewModel.Instances);
        Assert.Equal(SaveHostingState.Disabled, context.Coordinator.State);
        Assert.False(context.Settings.Current.HostingEnabled);
        Assert.False(context.Linker.TryGetTarget(context.Live, out _));
    }

    [Fact]
    public async Task Enable_WhenLiveIsEmpty_CreatesDefaultAndShowsIt()
    {
        using var context = new ViewModelContext();
        context.Service.SetHostingStateFromSettings(false);
        Directory.CreateDirectory(context.Live);
        await context.ViewModel.OnActivatedAsync();
        Assert.True(context.ViewModel.IsHostingDisabled);

        await context.ViewModel.SetHostingAsync(true);

        Assert.True(context.ViewModel.IsHostingEnabled);
        Assert.Equal(SaveHostingState.Enabled, context.Coordinator.State);
        Assert.True(context.Settings.Current.HostingEnabled);
        Assert.Single(context.ViewModel.Instances);
    }

    [Fact]
    public async Task Enable_Cancelled_LeavesGameFolderUntouched()
    {
        using var context = new ViewModelContext();
        context.Service.SetHostingStateFromSettings(false);
        Directory.CreateDirectory(context.Live);
        File.WriteAllText(Path.Combine(context.Live, "save.sv"), "unmanaged");
        context.Dialogs.HostingDecision = null;
        await context.ViewModel.OnActivatedAsync();

        await context.ViewModel.SetHostingAsync(true);

        Assert.True(context.ViewModel.IsHostingDisabled);
        Assert.Equal(SaveHostingState.Disabled, context.Coordinator.State);
        Assert.Equal("unmanaged", File.ReadAllText(Path.Combine(context.Live, "save.sv")));
        Assert.False(context.Linker.TryGetTarget(context.Live, out _));
        Assert.Empty(context.Service.GetGroups(context.Installation));
    }

    private sealed class ViewModelContext : IDisposable
    {
        public ViewModelContext()
        {
            Root = Path.Combine(Path.GetTempPath(), "mk-vm-hosting-" + Guid.NewGuid().ToString("N"));
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
                ProbeGameRunningState = _ => GameRunningState.NotRunning,
                ProbeAnyGameRunningState = () => GameRunningState.NotRunning
            });
            Settings = new MutableSettings();
            Coordinator = new HostingSwitchCoordinator(Service, Settings, Logger);
            Archive = new SaveGroupArchiveService(
                isGameRunning: () => false,
                coordinator: Service);

            Game = new GameContext(new GameInstanceStore());
            Game.Installations.Add(Installation);
            Game.Selected = Installation;
            Shell = new StubShell(Game, Settings);

            ViewModel = new SavesViewModel(
                Shell,
                Service,
                Coordinator,
                Archive,
                new SaveSlotReader(),
                new StubFilePicker(),
                Dialogs,
                Logger,
                new GameProcessMonitor(Game, Logger),
                new GameExecutableVersionReader());
        }

        public string Root { get; }
        public string ProfilesRoot { get; }
        public string BackupsRoot { get; }
        public string StagingRoot { get; }
        public string JournalRoot { get; }
        public string Live { get; }
        public string GameDirectory { get; }
        public GameInstallation Installation { get; }
        public FakeLinker Linker { get; } = new();
        public SaveGroupService Service { get; }
        public MutableSettings Settings { get; }
        public HostingSwitchCoordinator Coordinator { get; }
        public SaveGroupArchiveService Archive { get; }
        public GameContext Game { get; }
        public StubShell Shell { get; }
        public StubDialogs Dialogs { get; } = new();
        public NoopLogger Logger { get; } = new();
        public SavesViewModel ViewModel { get; }

        public async Task CreateGroupAsync()
        {
            var init = await Service.InitializeAsync(Installation);
            Assert.True(init.Success, init.Message);
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

    private sealed class MutableSettings : IAppSettingsService
    {
        public AppSettings Current { get; } = new();
        public HostingPreference Hosting { get; set; } = new(true, HostingPreferenceSource.Enabled);
        public event EventHandler? Changed;

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveAsync(CancellationToken cancellationToken = default)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }

    private sealed class StubShell : IAppShell
    {
        public StubShell(GameContext game, IAppSettingsService settings)
        {
            Game = game;
            Settings = settings;
        }

        public GameContext Game { get; }
        public IAppSettingsService Settings { get; }
        public List<(string Message, StatusKind Kind)> Statuses { get; } = [];
        public bool CanLaunch => true;
        public string LaunchButtonText => "启动";
        public bool IsCloseAction => false;
        public event Action<IReadOnlyCollection<string>>? ModsChanged;
        public void SetStatus(string message, StatusKind kind = StatusKind.Info) => Statuses.Add((message, kind));
        public Task RefreshGameAsync() => Task.CompletedTask;
        public Task LaunchGameAsync() => Task.CompletedTask;
        public void NotifyModsChanged(IReadOnlyCollection<string> packageNames) =>
            ModsChanged?.Invoke(packageNames);
        public void NavigateTo(string key) { }
        public void NavigateSettings(string focus) { }
    }

    private sealed class StubDialogs : IUserDialogService
    {
        public bool ConfirmResult { get; set; } = true;
        public HostingEnableDecision? HostingDecision { get; set; }

        public Task<bool> ConfirmAsync(string title, string message, string confirmText = "确认", string cancelText = "取消") =>
            Task.FromResult(ConfirmResult);
        public Task<bool> ConfirmDangerAsync(string title, string message, string confirmText = "确认", string cancelText = "取消") =>
            Task.FromResult(ConfirmResult);
        public Task AlertAsync(string title, string message, string closeText = "知道了") => Task.CompletedTask;
        public Task<string?> PromptAsync(string title, string message, string initialValue = "", string watermark = "名称") =>
            Task.FromResult<string?>(null);
        public Task<NexusMatch?> PickNexusMatchAsync(string gameDomain, string suggestedName) =>
            Task.FromResult<NexusMatch?>(null);
        public Task<UpdateModChoice?> ConfirmUpdateAsync(string title, string message, bool showBackupOption = true) =>
            Task.FromResult<UpdateModChoice?>(null);
        public Task<bool> PromptNxmAsync() => Task.FromResult(false);
        public Task<bool> ConfirmBepInExUninstallAsync(GameInstallation installation) => Task.FromResult(false);
        public Task<InstanceDeletionPlan?> PickInstanceDeletionAsync(
            GameInstallation installation, IReadOnlyList<SaveGroup> groups) =>
            Task.FromResult<InstanceDeletionPlan?>(null);
        public Task<UnmanagedSaveDecision?> PickUnmanagedSaveAsync(
            string liveDirectory, int fileCount,
            IReadOnlyList<UnmanagedSaveCandidate> candidates, string? selectedInstallationKey) =>
            Task.FromResult<UnmanagedSaveDecision?>(null);
        public Task<HostingEnableDecision?> PickHostingEnableAsync(
            string installationLabel, string liveDirectory, int fileCount,
            IReadOnlyList<SaveGroup> groups, Guid? selectedGroupId, string suggestedName) =>
            Task.FromResult(HostingDecision);
    }

    private sealed class StubFilePicker : IFilePickerService
    {
        public Task<string?> PickExecutableAsync() => Task.FromResult<string?>(null);
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
        public Task<string?> PickArchiveAsync() => Task.FromResult<string?>(null);
        public Task<string?> PickThemeFileAsync() => Task.FromResult<string?>(null);
        public Task<string?> PickLogFileAsync() => Task.FromResult<string?>(null);
        public Task<string?> PickDataFolderAsync() => Task.FromResult<string?>(null);
        public Task<string?> PickBackupFolderAsync() => Task.FromResult<string?>(null);
        public Task<string?> PickSaveArchiveTargetAsync(string suggestedFileName) => Task.FromResult<string?>(null);
        public Task<string?> PickSaveArchiveAsync() => Task.FromResult<string?>(null);
    }

    private sealed class FakeLinker : ISaveLinker
    {
        private readonly Dictionary<string, string> _targets = new(StringComparer.OrdinalIgnoreCase);
        public bool IsSupported => true;
        public bool Exists(string path) => Directory.Exists(path);
        public bool TryGetTarget(string path, out string target) =>
            _targets.TryGetValue(Normalize(path), out target!);

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

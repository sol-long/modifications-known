using System.Text.Json;
using ModificationsKnown.Core.Settings;
using ModificationsKnown.Infrastructure.Storage;

namespace ModificationsKnown.Tests;

/// <summary>
/// Field-level hosting migration. A wrong value must never reset unrelated
/// settings, and a genuinely missing file (new install) differs from an old
/// file that simply predates the field.
/// </summary>
public sealed class HostingSettingsMigrationTests
{
    [Fact]
    public async Task MissingFile_IsANewInstallAndPersistsOff()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        var service = new JsonAppSettingsService(path);

        await service.LoadAsync();

        Assert.Equal(HostingPreferenceSource.MissingFile, service.Hosting.Source);
        Assert.False(service.Hosting.Value);
        Assert.False(service.Current.HostingEnabled);
        Assert.Contains("\"HostingEnabled\": false", File.ReadAllText(path));
    }

    [Fact]
    public async Task OldFileWithoutField_IsMigratedToOn()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path,
            "{\"themePreset\":\"slate\",\"themePresetMigrated\":true,\"nexusApiKey\":\"keep-me\"}");
        var service = new JsonAppSettingsService(path);

        await service.LoadAsync();

        Assert.Equal(HostingPreferenceSource.MissingField, service.Hosting.Source);
        Assert.True(service.Hosting.Value);
        Assert.True(service.Current.HostingEnabled);
        Assert.Equal("slate", service.Current.ThemePreset);
        Assert.Equal("keep-me", service.Current.NexusApiKey);
        Assert.Contains("\"HostingEnabled\": true", File.ReadAllText(path));
    }

    [Fact]
    public async Task ExplicitFalse_StaysOff()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path,
            "{\"hostingEnabled\":false,\"themePresetMigrated\":true,\"themePreset\":\"moss\"}");
        var service = new JsonAppSettingsService(path);

        await service.LoadAsync();

        Assert.Equal(HostingPreferenceSource.Disabled, service.Hosting.Source);
        Assert.False(service.Hosting.Value);
        Assert.False(service.Current.HostingEnabled);
    }

    [Fact]
    public async Task WrongType_IsInvalidAndKeepsOtherSettings()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path,
            "{\"hostingEnabled\":\"yes\",\"themePreset\":\"daylight\",\"themePresetMigrated\":true,\"nexusApiKey\":\"keep\"}");
        var service = new JsonAppSettingsService(path);

        await service.LoadAsync();

        Assert.Equal(HostingPreferenceSource.Invalid, service.Hosting.Source);
        Assert.Null(service.Hosting.Value);
        Assert.Equal("daylight", service.Current.ThemePreset);
        Assert.Equal("keep", service.Current.NexusApiKey);
        Assert.NotNull(service.Hosting.CorruptBackupPath);
        Assert.True(File.Exists(service.Hosting.CorruptBackupPath));
    }

    [Fact]
    public async Task BrokenJson_IsParseFailedAndKeepsACopy()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        const string broken = "{\"hostingEnabled\": true, \"themePreset\": ";
        File.WriteAllText(path, broken);
        var service = new JsonAppSettingsService(path);

        await service.LoadAsync();

        Assert.Equal(HostingPreferenceSource.ParseFailed, service.Hosting.Source);
        Assert.NotNull(service.Hosting.CorruptBackupPath);
        Assert.Equal(broken, File.ReadAllText(service.Hosting.CorruptBackupPath!));
        Assert.Equal(broken, File.ReadAllText(path));
    }

    [Fact]
    public async Task OtherInvalidField_IsParseFailedInsteadOfCrashingTheApp()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path,
            "{\"hostingEnabled\":true,\"themePresetMigrated\":true,\"pageSize\":\"not-a-number\"}");
        var service = new JsonAppSettingsService(path);

        await service.LoadAsync();

        Assert.Equal(HostingPreferenceSource.ParseFailed, service.Hosting.Source);
        Assert.NotNull(service.Hosting.CorruptBackupPath);
        Assert.True(File.Exists(service.Hosting.CorruptBackupPath));
    }

    [Fact]
    public async Task ExplicitNull_IsTreatedAsAnOldFile()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path,
            "{\"hostingEnabled\":null,\"themePresetMigrated\":true}");
        var service = new JsonAppSettingsService(path);

        await service.LoadAsync();

        Assert.Equal(HostingPreferenceSource.MissingField, service.Hosting.Source);
        Assert.True(service.Current.HostingEnabled);
    }
}

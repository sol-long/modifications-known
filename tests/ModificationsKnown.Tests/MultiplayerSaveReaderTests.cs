using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using ModificationsKnown.Core.Saves;
using ModificationsKnown.Infrastructure.Saves;

namespace ModificationsKnown.Tests;

/// <summary>
/// Multiplayer saves: QoL host bundles (<c>slot_N.mp\</c>), QoL client inventory
/// snapshots (<c>slot_N.client.json</c>) and the live <c>mp_save\</c> directory.
/// The fixtures are a sanitised copy of a real 2026-11-20 run (SAVEID 8675,
/// two players) plus synthetic variants.
/// </summary>
public sealed class MultiplayerSaveReaderTests
{
    // ---- host bundle ------------------------------------------------------

    [Fact]
    public void HostBundle_ParsesRulesPlayersAndWorldState()
    {
        var directory = CopyFixture("host-bundle");
        try
        {
            var details = new SaveSlotReader().ReadGroup(directory);

            Assert.Equal(1, details.MultiplayerSlotCount);
            Assert.True(details.HasMultiplayer);

            var slot = Assert.Single(details.MultiplayerSaves);
            Assert.Equal(SaveArtifactKind.MultiplayerHostSlot, slot.Kind);
            Assert.Equal("槽 9", slot.Label);

            var info = Assert.IsType<MultiplayerSaveInfo>(slot.Multiplayer);
            Assert.False(info.IsIncomplete);
            Assert.Equal(2, info.PlayerCount);
            Assert.Equal(8675, info.SaveId);
            Assert.Equal(18, info.ModifierIndex);
            Assert.Equal(76, info.RuleCount);
            Assert.Equal("5.5.4", info.MpVersion);
            Assert.Equal("1.0.2", info.GameVersion);
            Assert.True(info.HasWorldgenState);
            Assert.Equal(200, info.WorldgenStateBytes);
            Assert.True(info.IsMidLevel);

            Assert.Equal(2, info.Players.Count);
            Assert.Contains(info.Players, player => player.Name == "HostCU");
            Assert.Contains(info.Players, player => player.Name == "ClientCU");
            Assert.All(info.Players, player => Assert.Equal(8675, player.CharacterId));
            Assert.All(info.Players, player => Assert.NotNull(player.PositionX));

            // The summary marks a multiplayer save as "（联机）".
            Assert.Contains("（联机）", details.MostRecentDetail ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void HostBundle_DoesNotProduceLooseFileCards()
    {
        var directory = CopyFixture("host-bundle");
        try
        {
            var details = new SaveSlotReader().ReadGroup(directory);

            Assert.DoesNotContain(details.Saves, save => save.Kind == SaveArtifactKind.Generic);
            Assert.Empty(details.Saves);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void HostBundle_MissingBundle_IsIncomplete()
    {
        var directory = CopyFixture("host-incomplete");
        try
        {
            var details = new SaveSlotReader().ReadGroup(directory);

            var slot = Assert.Single(details.MultiplayerSaves);
            var info = Assert.IsType<MultiplayerSaveInfo>(slot.Multiplayer);
            Assert.True(info.IsIncomplete);
            Assert.Empty(info.Players);
            Assert.Equal(SaveArtifactKind.MultiplayerHostSlot, slot.Kind);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    // ---- client snapshot --------------------------------------------------

    [Fact]
    public void ClientSnapshot_IsNotIncompleteAndCarriesPlayerName()
    {
        var directory = CopyFixture("client-snapshot");
        try
        {
            var details = new SaveSlotReader().ReadGroup(directory);

            var slot = Assert.Single(details.MultiplayerSaves);
            Assert.Equal(SaveArtifactKind.MultiplayerClientSnapshot, slot.Kind);

            var info = Assert.IsType<MultiplayerSaveInfo>(slot.Multiplayer);
            Assert.False(info.IsIncomplete);
            Assert.Equal("HostCU", info.PlayerName);

            var player = Assert.Single(info.Players);
            Assert.Equal("HostCU", player.Name);
            Assert.True(player.ItemCount >= 1);
            Assert.Equal(8675, player.CharacterId);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void ClientSnapshot_IsNeverMarkedAsCurrentProgress()
    {
        var directory = CopyFixture("live-equals-slot");
        try
        {
            // Turn the host slot into a client snapshot: even with an identical
            // live folder it must not claim "= 当前进度".
            var metaPath = Path.Combine(directory, "slot_9.sv.meta");
            var meta = File.ReadAllText(metaPath)
                .Replace("MultiplayerHostBundle", "MultiplayerClientInventory", StringComparison.Ordinal);
            File.WriteAllText(metaPath, meta);

            var details = new SaveSlotReader().ReadGroup(directory);
            var snapshot = Assert.Single(
                details.MultiplayerSaves,
                entry => entry.Kind == SaveArtifactKind.MultiplayerClientSnapshot);

            Assert.False(snapshot.Multiplayer!.IsCurrentProgress);
            // A client snapshot never equals the live run, so the live card stays.
            Assert.Contains(details.MultiplayerSaves, entry => entry.Kind == SaveArtifactKind.LiveMultiplayer);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    // ---- live -------------------------------------------------------------

    [Fact]
    public void LiveOnly_IsReadAsTheCurrentSave()
    {
        var directory = CopyFixture("live-only");
        try
        {
            var details = new SaveSlotReader().ReadGroup(directory);

            var live = Assert.Single(details.MultiplayerSaves);
            Assert.Equal(SaveArtifactKind.LiveMultiplayer, live.Kind);
            Assert.NotNull(live.SavedAt);
            Assert.NotNull(live.Multiplayer);
            Assert.Equal(2, live.Multiplayer!.PlayerCount);
            Assert.Equal("当前联机存档", details.SummaryLabel);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void LiveEqualsSlot_IsDeduplicatedIntoTheSlot()
    {
        var directory = CopyFixture("live-equals-slot");
        try
        {
            var details = new SaveSlotReader().ReadGroup(directory);

            var slot = Assert.Single(details.MultiplayerSaves);
            Assert.Equal(SaveArtifactKind.MultiplayerHostSlot, slot.Kind);
            Assert.True(slot.Multiplayer!.IsCurrentProgress);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void LiveWithExtraFile_IsShownAsItsOwnEntry()
    {
        var directory = CopyFixture("live-equals-slot");
        try
        {
            File.WriteAllText(Path.Combine(directory, "mp_save", "extra.tmp"), "x");

            var details = new SaveSlotReader().ReadGroup(directory);

            Assert.Equal(2, details.MultiplayerSaves.Count);
            Assert.Equal(SaveArtifactKind.LiveMultiplayer, details.MultiplayerSaves[0].Kind);
            Assert.False(details.MultiplayerSaves[1].Multiplayer!.IsCurrentProgress);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void SessionOnlyLiveFolder_IsIgnored()
    {
        var directory = CopyFixture("host-bundle");
        try
        {
            // An old `mp_save\_session\` leftover must not become a live card.
            var session = Path.Combine(directory, "mp_save", "_session");
            Directory.CreateDirectory(session);
            File.WriteAllText(Path.Combine(session, "save.sv"), "{}");

            var details = new SaveSlotReader().ReadGroup(directory);

            var slot = Assert.Single(details.MultiplayerSaves);
            Assert.Equal(SaveArtifactKind.MultiplayerHostSlot, slot.Kind);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void LiveWithoutSaveFile_StaysVisibleButIncomplete()
    {
        var directory = CopyFixture("live-only");
        try
        {
            File.Delete(Path.Combine(directory, "mp_save", "save.sv"));

            var details = new SaveSlotReader().ReadGroup(directory);

            var live = Assert.Single(details.MultiplayerSaves);
            Assert.Equal(SaveArtifactKind.LiveMultiplayer, live.Kind);
            Assert.True(live.Multiplayer!.IsIncomplete);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    // ---- robustness -------------------------------------------------------

    [Fact]
    public void BrokenRules_MarksTheSlotIncompleteInsteadOfThrowing()
    {
        var directory = CopyFixture("host-bundle");
        try
        {
            File.WriteAllText(Path.Combine(directory, "slot_9.mp", "mp_rules.json"), "{ not json");

            var details = new SaveSlotReader().ReadGroup(directory);
            var slot = Assert.Single(details.MultiplayerSaves);

            Assert.True(slot.Multiplayer!.IsIncomplete);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void SlotOccupancy_CountsMultiplayerSlots()
    {
        var directory = CopyFixture("mixed-slots");
        try
        {
            var details = new SaveSlotReader().ReadGroup(directory);

            // Declared 1/2/9, slot_1 is a single-player save and slot_9 is a
            // host bundle: only slot 2 is empty.
            Assert.Equal(9, details.SlotCount);
            Assert.Equal(1, details.EmptySlotCount);
            Assert.Equal(new[] { 2 }, details.EmptySlots);
            Assert.Contains("其中 1 个联机", details.SummaryLabel, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    // ---- names and items --------------------------------------------------

    [Fact]
    public void PersistentId_DecodesNamesAndKeepsSteamIdsRaw()
    {
        Assert.True(MultiplayerPersistentId.TryDecode("NAME_486F73744355", out var name, out var isSteam));
        Assert.Equal("HostCU", name);
        Assert.False(isSteam);

        Assert.True(MultiplayerPersistentId.TryDecode("STEAM_76561198166766610", out var steam, out isSteam));
        Assert.Equal("STEAM_76561198166766610", steam);
        Assert.True(isSteam);

        Assert.False(MultiplayerPersistentId.TryDecode("NAME_ZZZZ", out var broken, out _));
        Assert.Equal("NAME_ZZZZ", broken);
    }

    [Fact]
    public void ItemNames_FallBackToTheRawIdWithoutLang()
    {
        var directory = CopyFixture("host-bundle");
        try
        {
            var details = new SaveSlotReader().ReadGroup(directory);
            var player = details.MultiplayerSaves[0].Multiplayer!.Players
                .First(p => p.ItemCount > 0);

            Assert.NotEmpty(player.ItemNames);
            Assert.All(player.ItemNames, item => Assert.False(string.IsNullOrWhiteSpace(item)));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void ItemNames_AreResolvedFromTheGameLangTable()
    {
        var directory = CopyFixture("host-bundle");
        var gameDirectory = Path.Combine(TempRoot(), "game");
        try
        {
            var baseline = new SaveSlotReader().ReadGroup(directory);
            var rawId = baseline.MultiplayerSaves[0].Multiplayer!.Players
                .First(p => p.ItemCount > 0).ItemNames[0];

            var langDir = Path.Combine(gameDirectory, "CasualtiesUnknown_Data", "Lang");
            Directory.CreateDirectory(langDir);
            File.WriteAllText(
                Path.Combine(langDir, "zh-CN.json"),
                $"{{\"main\":{{\"{rawId}\":\"测试物品\"}}}}");

            var localized = new SaveSlotReader().ReadGroup(directory, gameDirectory);
            var localizedNames = localized.MultiplayerSaves[0].Multiplayer!.Players
                .SelectMany(p => p.ItemNames)
                .ToList();

            Assert.Contains("测试物品", localizedNames);
        }
        finally
        {
            DeleteDirectory(directory);
            DeleteDirectory(gameDirectory);
        }
    }

    // ---- archives ---------------------------------------------------------

    [Fact]
    public async Task GroupArchive_KeepsBundlesAndTheLiveDirectory()
    {
        var directory = CopyFixture("live-equals-slot");
        var target = Path.Combine(TempRoot(), "export.zip");
        try
        {
            var group = new SaveGroup(Guid.NewGuid(), "测试组", DateTimeOffset.UtcNow, true, directory);
            var service = new SaveGroupArchiveService();

            var result = await service.ExportAsync(group, SaveGameInfo.Unknown, target);

            Assert.True(result.Success, result.Message);
            using var archive = ZipFile.OpenRead(target);
            var names = archive.Entries.Select(entry => entry.FullName).ToList();
            Assert.Contains(names, name => name.Contains("slot_9.mp/", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(names, name => name.Contains("mp_save/", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteDirectory(directory);
            var parent = Path.GetDirectoryName(target);
            if (parent is not null) DeleteDirectory(parent);
        }
    }

    // ---- helpers ----------------------------------------------------------

    private static string FixtureRoot => Path.Combine(AppContext.BaseDirectory, "fixtures", "mp-save");

    private static string TempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "mk-mp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CopyFixture(string name)
    {
        var target = TempRoot();
        CopyDirectory(Path.Combine(FixtureRoot, name), target);
        return target;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        foreach (var child in Directory.GetDirectories(source))
            CopyDirectory(child, Path.Combine(target, Path.GetFileName(child)));
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup must never fail a test.
        }
    }
}

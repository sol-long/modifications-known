using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Saves;
using ModificationsKnown.Infrastructure.Saves;

namespace ModificationsKnown.App.ViewModels;

public sealed record SaveFact(string Text, string? Tip = null);

/// <summary>One save inside a group, formatted for the detail cards.</summary>
public sealed class SaveSlotCard : ObservableObject
{
    public SaveSlotCard(SaveSlotMetadata save)
    {
        Save = save;
        Label = string.IsNullOrWhiteSpace(save.Label) ? save.FileName : save.Label;
        Badges = BuildBadges(save);
        Facts = BuildFacts(save);
        // A live run is a folder: it has no own size, so "0 B" would be noise.
        FileLine = save.Kind == SaveArtifactKind.LiveMultiplayer
            ? save.FileName
            : $"{save.FileName} · {FormatSize(save.FileSize)}";

        Players = save.Multiplayer?.Players
            .Select(player => new MultiplayerPlayerRow(player))
            .ToList() ?? [];
        _playersExpanded = Players.Count is > 0 and <= 3;
        PlayerSummary = BuildPlayerSummary(save);
        IncompleteNote = save.Multiplayer?.IsIncomplete == true
            ? "这个槽位只剩房主的单人进度，其他玩家和联机世界已经丢失。导出不受影响，但导入后也无法继续这一局联机。"
            : null;
        ClientSnapshotNote = save.Kind == SaveArtifactKind.MultiplayerClientSnapshot
            ? "客机保存的角色快照，没有联机世界；恢复需要房主配合。"
            : null;
    }

    public SaveSlotMetadata Save { get; }

    public string Label { get; }

    public IReadOnlyList<string> Badges { get; }

    public IReadOnlyList<SaveFact> Facts { get; }

    public string FileLine { get; }

    public bool IsReadable => Save.IsReadable;

    public bool IsUnreadable => !Save.IsReadable;

    public bool HasError => !string.IsNullOrWhiteSpace(Save.Error);

    public string Error => Save.Error ?? string.Empty;

    // ---- multiplayer -------------------------------------------------------

    public bool IsMultiplayer => Save.Multiplayer is not null;

    public bool IsLive => Save.Kind == SaveArtifactKind.LiveMultiplayer;

    public bool IsClientSnapshot => Save.Kind == SaveArtifactKind.MultiplayerClientSnapshot;

    public bool IsIncomplete => Save.Multiplayer?.IsIncomplete == true;

    public bool IsCurrentProgress => Save.Multiplayer?.IsCurrentProgress == true;

    /// <summary>e.g. "玩家：HostCU、ClientCU"; null when the card has no player list.</summary>
    public string? PlayerSummary { get; }

    public IReadOnlyList<MultiplayerPlayerRow> Players { get; }

    public bool HasPlayers => Players.Count > 0;

    /// <summary>Shown under a host bundle whose <c>slot_N.mp</c> folder is gone.</summary>
    public string? IncompleteNote { get; }

    /// <summary>Shown under a client snapshot: it never carried a world.</summary>
    public string? ClientSnapshotNote { get; }

    public string? LiveHint => IsLive
        ? "在游戏里用 QoL 保存后，这里会出现对应的联机槽位。"
        : null;

    public bool HasPlayerSummary => !string.IsNullOrEmpty(PlayerSummary);

    private bool _playersExpanded;

    /// <summary>Players start collapsed: the card stays compact for rooms of any size.</summary>
    public bool PlayersExpanded
    {
        get => _playersExpanded;
        set
        {
            if (!SetProperty(ref _playersExpanded, value)) return;
            OnPropertyChanged(nameof(ShowPlayers));
            OnPropertyChanged(nameof(PlayerListLabel));
        }
    }

    public bool ShowPlayers => PlayersExpanded && Players.Count > 0;

    public string PlayerListLabel => Players.Count == 0
        ? string.Empty
        : (PlayersExpanded ? "收起玩家（" : "展开玩家（") + Players.Count + "）";

    private static IReadOnlyList<string> BuildBadges(SaveSlotMetadata save)
    {
        var badges = new List<string>();

        if (save.Multiplayer is { } multiplayer)
        {
            badges.Add(KindLabel(save.Kind));
            if (multiplayer.IsCurrentProgress) badges.Add("= 当前进度");
            if (multiplayer.IsIncomplete) badges.Add("不完整");
            if (multiplayer.IsMidLevel is { } midLevel) badges.Add(midLevel ? "层中存档" : "层界存档");
            if (multiplayer.PlayerCount is { } playerCount
                && save.Kind != SaveArtifactKind.MultiplayerClientSnapshot)
            {
                badges.Add($"{playerCount} 人");
            }

            if (save.IsMostRecent) badges.Add("最近");
            if (save.Cheated) badges.Add("已修改");
            return badges;
        }

        badges.Add(KindLabel(save.Kind));
        if (save.IsEmptyFile) badges.Add("空文件");
        if (save.IsMostRecent) badges.Add("最近");
        if (save.IsMultiplayer) badges.Add("多人");
        if (save.Cheated) badges.Add("已修改");
        if (save.ModCount > 0) badges.Add($"模组 {save.ModCount}");
        return badges;
    }

    private static IReadOnlyList<SaveFact> BuildFacts(SaveSlotMetadata save)
    {
        if (save.Multiplayer is { } multiplayer)
            return BuildMultiplayerFacts(save, multiplayer);

        var facts = new List<SaveFact>();

        // 深度置顶：LocationName 里的“2158m Deep”与深度是同一份数据，只保留深度。
        if (save.DepthMeters is > 0) facts.Add(new($"深度：{save.DepthMeters:0} m"));
        if (!string.IsNullOrWhiteSpace(save.LayerName)) facts.Add(new($"层级：{save.LayerName}"));
        else if (save.LayerIndex is { } layer) facts.Add(new($"层级：第 {layer + 1} 层"));
        if (!string.IsNullOrWhiteSpace(save.DifficultyLabel))
        {
            var tip = string.Equals(save.DifficultyKey, SaveDifficultyPresets.CustomKey, StringComparison.Ordinal)
                ? SaveDifficultyPresets.CustomDescription
                : null;
            facts.Add(new($"难度：{save.DifficultyLabel}", tip));
        }
        if (!string.IsNullOrWhiteSpace(save.CharacterLabel)) facts.Add(new($"角色：{save.CharacterLabel}"));
        if (!string.IsNullOrWhiteSpace(save.RunTimeLabel)) facts.Add(new($"游玩：{save.RunTimeLabel}"));
        if (!string.IsNullOrWhiteSpace(save.SavedAtLabel)) facts.Add(new($"保存：{save.SavedAtLabel}"));
        if (save.CharacterId is { } characterId) facts.Add(new($"角色 ID：{characterId}"));
        if (save.SeedValue is { } seed) facts.Add(new($"种子：{seed}"));
        if (!string.IsNullOrWhiteSpace(save.SaveKind)) facts.Add(new($"类型：{save.SaveKind}"));
        if (facts.Count == 0 && save.IsReadable) facts.Add(new("无更多元数据"));
        return facts;
    }

    private static IReadOnlyList<SaveFact> BuildMultiplayerFacts(SaveSlotMetadata save, MultiplayerSaveInfo multiplayer)
    {
        var facts = new List<SaveFact>();

        if (save.LayerIndex is { } layer)
        {
            var layerText = string.IsNullOrWhiteSpace(save.LocationName)
                ? $"层级：第 {layer + 1} 层"
                : $"层级：第 {layer + 1} 层 · {save.LocationName}";
            facts.Add(new(layerText));
        }
        else if (!string.IsNullOrWhiteSpace(save.LocationName))
        {
            facts.Add(new($"层级：{save.LocationName}"));
        }

        if (!string.IsNullOrWhiteSpace(save.DifficultyLabel)) facts.Add(new($"难度：{save.DifficultyLabel}"));
        if (!string.IsNullOrWhiteSpace(save.RunTimeLabel)) facts.Add(new($"游玩：{save.RunTimeLabel}"));
        if (!string.IsNullOrWhiteSpace(save.SavedAtLabel))
        {
            var label = save.Kind == SaveArtifactKind.LiveMultiplayer ? "最后写入" : "保存";
            facts.Add(new($"{label}：{save.SavedAtLabel}"));
        }

        if (save.Kind != SaveArtifactKind.MultiplayerClientSnapshot && multiplayer.PlayerCount is { } playerCount)
            facts.Add(new($"人数：{playerCount} 人"));

        if (!string.IsNullOrWhiteSpace(multiplayer.MpVersion) || !string.IsNullOrWhiteSpace(multiplayer.GameVersion))
            facts.Add(new($"Together {multiplayer.MpVersion ?? "?"} · 游戏 {multiplayer.GameVersion ?? "?"}"));

        if (multiplayer.ModNames.Count > 0)
            facts.Add(new($"Mods：{multiplayer.ModNames.Count} 个", string.Join("\n", multiplayer.ModNames)));

        if (facts.Count == 0 && save.IsReadable) facts.Add(new("无更多元数据"));
        return facts;
    }

    private static string? BuildPlayerSummary(SaveSlotMetadata save)
    {
        var players = save.Multiplayer?.Players;
        if (players is null || players.Count == 0) return null;

        var names = string.Join("、", players.Take(5).Select(player => player.Name));
        return players.Count > 5 ? $"玩家：{names} 等 {players.Count} 人" : $"玩家：{names}";
    }

    private static string KindLabel(SaveArtifactKind kind) => kind switch
    {
        SaveArtifactKind.Slot => "槽位",
        SaveArtifactKind.Current => "当前进度",
        SaveArtifactKind.Autosave => "自动存档",
        SaveArtifactKind.SingleSave => "原版存档",
        SaveArtifactKind.MultiplayerHostSlot => "联机 · 房主存档",
        SaveArtifactKind.MultiplayerClientSnapshot => "联机 · 客机快照",
        SaveArtifactKind.LiveMultiplayer => "当前联机存档",
        _ => "文件"
    };

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB"
    };
}

/// <summary>One player row inside a multiplayer card: name, character, item count
/// and the first few item names resolved from the game's Lang table.</summary>
public sealed class MultiplayerPlayerRow
{
    public MultiplayerPlayerRow(MultiplayerPlayer player)
    {
        Name = string.IsNullOrWhiteSpace(player.Name) ? player.PersistentId : player.Name;
        NameTip = player.IsSteamId ? player.PersistentId : null;
        Items = player.ItemNames;
        ItemsMore = player.ItemCount > player.ItemNames.Count ? $"等 {player.ItemCount} 件" : null;
        Error = player.Error;

        var facts = new List<SaveFact>();
        if (!string.IsNullOrWhiteSpace(player.CharacterLabel)) facts.Add(new($"角色：{player.CharacterLabel}"));
        facts.Add(new($"物品 {player.ItemCount} 件"));
        if (player.CharacterId is { } characterId) facts.Add(new($"cId：{characterId}"));
        if (player.PositionX is { } x && player.PositionY is { } y) facts.Add(new($"坐标：{x:0.#}, {y:0.#}"));
        Facts = facts;
    }

    public string Name { get; }

    public string? NameTip { get; }

    public IReadOnlyList<SaveFact> Facts { get; }

    public IReadOnlyList<string> Items { get; }

    public string? ItemsMore { get; }

    public bool HasMoreItems => !string.IsNullOrEmpty(ItemsMore);

    public bool HasItems => Items.Count > 0;

    public string? Error { get; }

    public bool HasError => !string.IsNullOrWhiteSpace(Error);
}

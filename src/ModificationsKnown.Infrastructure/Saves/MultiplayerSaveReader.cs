using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ModificationsKnown.Core.Saves;

namespace ModificationsKnown.Infrastructure.Saves;

/// <summary>
/// Reads the multiplayer side of a save group: QoL host bundles
/// (<c>slot_N.mp\</c>), QoL client inventory snapshots (<c>slot_N.client.json</c>)
/// and the live <c>mp_save\</c> directory.
///
/// Everything is best-effort: a missing or broken file degrades one field (or
/// marks the entity incomplete) and never throws to the caller. Sizes and JSON
/// depth reuse <see cref="SaveGroupArchiveLimits"/> so a hostile directory cannot
/// make the page allocate unbounded memory, and every path is contained to the
/// group root.
/// </summary>
internal static class MultiplayerSaveReader
{
    public const string LiveFolderName = "mp_save";

    public const string RulesFileName = "mp_rules.json";

    public const string WorldgenStateFileName = "qol_worldgen_state.txt";

    public const string HostBundleSuffix = ".mp";

    public const string ClientSnapshotSuffix = ".client.json";

    public const int MaxItemNames = 8;

    /// <summary>Fills <paramref name="entry"/>.Multiplayer for a slot already classified
    /// as a host bundle or a client snapshot.</summary>
    public static void Enrich(SaveSlotMetadata entry, string groupRoot, SaveLanguage language)
    {
        var info = new MultiplayerSaveInfo
        {
            Kind = entry.Kind,
            ModNames = entry.MetaModNames,
            MpVersion = entry.MultiplayerVersion,
            GameVersion = entry.MultiplayerGameVersion
        };
        entry.Multiplayer = info;

        try
        {
            info.SaveId = ParseInt(entry.MultiplayerSaveId);

            if (entry.Kind == SaveArtifactKind.MultiplayerHostSlot)
            {
                EnrichHostSlot(entry, info, groupRoot, language);
                return;
            }

            if (entry.Kind == SaveArtifactKind.MultiplayerClientSnapshot)
            {
                EnrichClientSnapshot(entry, info, groupRoot, language);
            }
        }
        catch (Exception exception)
        {
            entry.Error ??= $"联机存档无法解析：{exception.Message}";
            info.IsIncomplete = entry.Kind == SaveArtifactKind.MultiplayerHostSlot;
        }
    }

    private static void EnrichHostSlot(
        SaveSlotMetadata entry,
        MultiplayerSaveInfo info,
        string groupRoot,
        SaveLanguage language)
    {
        var bundleName = string.IsNullOrWhiteSpace(entry.MultiplayerBundleName)
            ? "slot_" + entry.SlotNumber?.ToString(CultureInfo.InvariantCulture) + HostBundleSuffix
            : entry.MultiplayerBundleName;
        var bundle = SafeChild(groupRoot, bundleName);

        if (bundle is null || !Directory.Exists(bundle))
        {
            // A host bundle without its mp_save copy only keeps the host's own
            // progress; the run cannot be continued as multiplayer.
            info.IsIncomplete = true;
            return;
        }

        var rules = TryReadPlainJson(Path.Combine(bundle, RulesFileName));
        if (rules is null)
        {
            info.IsIncomplete = true;
            return;
        }

        ApplyRules(info, rules.Value);
        info.Players = ReadPlayers(bundle, rules.Value, language);
        info.PlayerCount ??= info.Players.Count;

        var statePath = Path.Combine(bundle, WorldgenStateFileName);
        if (File.Exists(statePath))
        {
            info.HasWorldgenState = true;
            try
            {
                info.WorldgenStateBytes = new FileInfo(statePath).Length;
            }
            catch (Exception)
            {
                // Size is cosmetic; keep the "present" flag.
            }
        }
    }

    private static void EnrichClientSnapshot(
        SaveSlotMetadata entry,
        MultiplayerSaveInfo info,
        string groupRoot,
        SaveLanguage language)
    {
        var snapshotName = string.IsNullOrWhiteSpace(entry.MultiplayerBundleName)
            ? "slot_" + entry.SlotNumber?.ToString(CultureInfo.InvariantCulture) + ClientSnapshotSuffix
            : entry.MultiplayerBundleName;
        var snapshot = SafeChild(groupRoot, snapshotName);
        var rawId = entry.LocalPersistentPlayerId;

        MultiplayerPersistentId.TryDecode(rawId, out var decoded, out var isSteam);
        info.PlayerName = decoded;

        var player = new MultiplayerPlayer
        {
            Name = decoded,
            PersistentId = rawId ?? string.Empty,
            IsSteamId = isSteam
        };

        var json = snapshot is null ? null : TryReadPlainJson(snapshot);
        if (json is { } payload)
        {
            ApplyPlayerPayload(player, payload, language);
        }
        else
        {
            player.Error = "角色快照无法读取。";
        }

        info.Players = [player];
        info.PlayerCount = 1;
    }

    /// <summary>Reads the live <c>mp_save\</c> directory, or null when it does not exist.</summary>
    public static SaveSlotMetadata? TryReadLive(string groupRoot, SaveLanguage language)
    {
        var live = SafeChild(groupRoot, LiveFolderName);
        if (live is null || !Directory.Exists(live)) return null;

        // Only the live layout at the root counts. A folder that merely holds an
        // old `_session` leftover must not surface as "当前联机存档".
        var hasSaveFile = File.Exists(Path.Combine(live, "save.sv"));
        var hasRulesFile = File.Exists(Path.Combine(live, RulesFileName));
        if (!hasSaveFile && !hasRulesFile) return null;

        var entry = new SaveSlotMetadata
        {
            Kind = SaveArtifactKind.LiveMultiplayer,
            Label = "当前联机存档",
            FilePath = live,
            FileName = LiveFolderName,
            IsMultiplayer = true
        };

        var info = new MultiplayerSaveInfo
        {
            Kind = SaveArtifactKind.LiveMultiplayer,
            IsIncomplete = !hasSaveFile
        };
        entry.Multiplayer = info;

        try
        {
            info.LastWrite = NewestWrite(live);
            entry.SavedAt = info.LastWrite;

            var rules = TryReadPlainJson(Path.Combine(live, RulesFileName));
            if (rules is null)
            {
                info.IsIncomplete = true;
            }
            else
            {
                ApplyRules(info, rules.Value);
                info.Players = ReadPlayers(live, rules.Value, language);
                info.PlayerCount ??= info.Players.Count;
            }

            // The live save.sv carries the world/host body facts the card shows.
            var hostFile = Path.Combine(live, "save.sv");
            if (File.Exists(hostFile))
            {
                var host = new SaveSlotMetadata { FilePath = hostFile, FileName = "save.sv" };
                SaveSlotReader.ReadContent(host);
                entry.LayerIndex = host.LayerIndex;
                entry.IgtSeconds = host.IgtSeconds;
                entry.DifficultyKey = host.DifficultyKey;
                entry.CharacterValue = host.CharacterValue;
                entry.CharacterId = host.CharacterId;
                entry.Cheated = host.Cheated;
                entry.IsReadable = host.IsReadable;
                entry.Error = host.Error;
            }
            else
            {
                entry.IsReadable = false;
                entry.Error ??= "当前联机目录里没有角色存档。";
            }
        }
        catch (Exception exception)
        {
            entry.Error ??= $"当前联机存档无法解析：{exception.Message}";
        }

        return entry;
    }

    /// <summary>True when the live directory and a host bundle contain the exact same
    /// file set (relative paths) with identical contents.</summary>
    public static bool BundlesEqual(string liveDirectory, string bundleDirectory)
    {
        try
        {
            if (!Directory.Exists(liveDirectory) || !Directory.Exists(bundleDirectory)) return false;

            var liveFiles = RelativeFiles(liveDirectory);
            var bundleFiles = RelativeFiles(bundleDirectory);
            if (liveFiles.Count == 0 || liveFiles.Count != bundleFiles.Count) return false;

            for (var index = 0; index < liveFiles.Count; index++)
            {
                if (!string.Equals(liveFiles[index], bundleFiles[index], StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            foreach (var relative in liveFiles)
            {
                if (!FileHashesEqual(
                        Path.Combine(liveDirectory, relative),
                        Path.Combine(bundleDirectory, relative)))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void ApplyRules(MultiplayerSaveInfo info, JsonElement rules)
    {
        if (rules.ValueKind != JsonValueKind.Object) return;

        info.SaveId ??= ParseInt(ReadString(Find(rules, "SAVEID", "SaveId")));
        info.ModifierIndex = ParseInt(ReadString(Find(rules, "MODIFIER", "Modifier")));
        info.MpVersion ??= ReadString(Find(rules, "MPVERSION", "MpVersion"));
        info.GameVersion ??= ReadString(Find(rules, "GAMEVERSION", "GameVersion"));

        if (Find(rules, "RULES", "Rules") is { ValueKind: JsonValueKind.Object } ruleSet)
        {
            info.RuleCount = ruleSet.EnumerateObject().Count();
        }

        if (Find(rules, "LEVEL", "Level") is { } level)
        {
            info.IsMidLevel = level.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => info.IsMidLevel
            };
        }

        if (Find(rules, "PLRPOS", "PlrPos") is { ValueKind: JsonValueKind.Object } positions)
        {
            info.PlayerCount = positions.EnumerateObject().Count();
        }
    }

    private static List<MultiplayerPlayer> ReadPlayers(string directory, JsonElement rules, SaveLanguage language)
    {
        var players = new List<MultiplayerPlayer>();
        string[] children;
        try
        {
            children = Directory.GetDirectories(directory);
        }
        catch (Exception)
        {
            return players;
        }

        foreach (var child in children)
        {
            var id = Path.GetFileName(child);
            if (!id.StartsWith(MultiplayerPersistentId.NamePrefix, StringComparison.OrdinalIgnoreCase)
                && !id.StartsWith(MultiplayerPersistentId.SteamPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsReparsePoint(child)) continue;

            MultiplayerPersistentId.TryDecode(id, out var name, out var isSteam);
            var player = new MultiplayerPlayer { Name = name, PersistentId = id, IsSteamId = isSteam };

            var saveFile = Path.Combine(child, "save.sv");
            if (!File.Exists(saveFile))
            {
                player.Error = "缺少角色存档。";
                players.Add(player);
                continue;
            }

            var payload = TryReadSaveJson(saveFile);
            if (payload is null)
            {
                player.Error = "角色存档无法读取。";
                players.Add(player);
                continue;
            }

            ApplyPlayerPayload(player, payload.Value, language);
            ApplyPosition(player, rules);
            players.Add(player);
        }

        players.Sort((left, right) => string.CompareOrdinal(left.PersistentId, right.PersistentId));
        return players;
    }

    private static void ApplyPlayerPayload(MultiplayerPlayer player, JsonElement root, SaveLanguage language)
    {
        var payload = root;

        if (ReadDouble(Find(payload, "character", "Character")) is { } character)
        {
            player.CharacterValue = character;
            player.CharacterLabel = language.CharacterLabel(character);
        }

        player.CharacterId = ParseInt(ReadString(Find(payload, "cId", "characterId"))
            , ReadInt(Find(payload, "cId", "characterId")));

        if (Find(payload, "items", "Items") is { ValueKind: JsonValueKind.Array } items)
        {
            player.ItemCount = items.GetArrayLength();
            player.ItemNames = items
                .EnumerateArray()
                .Take(MaxItemNames)
                .Select(item => language.ItemName(ReadString(Find(item, "id", "Id", "itemId")) ?? string.Empty))
                .Where(name => !string.IsNullOrEmpty(name))
                .ToList();
        }
    }

    private static void ApplyPosition(MultiplayerPlayer player, JsonElement rules)
    {
        if (Find(rules, "PLRPOS", "PlrPos") is not { ValueKind: JsonValueKind.Object } positions) return;
        if (!positions.TryGetProperty(player.PersistentId, out var position)) return;

        player.PositionX = ReadDouble(Find(position, "Item1", "x", "X"));
        player.PositionY = ReadDouble(Find(position, "Item2", "y", "Y"));
    }

    private static JsonElement? TryReadPlainJson(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var info = new FileInfo(path);
            if (info.Length > SaveGroupArchiveLimits.MaxManifestBytes || IsReparsePoint(path)) return null;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var document = JsonDocument.Parse(
                stream,
                new JsonDocumentOptions { MaxDepth = SaveGroupArchiveLimits.MaxSaveJsonDepth });
            return document.RootElement.Clone();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static JsonElement? TryReadSaveJson(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > SaveGroupArchiveLimits.MaxSaveFileBytes || IsReparsePoint(path)) return null;

            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            using var limited = new CappedReadStream(
                gzip, SaveGroupArchiveLimits.MaxSaveJsonBytes, "联机角色存档");
            using var document = JsonDocument.Parse(
                limited,
                new JsonDocumentOptions { MaxDepth = SaveGroupArchiveLimits.MaxSaveJsonDepth });
            return document.RootElement.Clone();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static List<string> RelativeFiles(string root)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string[] children;
            try
            {
                children = Directory.GetDirectories(directory);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var child in children)
            {
                if (IsReparsePoint(child)) continue;
                pending.Push(child);
            }

            string[] entries;
            try
            {
                entries = Directory.GetFiles(directory);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var file in entries)
            {
                if (IsReparsePoint(file)) continue;
                files.Add(Path.GetRelativePath(root, file));
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    private static bool FileHashesEqual(string left, string right)
    {
        try
        {
            return string.Equals(Hash(left), Hash(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static DateTimeOffset? NewestWrite(string directory)
    {
        DateTimeOffset? newest = null;
        foreach (var file in RelativeFiles(directory))
        {
            try
            {
                var stamp = new DateTimeOffset(File.GetLastWriteTimeUtc(Path.Combine(directory, file)), TimeSpan.Zero);
                if (newest is null || stamp > newest) newest = stamp;
            }
            catch (Exception)
            {
                // Ignore a file we cannot stat.
            }
        }

        return newest;
    }

    private static string? SafeChild(string root, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        try
        {
            var rootFull = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var combined = Path.GetFullPath(Path.Combine(rootFull, relative));
            return combined.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? combined
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static JsonElement? Find(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value)) return value;
        }

        return null;
    }

    private static string? ReadString(JsonElement? element) => element?.ValueKind switch
    {
        JsonValueKind.String => element.Value.GetString(),
        JsonValueKind.Number => element.Value.GetRawText(),
        _ => null
    };

    private static double? ReadDouble(JsonElement? element) => element?.ValueKind switch
    {
        JsonValueKind.Number when element.Value.TryGetDouble(out var number) => number,
        JsonValueKind.String when double.TryParse(
            element.Value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null
    };

    private static int? ReadInt(JsonElement? element) =>
        element?.ValueKind == JsonValueKind.Number && element.Value.TryGetInt32(out var number) ? number : null;

    private static int? ParseInt(string? text, int? fallback = null) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
}

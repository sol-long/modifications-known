using System;
using System.Collections.Generic;
using System.Text;

namespace ModificationsKnown.Core.Saves;

/// <summary>
/// Multiplayer facts for one entity: a QoL host bundle, a QoL client snapshot or
/// the live <c>mp_save</c> directory. Everything here is optional: a missing
/// <c>mp_rules.json</c> or an unreadable player file degrades a single field, it
/// never throws.
/// </summary>
public sealed class MultiplayerSaveInfo
{
    public SaveArtifactKind Kind { get; init; }

    /// <summary>A host bundle without its <c>slot_N.mp\</c> folder. Client snapshots
    /// never carry a world, so this stays false for them.</summary>
    public bool IsIncomplete { get; set; }

    /// <summary>The live run is byte-identical to this host bundle.</summary>
    public bool IsCurrentProgress { get; set; }

    /// <summary><c>LEVEL</c> from <c>mp_rules.json</c>: true when the save was made
    /// mid-layer, false when it sits on a layer boundary.</summary>
    public bool? IsMidLevel { get; set; }

    /// <summary>Client snapshot owner, decoded from <c>LocalPersistentPlayerId</c>.</summary>
    public string? PlayerName { get; set; }

    public int? PlayerCount { get; set; }

    public int? SaveId { get; set; }

    public int? ModifierIndex { get; set; }

    public int? RuleCount { get; set; }

    public string? MpVersion { get; set; }

    public string? GameVersion { get; set; }

    public bool HasWorldgenState { get; set; }

    public long? WorldgenStateBytes { get; set; }

    public IReadOnlyList<string> ModNames { get; set; } = [];

    public IReadOnlyList<MultiplayerPlayer> Players { get; set; } = [];

    /// <summary>Newest file write time inside the live directory (live only).</summary>
    public DateTimeOffset? LastWrite { get; set; }
}

public sealed class MultiplayerPlayer
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Raw directory name (<c>NAME_…</c>/<c>STEAM_…</c>).</summary>
    public string PersistentId { get; set; } = string.Empty;

    public bool IsSteamId { get; set; }

    public double? CharacterValue { get; set; }

    public string? CharacterLabel { get; set; }

    public int ItemCount { get; set; }

    /// <summary>First few resolved item names; <see cref="ItemCount"/> keeps the total.</summary>
    public IReadOnlyList<string> ItemNames { get; set; } = [];

    public int? CharacterId { get; set; }

    public double? PositionX { get; set; }

    public double? PositionY { get; set; }

    public string? Error { get; set; }
}

/// <summary>Turns Together's persistent ids into display names.</summary>
public static class MultiplayerPersistentId
{
    public const string NamePrefix = "NAME_";

    public const string SteamPrefix = "STEAM_";

    /// <summary>
    /// <c>NAME_&lt;hex&gt;</c> decodes to the UTF-8 player name; a broken hex keeps the
    /// raw id so the row is still identifiable. <c>STEAM_</c> stays raw by design —
    /// the save does not contain the persona name.
    /// </summary>
    public static bool TryDecode(string? persistentId, out string name, out bool isSteam)
    {
        name = persistentId ?? string.Empty;
        isSteam = false;
        if (string.IsNullOrEmpty(persistentId)) return false;

        if (persistentId.StartsWith(SteamPrefix, StringComparison.OrdinalIgnoreCase))
        {
            isSteam = true;
            return true;
        }

        if (!persistentId.StartsWith(NamePrefix, StringComparison.OrdinalIgnoreCase)) return false;

        var hex = persistentId[NamePrefix.Length..];
        if (hex.Length == 0 || hex.Length % 2 != 0) return false;

        try
        {
            var bytes = Convert.FromHexString(hex);
            var decoded = Encoding.UTF8.GetString(bytes);
            if (string.IsNullOrEmpty(decoded)) return false;
            name = decoded;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

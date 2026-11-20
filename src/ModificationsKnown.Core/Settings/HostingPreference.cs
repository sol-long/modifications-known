namespace ModificationsKnown.Core.Settings;

/// <summary>
/// How the persisted hosting switch was read. <c>MissingFile</c> is a genuinely
/// new install; <c>MissingField</c> is a settings file written before the
/// switch existed; <c>Invalid</c>/<c>ParseFailed</c> must never be treated as
/// “no settings”.
/// </summary>
public enum HostingPreferenceSource
{
    MissingFile,
    MissingField,
    Enabled,
    Disabled,
    Invalid,
    ParseFailed
}

/// <summary>
/// Field-level result of reading <c>hostingEnabled</c>. <paramref name="Value"/>
/// is null when the field was absent or unusable; <paramref name="Source"/>
/// decides how the host bootstraps, so a corrupt file is not mistaken for a
/// fresh install.
/// </summary>
public sealed record HostingPreference(
    bool? Value,
    HostingPreferenceSource Source,
    string? CorruptBackupPath = null);

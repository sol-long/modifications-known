namespace ModificationsKnown.Core.Saves;

/// <summary>
/// The committed hosting mode. <c>PendingRecovery</c> covers both “settings are
/// unreadable but the live link still points at an MK group” and “a hosting
/// transaction was interrupted”; all save-group writes and MK launches are
/// blocked until the user resolves it.
/// </summary>
public enum SaveHostingState
{
    Disabled,
    Enabled,
    PendingRecovery,
    Transition,
    Unknown
}

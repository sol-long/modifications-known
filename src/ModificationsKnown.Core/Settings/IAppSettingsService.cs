namespace ModificationsKnown.Core.Settings;

public interface IAppSettingsService
{
    AppSettings Current { get; }

    /// <summary>Field-level read result for the hosting switch. Implementations
    /// that do not persist settings report <see cref="HostingPreferenceSource.MissingFile"/>.</summary>
    HostingPreference Hosting { get; }

    event EventHandler? Changed;

    Task LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(CancellationToken cancellationToken = default);
}

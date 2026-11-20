using System.Text.Json;
using System.Text.Json.Nodes;
using ModificationsKnown.Core.Settings;
using ModificationsKnown.Infrastructure.Storage;

namespace ModificationsKnown.Infrastructure.Storage;

public sealed class JsonAppSettingsService : IAppSettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly string _settingsFile;

    public JsonAppSettingsService(string? settingsFile = null)
    {
        _settingsFile = settingsFile ?? AppPaths.SettingsFile;
    }

    public AppSettings Current { get; private set; } = new();

    public HostingPreference Hosting { get; private set; } =
        new(null, HostingPreferenceSource.MissingFile);

    public event EventHandler? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        EnsureStorage();

        if (!File.Exists(_settingsFile))
        {
            Current = new AppSettings { HostingEnabled = false };
            Hosting = new HostingPreference(false, HostingPreferenceSource.MissingFile);
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(_settingsFile, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The file exists but cannot be read: never call this a new install.
            Current = new AppSettings();
            Hosting = new HostingPreference(null, HostingPreferenceSource.ParseFailed);
            return;
        }

        var field = InspectHostingField(json, out var sanitizedJson);
        if (field.Source == HostingPreferenceSource.ParseFailed)
        {
            Current = new AppSettings();
            Hosting = field with { CorruptBackupPath = TryBackupCorrupt("corrupt") };
            return;
        }

        if (field.Source == HostingPreferenceSource.Invalid)
        {
            // Keep the usable settings, but never silently decide whether the
            // switch is on or off. The original file is preserved verbatim.
            try
            {
                Current = JsonSerializer.Deserialize<AppSettings>(sanitizedJson, SerializerOptions)
                          ?? new AppSettings();
                Hosting = field with { CorruptBackupPath = TryBackupCorrupt("invalid-hosting") };
            }
            catch (Exception)
            {
                Current = new AppSettings();
                Hosting = new HostingPreference(
                    null, HostingPreferenceSource.ParseFailed, TryBackupCorrupt("corrupt"));
            }

            return;
        }

        try
        {
            Current = JsonSerializer.Deserialize<AppSettings>(sanitizedJson, SerializerOptions)
                      ?? new AppSettings();
        }
        catch (Exception)
        {
            Current = new AppSettings();
            Hosting = new HostingPreference(
                null, HostingPreferenceSource.ParseFailed, TryBackupCorrupt("corrupt"));
            return;
        }

        Hosting = field;

        // Old settings files predate the switch: preserve the previous
        // "MK manages the live folder" behaviour and persist that decision once.
        if (field.Source == HostingPreferenceSource.MissingField)
        {
            Current.HostingEnabled = true;
            Hosting = new HostingPreference(true, HostingPreferenceSource.MissingField);
        }

        // New installs default to the moss theme again. The removed ember
        // preset falls back to moss; explicit daylight/slate/custom choices
        // stay as chosen.
        try
        {
            if (!Current.ThemePresetMigrated)
            {
                if (string.IsNullOrWhiteSpace(Current.ThemePreset)
                    || string.Equals(Current.ThemePreset, "ember", StringComparison.OrdinalIgnoreCase))
                {
                    Current.ThemePreset = "moss";
                }

                Current.ThemePresetMigrated = true;
                await SaveAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (field.Source == HostingPreferenceSource.MissingField)
            {
                await SaveAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // A failed migration save is retried on the next start; the
            // in-memory value still reflects the documented default.
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureStorage();
            var json = JsonSerializer.Serialize(Current, SerializerOptions);
            var temporaryFile = _settingsFile + ".tmp";

            await File.WriteAllTextAsync(temporaryFile, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryFile, _settingsFile, overwrite: true);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void EnsureStorage()
    {
        if (string.Equals(_settingsFile, AppPaths.SettingsFile, StringComparison.OrdinalIgnoreCase))
        {
            AppPaths.EnsureCreated();
            return;
        }

        var directory = Path.GetDirectoryName(_settingsFile);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
    }

    /// <summary>
    /// Reads only <c>hostingEnabled</c>. Missing means “old settings file”,
    /// null means the same, and a wrong JSON type is reported as invalid
    /// instead of letting the whole deserialization throw (which would reset
    /// every other setting).
    /// </summary>
    private static HostingPreference InspectHostingField(string json, out string sanitizedJson)
    {
        sanitizedJson = json;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return new HostingPreference(null, HostingPreferenceSource.ParseFailed);

            JsonElement? value = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "hostingEnabled", StringComparison.OrdinalIgnoreCase))
                    continue;

                value = property.Value;
                break;
            }

            if (value is null) return new HostingPreference(null, HostingPreferenceSource.MissingField);

            switch (value.Value.ValueKind)
            {
                case JsonValueKind.True:
                    return new HostingPreference(true, HostingPreferenceSource.Enabled);
                case JsonValueKind.False:
                    return new HostingPreference(false, HostingPreferenceSource.Disabled);
                case JsonValueKind.Null:
                    return new HostingPreference(null, HostingPreferenceSource.MissingField);
            }

            // Strip the bad value so the other settings still deserialize.
            if (JsonNode.Parse(json) is JsonObject root)
            {
                foreach (var property in root.Select(pair => pair.Key).ToList())
                {
                    if (string.Equals(property, "hostingEnabled", StringComparison.OrdinalIgnoreCase))
                        root.Remove(property);
                }

                sanitizedJson = root.ToJsonString(SerializerOptions);
            }

            return new HostingPreference(null, HostingPreferenceSource.Invalid);
        }
        catch (Exception)
        {
            return new HostingPreference(null, HostingPreferenceSource.ParseFailed);
        }
    }

    private string? TryBackupCorrupt(string suffix)
    {
        try
        {
            var directory = Path.GetDirectoryName(_settingsFile);
            if (string.IsNullOrEmpty(directory)) return null;

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var target = Path.Combine(directory, $"settings.json.{suffix}-{stamp}.json");
            var index = 2;
            while (File.Exists(target))
                target = Path.Combine(directory, $"settings.json.{suffix}-{stamp}-{index++}.json");

            File.Copy(_settingsFile, target, overwrite: false);
            return target;
        }
        catch
        {
            return null;
        }
    }
}

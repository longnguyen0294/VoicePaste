using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using VoicePaste.Core;

namespace VoicePaste.Windows.Infrastructure;

public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _settingsPath;

    public JsonSettingsStore(string? settingsPath = null)
    {
        _settingsPath = Path.GetFullPath(settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VoicePaste",
            "settings.json"));
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_settingsPath))
        {
            return AppSettings.Default;
        }

        try
        {
            await using var stream = new FileStream(
                _settingsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(
                    stream,
                    SerializerOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            return Validate(settings);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return AppSettings.Default;
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var validated = Validate(settings);
        var directory = Path.GetDirectoryName(_settingsPath)
            ?? throw new InvalidOperationException("Settings path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"settings-{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        validated,
                        SerializerOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static AppSettings Validate(AppSettings? settings)
    {
        if (settings is null ||
            settings.SchemaVersion != AppSettings.CurrentSchemaVersion ||
            settings.Hotkey is null ||
            settings.CancelHotkey is null ||
            settings.LocaleHints is null)
        {
            return AppSettings.Default;
        }

        if (HotkeyBindingValidator.Validate(settings.Hotkey, settings.CancelHotkey)
                is OperationFailure<HotkeyGesture> ||
            HotkeyBindingValidator.Validate(settings.CancelHotkey, settings.Hotkey)
                is OperationFailure<HotkeyGesture>)
        {
            return AppSettings.Default;
        }

        var localeHints = settings.LocaleHints
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (localeHints.Length == 0)
        {
            localeHints = AppSettings.Default.LocaleHints.ToArray();
        }

        return settings with
        {
            MicrophoneId = string.IsNullOrWhiteSpace(settings.MicrophoneId)
                ? AppSettings.Default.MicrophoneId
                : settings.MicrophoneId,
            ProviderId = string.IsNullOrWhiteSpace(settings.ProviderId) ||
                         string.Equals(
                             settings.ProviderId,
                             "pending-provider-evaluation",
                             StringComparison.OrdinalIgnoreCase)
                ? AppSettings.Default.ProviderId
                : settings.ProviderId,
            LocaleHints = localeHints,
            HistoryEnabled = false,
            CredentialReference = string.IsNullOrWhiteSpace(settings.CredentialReference)
                ? AppSettings.Default.CredentialReference
                : settings.CredentialReference,
        };
    }
}

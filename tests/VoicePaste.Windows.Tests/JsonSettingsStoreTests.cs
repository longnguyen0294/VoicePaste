using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VoicePaste.Core;
using VoicePaste.Windows.Infrastructure;

namespace VoicePaste.Windows.Tests;

[TestClass]
public sealed class JsonSettingsStoreTests
{
    [TestMethod]
    public async Task UtSet001RoundTripsValidatedSettingsWithoutSecretMaterial()
    {
        var root = Path.Combine(Path.GetTempPath(), "VoicePaste.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "settings.json");
            var store = new JsonSettingsStore(path);
            var expected = AppSettings.Default with
            {
                MicrophoneId = "microphone-1",
                CredentialReference = "provider-key",
            };

            await store.SaveAsync(expected, CancellationToken.None);
            var actual = await store.LoadAsync(CancellationToken.None);
            var serialized = await File.ReadAllTextAsync(path);

            Assert.AreEqual(expected.MicrophoneId, actual.MicrophoneId);
            Assert.AreEqual(expected.CredentialReference, actual.CredentialReference);
            Assert.IsFalse(serialized.Contains("api-key-value", StringComparison.Ordinal));
        }
        finally
        {
            var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VoicePaste.Tests")) +
                                 Path.DirectorySeparatorChar;
            var resolved = Path.GetFullPath(root);
            if (resolved.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(resolved, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task UtSet002InvalidNullMembersFallBackToSafeDefaults()
    {
        var root = Path.Combine(Path.GetTempPath(), "VoicePaste.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "settings.json");
            await File.WriteAllTextAsync(
                path,
                "{\"schemaVersion\":1,\"hotkey\":null,\"cancelHotkey\":null,\"localeHints\":null}");

            var actual = await new JsonSettingsStore(path).LoadAsync(CancellationToken.None);

            Assert.AreEqual(AppSettings.Default, actual);
        }
        finally
        {
            var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VoicePaste.Tests")) +
                                 Path.DirectorySeparatorChar;
            var resolved = Path.GetFullPath(root);
            if (resolved.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(resolved, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task UtSet003RoundTripsLeftControlHotkeySetting()
    {
        var root = Path.Combine(Path.GetTempPath(), "VoicePaste.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "settings.json");
            var store = new JsonSettingsStore(path);
            var expected = AppSettings.Default with
            {
                Hotkey = HotkeyGesture.LeftControl,
            };

            await store.SaveAsync(expected, CancellationToken.None);
            var actual = await store.LoadAsync(CancellationToken.None);

            Assert.AreEqual(expected.Hotkey, actual.Hotkey);
            Assert.AreEqual(KeySide.Left, actual.Hotkey.Trigger.Side);
            Assert.IsFalse(actual.Hotkey.Trigger.IsExtended);
        }
        finally
        {
            var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VoicePaste.Tests")) +
                                 Path.DirectorySeparatorChar;
            var resolved = Path.GetFullPath(root);
            if (resolved.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(resolved, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task UtSet004RoundTripsRealtimeDictationModeSetting()
    {
        var root = Path.Combine(Path.GetTempPath(), "VoicePaste.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "settings.json");
            var store = new JsonSettingsStore(path);
            var expected = AppSettings.Default with
            {
                DictationMode = DictationMode.Realtime,
            };

            await store.SaveAsync(expected, CancellationToken.None);
            var actual = await store.LoadAsync(CancellationToken.None);

            Assert.AreEqual(DictationMode.Realtime, actual.DictationMode);
        }
        finally
        {
            var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VoicePaste.Tests")) +
                                 Path.DirectorySeparatorChar;
            var resolved = Path.GetFullPath(root);
            if (resolved.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(resolved, recursive: true);
            }
        }
    }
}

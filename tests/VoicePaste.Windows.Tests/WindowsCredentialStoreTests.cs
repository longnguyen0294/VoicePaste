using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VoicePaste.Windows.Infrastructure;

namespace VoicePaste.Windows.Tests;

[TestClass]
public sealed class WindowsCredentialStoreTests
{
    [TestMethod]
    public async Task ItCred001EnumeratesMetadataAndDeletesOnlyTheSelectedVoicePasteCredential()
    {
        const string secret = "not-a-real-provider-secret";
        var reference = $"test-{Guid.NewGuid():N}";
        var store = new WindowsCredentialStore();

        await store.SaveAsync(reference, secret, CancellationToken.None);
        try
        {
            var credentials = await store.ListAsync(CancellationToken.None);
            var descriptor = credentials.Single(item => item.Reference == reference);

            Assert.IsNotNull(descriptor.LastWritten);
            Assert.IsFalse(JsonSerializer.Serialize(descriptor).Contains(secret, StringComparison.Ordinal));
            Assert.AreEqual(secret, await store.ReadAsync(reference, CancellationToken.None));

            await store.DeleteAsync(reference, CancellationToken.None);

            Assert.IsNull(await store.ReadAsync(reference, CancellationToken.None));
            Assert.IsFalse((await store.ListAsync(CancellationToken.None))
                .Any(item => item.Reference == reference));
        }
        finally
        {
            await store.DeleteAsync(reference, CancellationToken.None);
        }
    }
}

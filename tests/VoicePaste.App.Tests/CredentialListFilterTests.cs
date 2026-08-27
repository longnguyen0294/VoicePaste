using Microsoft.VisualStudio.TestTools.UnitTesting;
using VoicePaste.App.UI;
using VoicePaste.Core;

namespace VoicePaste.App.Tests;

[TestClass]
public sealed class CredentialListFilterTests
{
    [TestMethod]
    public void UtCred001FiltersNamesAndMarksTheActiveReferenceWithoutSecrets()
    {
        var credentials = new[]
        {
            new StoredCredentialDescriptor("azure-speech-key", new DateTimeOffset(2026, 8, 1, 2, 3, 0, TimeSpan.Zero)),
            new StoredCredentialDescriptor("openai-api-key", new DateTimeOffset(2026, 8, 2, 3, 4, 0, TimeSpan.Zero)),
        };

        var result = CredentialListFilter.Apply(
            credentials,
            "OPENAI",
            "openai-api-key");

        Assert.HasCount(1, result);
        Assert.AreEqual("openai-api-key", result[0].Reference);
        Assert.IsTrue(result[0].IsActive);
        StringAssert.Contains(result[0].DisplayText, "active");
        Assert.AreEqual(result[0].DisplayText, result[0].ToString());
        Assert.IsFalse(result[0].DisplayText.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }
}

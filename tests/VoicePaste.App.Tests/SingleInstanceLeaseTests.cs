using Microsoft.VisualStudio.TestTools.UnitTesting;
using VoicePaste.App.Composition;

namespace VoicePaste.App.Tests;

[TestClass]
public sealed class SingleInstanceLeaseTests
{
    [TestMethod]
    public void UtLife001AllowsOnlyOneLiveLeasePerWindowsSession()
    {
        var mutexName = $@"Local\VoicePaste.Tests.{Guid.NewGuid():N}";

        Assert.IsTrue(SingleInstanceLease.TryAcquire(mutexName, out var first));
        Assert.IsNotNull(first);
        using (first)
        {
            Assert.IsFalse(SingleInstanceLease.TryAcquire(mutexName, out var duplicate));
            Assert.IsNull(duplicate);
        }

        Assert.IsTrue(SingleInstanceLease.TryAcquire(mutexName, out var replacement));
        Assert.IsNotNull(replacement);
        replacement.Dispose();
    }
}

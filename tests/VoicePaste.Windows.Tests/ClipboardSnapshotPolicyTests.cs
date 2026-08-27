using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VoicePaste.Core;
using VoicePaste.Windows.Insertion;

namespace VoicePaste.Windows.Tests;

[TestClass]
public sealed class ClipboardSnapshotPolicyTests
{
    [TestMethod]
    public void UtClip003MaterializesSupportedValues()
    {
        using var stream = new MemoryStream([1, 2, 3]);
        var result = ClipboardSnapshotPolicy.Create(
        [
            new KeyValuePair<string, object?>("UnicodeText", "Xin chào"),
            new KeyValuePair<string, object?>("Binary", stream),
        ]);

        var success = result as OperationSuccess<ClipboardSnapshot>;
        Assert.IsNotNull(success);
        Assert.AreEqual("Xin chào", success.Value.Values["UnicodeText"]);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, (byte[])success.Value.Values["Binary"]);
    }

    [TestMethod]
    public void UtClip003RejectsUnsupportedAndOversizedValues()
    {
        var unsupported = ClipboardSnapshotPolicy.Create(
        [
            new KeyValuePair<string, object?>("Bitmap", new object()),
        ]);
        var oversized = ClipboardSnapshotPolicy.Create(
        [
            new KeyValuePair<string, object?>(
                "Binary",
                new byte[ClipboardSnapshotPolicy.MaximumSnapshotBytes + 1]),
        ]);

        Assert.IsInstanceOfType<OperationFailure<ClipboardSnapshot>>(unsupported);
        Assert.IsInstanceOfType<OperationFailure<ClipboardSnapshot>>(oversized);
    }
}


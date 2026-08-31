using Microsoft.VisualStudio.TestTools.UnitTesting;
using VoicePaste.Windows.Insertion;

namespace VoicePaste.Windows.Tests;

[TestClass]
public sealed class UnicodeTextInputTests
{
    [TestMethod]
    public void UtInsert002BuildsUnicodeKeyPairsWithoutAddingClipboardFormatting()
    {
        const string text = "N\u00e3y n\u00f3 hi\u1ec3u l\u00e0 gi\u1eb7t xong m\u1eb7c cho ng\u01b0\u1eddi b\u1ecb vi\u00eam da c\u01a1 \u0111\u1ecba.";

        var events = UnicodeTextInput.CreateEvents(text);

        Assert.AreEqual(text.Length * 2, events.Count);
        for (var index = 0; index < text.Length; index++)
        {
            var keyDown = events[index * 2];
            var keyUp = events[index * 2 + 1];

            Assert.AreEqual((ushort)text[index], keyDown.ScanCode);
            Assert.AreEqual((ushort)text[index], keyUp.ScanCode);
            Assert.AreEqual(UnicodeTextInput.UnicodeFlag, keyDown.Flags);
            Assert.AreEqual(
                UnicodeTextInput.UnicodeFlag | UnicodeTextInput.KeyUpFlag,
                keyUp.Flags);
        }
    }

    [TestMethod]
    public void UtInsert002ReturnsNoEventsForEmptyText()
    {
        CollectionAssert.AreEqual(
            Array.Empty<UnicodeInputEvent>(),
            UnicodeTextInput.CreateEvents(string.Empty).ToArray());
    }
}

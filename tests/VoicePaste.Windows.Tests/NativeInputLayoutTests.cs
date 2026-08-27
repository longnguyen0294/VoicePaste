using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VoicePaste.Windows.Insertion;

namespace VoicePaste.Windows.Tests;

[TestClass]
public sealed class NativeInputLayoutTests
{
    [TestMethod]
    public void UtInsert001UsesNativeInputSizeForCurrentArchitecture()
    {
        var inputType = typeof(WindowsTextInsertionService).GetNestedType(
            "KeyboardInput",
            BindingFlags.NonPublic);
        var unionType = typeof(WindowsTextInsertionService).GetNestedType(
            "InputUnion",
            BindingFlags.NonPublic);

        Assert.IsNotNull(inputType);
        Assert.IsNotNull(unionType);
        Assert.AreEqual(Environment.Is64BitProcess ? 40 : 28, Marshal.SizeOf(inputType));
        Assert.AreEqual(Environment.Is64BitProcess ? 32 : 24, Marshal.SizeOf(unionType));
    }
}

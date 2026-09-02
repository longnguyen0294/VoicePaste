using Microsoft.VisualStudio.TestTools.UnitTesting;
using VoicePaste.Core;

namespace VoicePaste.Core.Tests;

[TestClass]
public sealed class PolicyTests
{
    [TestMethod]
    public void UtHotkey002RejectsUnmodifiedLetterAndConflictingBinding()
    {
        var letter = new HotkeyGesture(
            new PhysicalKey(VirtualKey.A, 0x1E, IsExtended: false, KeySide.None),
            KeyModifiers.None);

        var unsafeResult = HotkeyBindingValidator.Validate(letter);
        var conflictResult = HotkeyBindingValidator.Validate(AppSettings.Default.Hotkey, AppSettings.Default.Hotkey);

        Assert.IsInstanceOfType<OperationFailure<HotkeyGesture>>(unsafeResult);
        Assert.IsInstanceOfType<OperationFailure<HotkeyGesture>>(conflictResult);
    }

    [TestMethod]
    public void UtHotkey003DefaultBindingIsExtendedRightControl()
    {
        var key = AppSettings.Default.Hotkey.Trigger;

        Assert.AreEqual(VirtualKey.Control, key.VirtualKey);
        Assert.AreEqual(KeySide.Right, key.Side);
        Assert.IsTrue(key.IsExtended);
        Assert.AreEqual(0x1D, key.ScanCode);
    }

    [TestMethod]
    public void UtHotkey004LeftControlGestureIsValidAndDistinguishableFromRightControl()
    {
        var left = HotkeyGesture.LeftControl;
        var right = HotkeyGesture.RightControl;
        var escape = HotkeyGesture.Escape;

        Assert.AreNotEqual(left, right);
        Assert.AreEqual(VirtualKey.Control, left.Trigger.VirtualKey);
        Assert.AreEqual(KeySide.Left, left.Trigger.Side);
        Assert.IsFalse(left.Trigger.IsExtended);
        Assert.AreEqual(0x1D, left.Trigger.ScanCode);
        Assert.AreEqual(KeyModifiers.None, left.RequiredModifiers);

        var validResult = HotkeyBindingValidator.Validate(left, escape);
        var conflictResult = HotkeyBindingValidator.Validate(left, left);

        Assert.IsInstanceOfType<OperationSuccess<HotkeyGesture>>(validResult);
        Assert.IsInstanceOfType<OperationFailure<HotkeyGesture>>(conflictResult);
    }

    [TestMethod]
    public void UtAudio001UsesThreeHundredMillisecondMinimumWithoutMaximum()
    {
        Assert.IsFalse(RecordingDurationPolicy.IsUsable(TimeSpan.FromMilliseconds(299)));
        Assert.IsTrue(RecordingDurationPolicy.IsUsable(TimeSpan.FromMilliseconds(300)));
        Assert.IsTrue(RecordingDurationPolicy.IsUsable(TimeSpan.FromHours(24)));
    }

    [TestMethod]
    public void UtStt002RejectsWhitespaceAndPreservesUnicode()
    {
        var empty = TranscriptNormalizer.Normalize("   ", trimWhitespace: true);
        var mixed = TranscriptNormalizer.Normalize("  Xin chào OpenAI  ", trimWhitespace: true);

        Assert.IsInstanceOfType<OperationFailure<string>>(empty);
        Assert.AreEqual("Xin chào OpenAI", ((OperationSuccess<string>)mixed).Value);
    }
}

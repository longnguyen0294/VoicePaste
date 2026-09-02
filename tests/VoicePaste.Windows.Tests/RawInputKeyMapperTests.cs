using Microsoft.VisualStudio.TestTools.UnitTesting;
using VoicePaste.Core;
using VoicePaste.Windows.Input;

namespace VoicePaste.Windows.Tests;

[TestClass]
public sealed class RawInputKeyMapperTests
{
    [TestMethod]
    public void UtHotkey003DistinguishesRightControlFromLeftControl()
    {
        var key = AppSettings.Default.Hotkey.Trigger;
        var right = new RawKeyPacket(
            (ushort)VirtualKey.Control,
            0x1D,
            RawInputKeyMapper.ExtendedE0Flag);
        var left = new RawKeyPacket(
            (ushort)VirtualKey.Control,
            0x1D,
            0);

        Assert.IsTrue(RawInputKeyMapper.Matches(right, key));
        Assert.IsFalse(RawInputKeyMapper.Matches(left, key));
    }

    [TestMethod]
    public void UtHotkey001DebouncesRepeatAndReleaseEdges()
    {
        var key = AppSettings.Default.Hotkey.Trigger;
        var press = new RawKeyPacket(
            (ushort)VirtualKey.Control,
            0x1D,
            RawInputKeyMapper.ExtendedE0Flag);
        var release = press with
        {
            Flags = (ushort)(press.Flags | RawInputKeyMapper.BreakFlag),
        };

        Assert.AreEqual(HotkeyEdge.Pressed, RawInputKeyMapper.MapEdge(press, key, isHeld: false));
        Assert.AreEqual(HotkeyEdge.None, RawInputKeyMapper.MapEdge(press, key, isHeld: true));
        Assert.AreEqual(HotkeyEdge.Released, RawInputKeyMapper.MapEdge(release, key, isHeld: true));
        Assert.AreEqual(HotkeyEdge.None, RawInputKeyMapper.MapEdge(release, key, isHeld: false));
    }

    [TestMethod]
    public void UtHotkey004MatchesConfiguredLeftControl()
    {
        var key = HotkeyGesture.LeftControl.Trigger;
        var right = new RawKeyPacket(
            (ushort)VirtualKey.Control,
            0x1D,
            RawInputKeyMapper.ExtendedE0Flag);
        var left = new RawKeyPacket(
            (ushort)VirtualKey.Control,
            0x1D,
            0);

        Assert.IsTrue(RawInputKeyMapper.Matches(left, key));
        Assert.IsFalse(RawInputKeyMapper.Matches(right, key));
    }

    [TestMethod]
    public void UtHotkey005DebouncesRepeatAndReleaseEdgesForLeftControl()
    {
        var key = HotkeyGesture.LeftControl.Trigger;
        var press = new RawKeyPacket(
            (ushort)VirtualKey.Control,
            0x1D,
            0);
        var release = press with
        {
            Flags = (ushort)(press.Flags | RawInputKeyMapper.BreakFlag),
        };

        Assert.AreEqual(HotkeyEdge.Pressed, RawInputKeyMapper.MapEdge(press, key, isHeld: false));
        Assert.AreEqual(HotkeyEdge.None, RawInputKeyMapper.MapEdge(press, key, isHeld: true));
        Assert.AreEqual(HotkeyEdge.Released, RawInputKeyMapper.MapEdge(release, key, isHeld: true));
        Assert.AreEqual(HotkeyEdge.None, RawInputKeyMapper.MapEdge(release, key, isHeld: false));
    }
}


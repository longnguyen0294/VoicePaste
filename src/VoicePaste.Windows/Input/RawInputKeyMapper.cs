using VoicePaste.Core;

namespace VoicePaste.Windows.Input;

public readonly record struct RawKeyPacket(
    ushort VirtualKey,
    ushort ScanCode,
    ushort Flags);

public enum HotkeyEdge
{
    None,
    Pressed,
    Released,
}

public static class RawInputKeyMapper
{
    public const ushort BreakFlag = 0x0001;
    public const ushort ExtendedE0Flag = 0x0002;

    public static bool Matches(RawKeyPacket packet, PhysicalKey key)
    {
        var side = ResolveSide(packet);
        return packet.VirtualKey == (ushort)key.VirtualKey &&
               packet.ScanCode == key.ScanCode &&
               (packet.Flags & ExtendedE0Flag) != 0 == key.IsExtended &&
               side == key.Side;
    }

    public static HotkeyEdge MapEdge(RawKeyPacket packet, PhysicalKey key, bool isHeld)
    {
        if (!Matches(packet, key))
        {
            return HotkeyEdge.None;
        }

        var isRelease = (packet.Flags & BreakFlag) != 0;
        if (isRelease)
        {
            return isHeld ? HotkeyEdge.Released : HotkeyEdge.None;
        }

        return isHeld ? HotkeyEdge.None : HotkeyEdge.Pressed;
    }

    private static KeySide ResolveSide(RawKeyPacket packet)
    {
        if (packet.VirtualKey != (ushort)VirtualKey.Control)
        {
            return KeySide.None;
        }

        return (packet.Flags & ExtendedE0Flag) != 0 ? KeySide.Right : KeySide.Left;
    }
}


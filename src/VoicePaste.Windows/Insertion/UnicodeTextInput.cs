namespace VoicePaste.Windows.Insertion;

public readonly record struct UnicodeInputEvent(ushort ScanCode, uint Flags);

public static class UnicodeTextInput
{
    public const uint KeyUpFlag = 0x0002;
    public const uint UnicodeFlag = 0x0004;

    public static IReadOnlyList<UnicodeInputEvent> CreateEvents(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0)
        {
            return [];
        }

        var events = new UnicodeInputEvent[checked(text.Length * 2)];
        for (var index = 0; index < text.Length; index++)
        {
            var codeUnit = text[index];
            var eventIndex = index * 2;
            events[eventIndex] = new UnicodeInputEvent(codeUnit, UnicodeFlag);
            events[eventIndex + 1] = new UnicodeInputEvent(
                codeUnit,
                UnicodeFlag | KeyUpFlag);
        }

        return events;
    }
}

namespace VoicePaste.Core;

public static class RecordingDurationPolicy
{
    public static readonly TimeSpan MinimumUsableDuration = TimeSpan.FromMilliseconds(300);

    public static bool IsUsable(TimeSpan duration) => duration >= MinimumUsableDuration;
}

public static class TranscriptNormalizer
{
    public static OperationResult<string> Normalize(string? text, bool trimWhitespace)
    {
        var normalized = trimWhitespace ? text?.Trim() : text;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return OperationResult.Failure<string>(new OperationError(
                ErrorCategory.NoSpeech,
                "transcription.no_speech",
                IsRetryable: false));
        }

        return OperationResult.Success(normalized);
    }
}

public static class HotkeyBindingValidator
{
    public static OperationResult<HotkeyGesture> Validate(
        HotkeyGesture gesture,
        HotkeyGesture? conflictingGesture = null)
    {
        if (gesture.Trigger.ScanCode == 0)
        {
            return Invalid("hotkey.empty", "empty_scan_code");
        }

        if (conflictingGesture == gesture)
        {
            return Invalid("hotkey.conflict", "duplicate_binding");
        }

        var keyCode = (ushort)gesture.Trigger.VirtualKey;
        var isCommonLetter = keyCode is >= 0x41 and <= 0x5A;
        if (isCommonLetter && gesture.RequiredModifiers == KeyModifiers.None)
        {
            return Invalid("hotkey.unsafe", "unmodified_letter");
        }

        return OperationResult.Success(gesture);
    }

    private static OperationResult<HotkeyGesture> Invalid(string messageKey, string diagnosticCode) =>
        OperationResult.Failure<HotkeyGesture>(new OperationError(
            ErrorCategory.HotkeyInputUnavailable,
            messageKey,
            IsRetryable: false,
            diagnosticCode));
}

using VoicePaste.Core;

namespace VoicePaste.App.UI;

public static class OperationErrorPresenter
{
    public static string ToUserMessage(OperationError operationError)
    {
        ArgumentNullException.ThrowIfNull(operationError);
        return operationError.UserMessageKey switch
        {
            "provider.api_key_required" =>
                "Open Settings and save an OpenAI API key before dictating.",
            "provider.credential_unavailable" =>
                "The OpenAI API key could not be read from Windows Credential Manager. Open Settings and try again.",
            "clipboard.unsupported_snapshot" =>
                "The clipboard contains a format that cannot be restored safely. Copy plain text or use manual copy.",
            "clipboard.snapshot_too_large" =>
                "The clipboard snapshot is too large to restore safely. Use manual copy.",
            "target.voicepaste_window" =>
                "Switch to an editable field in another application, then hold Right Ctrl to dictate.",
            _ => operationError.Category switch
            {
                ErrorCategory.AuthenticationFailed =>
                    "OpenAI rejected the API key. Update it in Settings.",
                ErrorCategory.NetworkUnavailable =>
                    "The speech provider could not be reached. Check the network and try again.",
                ErrorCategory.QuotaExceeded =>
                    "The OpenAI project is rate-limited or out of quota. Check API usage and try again.",
                ErrorCategory.TimedOut =>
                    "Speech transcription timed out. Try again.",
                ErrorCategory.ProviderFailure =>
                    "The speech provider could not complete transcription. Try again.",
                ErrorCategory.NoSpeech => "No usable speech was captured.",
                ErrorCategory.StorageFull => "Temporary storage is below the 256 MiB safety reserve.",
                ErrorCategory.TargetUnavailable =>
                    "The original target is no longer foreground. Return to it and try dictating again.",
                ErrorCategory.TargetPrivilegeMismatch =>
                    "The target runs at a higher privilege level. Run both applications at the same privilege level.",
                ErrorCategory.ClipboardBusy =>
                    "The clipboard could not be changed safely. Try again or use manual copy.",
                _ => "VoicePaste could not complete this dictation. Try again.",
            },
        };
    }
}

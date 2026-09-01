using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using VoicePaste.Core;
using VoicePaste.Windows.Input;

namespace VoicePaste.Windows.Insertion;

public sealed partial class WindowsTextInsertionService : ITextInsertionService, IDisposable
{
    private static readonly TimeSpan PasteSettleInterval = TimeSpan.FromMilliseconds(400);
    private const int MaximumUnicodeInputEventsPerCall = 512;
    private readonly ITargetWindowValidator _targetValidator;
    private readonly WindowsClipboardService _clipboardService;
    private readonly ITranscriptFallbackStore _fallbackStore;
    private readonly bool _restoreClipboard;

    public WindowsTextInsertionService(
        ITargetWindowValidator targetValidator,
        WindowsClipboardService clipboardService,
        ITranscriptFallbackStore fallbackStore,
        bool restoreClipboard = true)
    {
        _targetValidator = targetValidator;
        _clipboardService = clipboardService;
        _fallbackStore = fallbackStore;
        _restoreClipboard = restoreClipboard;
    }

    public async Task<OperationResult<InsertionOutcome>> InsertAsync(
        string text,
        TargetWindow target,
        CancellationToken cancellationToken)
    {
        var targetResult = _targetValidator.ValidateForPaste(target);
        if (targetResult is OperationFailure<Unit> targetFailure)
        {
            return RetainAndFail(text, targetFailure.Error);
        }

        var clipboardResult = await _clipboardService
            .PlaceTranscriptAsync(text, cancellationToken)
            .ConfigureAwait(false);
        if (clipboardResult is OperationFailure<ClipboardTransaction> clipboardFailure)
        {
            if (clipboardFailure.Error.Category == ErrorCategory.ClipboardBusy)
            {
                var directInsertion = await TryDirectUnicodeInsertionAsync(
                        text,
                        target,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (directInsertion is OperationSuccess<Unit>)
                {
                    return OperationResult.Success(new InsertionOutcome(
                        WasAutomaticallyPasted: true,
                        IsAvailableForManualCopy: false));
                }

                return RetainAndFail(
                    text,
                    ((OperationFailure<Unit>)directInsertion).Error);
            }

            return RetainAndFail(text, clipboardFailure.Error);
        }

        var transaction = ((OperationSuccess<ClipboardTransaction>)clipboardResult).Value;
        try
        {
            targetResult = _targetValidator.ValidateForPaste(target);
            if (targetResult is OperationFailure<Unit> changedTargetFailure)
            {
                return RetainAndFail(text, changedTargetFailure.Error);
            }

            if (!SendPasteShortcut())
            {
                var error = new OperationError(
                    ErrorCategory.TargetUnavailable,
                    "paste.send_input_failed",
                    IsRetryable: true,
                    new Win32Exception(Marshal.GetLastWin32Error()).NativeErrorCode.ToString(
                        CultureInfo.InvariantCulture));
                return RetainAndFail(text, error);
            }

            await Task.Delay(PasteSettleInterval, cancellationToken).ConfigureAwait(false);
            return OperationResult.Success(new InsertionOutcome(
                WasAutomaticallyPasted: true,
                IsAvailableForManualCopy: false));
        }
        finally
        {
            if (_restoreClipboard)
            {
                await _clipboardService
                    .RestoreIfOwnedAsync(transaction, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }

    public void Dispose() => _clipboardService.Dispose();

    private OperationResult<InsertionOutcome> RetainAndFail(string text, OperationError operationError)
    {
        _fallbackStore.Retain(text, operationError);
        return OperationResult.Failure<InsertionOutcome>(operationError);
    }

    private async Task<OperationResult<Unit>> TryDirectUnicodeInsertionAsync(
        string text,
        TargetWindow target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(() =>
        {
            var targetResult = _targetValidator.ValidateForPaste(target);
            if (targetResult is OperationFailure<Unit> targetFailure)
            {
                return (OperationResult<Unit>)targetFailure;
            }

            return SendUnicodeText(text)
                ? OperationResult.Success(Unit.Value)
                : OperationResult.Failure<Unit>(new OperationError(
                    ErrorCategory.TargetUnavailable,
                    "paste.unicode_send_input_failed",
                    IsRetryable: true,
                    new Win32Exception(Marshal.GetLastWin32Error()).NativeErrorCode.ToString(
                        CultureInfo.InvariantCulture)));
        }, cancellationToken).ConfigureAwait(false);
    }

    private static bool SendPasteShortcut()
    {
        const ushort controlKey = 0x11;
        const ushort vKey = 0x56;
        const uint keyboardInput = 1;
        const uint keyUp = 0x0002;
        var inputs = new[]
        {
            KeyboardInput.Create(keyboardInput, controlKey, 0),
            KeyboardInput.Create(keyboardInput, vKey, 0),
            KeyboardInput.Create(keyboardInput, vKey, keyUp),
            KeyboardInput.Create(keyboardInput, controlKey, keyUp),
        };
        var inserted = NativeMethods.SendInput(
            (uint)inputs.Length,
            inputs,
            Marshal.SizeOf<KeyboardInput>());
        if (inserted == (uint)inputs.Length)
        {
            return true;
        }

        if (inserted > 0)
        {
            var releases = new[]
            {
                KeyboardInput.Create(keyboardInput, vKey, keyUp),
                KeyboardInput.Create(keyboardInput, controlKey, keyUp),
            };
            if (NativeMethods.SendInput(
                    (uint)releases.Length,
                    releases,
                    Marshal.SizeOf<KeyboardInput>()) != (uint)releases.Length)
            {
                return false;
            }
        }

        return false;
    }

    private static bool SendUnicodeText(string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        var unicodeEvents = UnicodeTextInput.CreateEvents(text);
        for (var offset = 0; offset < unicodeEvents.Count; offset += MaximumUnicodeInputEventsPerCall)
        {
            var eventCount = Math.Min(
                MaximumUnicodeInputEventsPerCall,
                unicodeEvents.Count - offset);
            var inputs = new KeyboardInput[eventCount];
            for (var index = 0; index < eventCount; index++)
            {
                var unicodeEvent = unicodeEvents[offset + index];
                inputs[index] = KeyboardInput.CreateUnicode(
                    unicodeEvent.ScanCode,
                    unicodeEvent.Flags);
            }

            var inserted = NativeMethods.SendInput(
                (uint)inputs.Length,
                inputs,
                Marshal.SizeOf<KeyboardInput>());
            if (inserted == (uint)inputs.Length)
            {
                continue;
            }

            if (inserted > 0 && inserted < (uint)inputs.Length && (inserted & 1) != 0)
            {
                var unmatchedKeyDown = inputs[(int)inserted - 1].Data.Keyboard;
                var release = new[]
                {
                    KeyboardInput.CreateUnicode(
                        unmatchedKeyDown.ScanCode,
                        UnicodeTextInput.UnicodeFlag | UnicodeTextInput.KeyUpFlag),
                };
                if (NativeMethods.SendInput(
                        (uint)release.Length,
                        release,
                        Marshal.SizeOf<KeyboardInput>()) != (uint)release.Length)
                {
                    return false;
                }
            }

            return false;
        }

        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public uint Type;
        public InputUnion Data;

        public static KeyboardInput Create(uint type, ushort virtualKey, uint flags) => new()
        {
            Type = type,
            Data = new InputUnion
            {
                Keyboard = new KeyboardData
                {
                    VirtualKey = virtualKey,
                    Flags = flags,
                },
            },
        };

        public static KeyboardInput CreateUnicode(ushort scanCode, uint flags) => new()
        {
            Type = 1,
            Data = new InputUnion
            {
                Keyboard = new KeyboardData
                {
                    ScanCode = scanCode,
                    Flags = flags,
                },
            },
        };
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KeyboardData Keyboard;

        [FieldOffset(0)]
        public MouseData Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardData
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseData
    {
        public int X;
        public int Y;
        public uint MouseDataValue;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(
            uint inputCount,
            [In] KeyboardInput[] inputs,
            int inputSize);
    }
}

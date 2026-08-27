using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using VoicePaste.Core;
using Forms = System.Windows.Forms;

namespace VoicePaste.Windows.Input;

public sealed partial class RawInputHotkeyService : IGlobalHotkeyService
{
    private const int WindowsMessageInput = 0x00FF;
    private const int WindowsMessageInputDeviceChange = 0x00FE;
    private const uint RawInputTypeKeyboard = 1;
    private const uint RawInputCommandInput = 0x10000003;
    private const uint RawInputDeviceInputSink = 0x00000100;
    private const uint RawInputDeviceDeviceNotify = 0x00002000;
    private const uint RawInputDeviceRemove = 0x00000001;
    private readonly RawInputWindow _window;
    private HotkeyGesture? _gesture;
    private bool _isHeld;
    private bool _registered;
    private bool _disposed;

    public RawInputHotkeyService()
    {
        _window = new RawInputWindow(ProcessWindowMessage);
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    public event EventHandler? Pressed;

    public event EventHandler? Released;

    public event EventHandler? Interrupted;

    public void Start(HotkeyGesture gesture)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (HotkeyBindingValidator.Validate(gesture) is OperationFailure<HotkeyGesture> failure)
        {
            throw new ArgumentException(failure.Error.UserMessageKey, nameof(gesture));
        }

        if (_registered)
        {
            Unregister();
        }

        var devices = new[]
        {
            new RawInputDevice
            {
                UsagePage = 0x01,
                Usage = 0x06,
                Flags = RawInputDeviceInputSink | RawInputDeviceDeviceNotify,
                Target = _window.Handle,
            },
        };

        if (!NativeMethods.RegisterRawInputDevices(
                devices,
                (uint)devices.Length,
                (uint)Marshal.SizeOf<RawInputDevice>()))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to register Raw Input keyboard.");
        }

        _gesture = gesture;
        _registered = true;
    }

    public void Unregister()
    {
        if (!_registered)
        {
            return;
        }

        var devices = new[]
        {
            new RawInputDevice
            {
                UsagePage = 0x01,
                Usage = 0x06,
                Flags = RawInputDeviceRemove,
                Target = nint.Zero,
            },
        };
        NativeMethods.RegisterRawInputDevices(
            devices,
            (uint)devices.Length,
            (uint)Marshal.SizeOf<RawInputDevice>());

        InterruptIfHeld();
        _gesture = null;
        _registered = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Unregister();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _window.Dispose();
        _disposed = true;
    }

    private void ProcessWindowMessage(int message, nint lParam)
    {
        if (message == WindowsMessageInputDeviceChange)
        {
            InterruptIfHeld();
            return;
        }

        if (message != WindowsMessageInput || _gesture is null)
        {
            return;
        }

        var packet = ReadKeyboardPacket(lParam);
        if (packet is null)
        {
            return;
        }

        var edge = RawInputKeyMapper.MapEdge(packet.Value, _gesture.Trigger, _isHeld);
        switch (edge)
        {
            case HotkeyEdge.Pressed:
                if (!AreRequiredModifiersDown(_gesture.RequiredModifiers))
                {
                    break;
                }

                _isHeld = true;
                Pressed?.Invoke(this, EventArgs.Empty);
                break;
            case HotkeyEdge.Released:
                _isHeld = false;
                Released?.Invoke(this, EventArgs.Empty);
                break;
            case HotkeyEdge.None:
            default:
                break;
        }
    }

    private static RawKeyPacket? ReadKeyboardPacket(nint rawInputHandle)
    {
        uint size = 0;
        var headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        if (NativeMethods.GetRawInputData(
                rawInputHandle,
                RawInputCommandInput,
                nint.Zero,
                ref size,
                headerSize) == uint.MaxValue || size == 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (NativeMethods.GetRawInputData(
                    rawInputHandle,
                    RawInputCommandInput,
                    buffer,
                    ref size,
                    headerSize) == uint.MaxValue)
            {
                return null;
            }

            var input = Marshal.PtrToStructure<RawInput>(buffer);
            if (input.Header.Type != RawInputTypeKeyboard)
            {
                return null;
            }

            return new RawKeyPacket(
                input.Keyboard.VirtualKey,
                input.Keyboard.MakeCode,
                input.Keyboard.Flags);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs args)
    {
        if (args.Mode == PowerModes.Suspend)
        {
            InterruptIfHeld();
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs args)
    {
        if (args.Reason is SessionSwitchReason.SessionLock or
            SessionSwitchReason.ConsoleDisconnect or
            SessionSwitchReason.RemoteDisconnect)
        {
            InterruptIfHeld();
        }
    }

    private void InterruptIfHeld()
    {
        if (!_isHeld)
        {
            return;
        }

        _isHeld = false;
        Interrupted?.Invoke(this, EventArgs.Empty);
    }

    private static bool AreRequiredModifiersDown(KeyModifiers modifiers)
    {
        const int virtualKeyControl = 0x11;
        const int virtualKeyMenu = 0x12;
        const int virtualKeyShift = 0x10;
        const int virtualKeyLeftWindows = 0x5B;
        const int virtualKeyRightWindows = 0x5C;
        const short keyDownMask = unchecked((short)0x8000);

        bool IsDown(int virtualKey) => (NativeMethods.GetAsyncKeyState(virtualKey) & keyDownMask) != 0;

        return (!modifiers.HasFlag(KeyModifiers.Control) || IsDown(virtualKeyControl)) &&
               (!modifiers.HasFlag(KeyModifiers.Alt) || IsDown(virtualKeyMenu)) &&
               (!modifiers.HasFlag(KeyModifiers.Shift) || IsDown(virtualKeyShift)) &&
               (!modifiers.HasFlag(KeyModifiers.Windows) ||
                IsDown(virtualKeyLeftWindows) ||
                IsDown(virtualKeyRightWindows));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public nint Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public nint Device;
        public nint WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawKeyboard
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VirtualKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInput
    {
        public RawInputHeader Header;
        public RawKeyboard Keyboard;
    }

    private sealed class RawInputWindow : Forms.NativeWindow, IDisposable
    {
        private readonly Action<int, nint> _messageHandler;

        public RawInputWindow(Action<int, nint> messageHandler)
        {
            _messageHandler = messageHandler;
            CreateHandle(new Forms.CreateParams
            {
                Caption = "VoicePaste.RawInput",
                Parent = new nint(-3),
            });
        }

        public void Dispose() => DestroyHandle();

        protected override void WndProc(ref Forms.Message message)
        {
            _messageHandler(message.Msg, message.LParam);
            base.WndProc(ref message);
        }
    }

    private static partial class NativeMethods
    {
        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool RegisterRawInputDevices(
            [In] RawInputDevice[] devices,
            uint deviceCount,
            uint deviceSize);

        [LibraryImport("user32.dll", SetLastError = true)]
        public static partial uint GetRawInputData(
            nint rawInput,
            uint command,
            nint data,
            ref uint size,
            uint headerSize);

        [LibraryImport("user32.dll")]
        public static partial short GetAsyncKeyState(int virtualKey);
    }
}

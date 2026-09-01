using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using VoicePaste.Core;

namespace VoicePaste.App.UI;

public sealed partial class StatusOverlayWindow : Window
{
    private const int ExtendedStyleIndex = -20;
    private const nint ExtendedStyleNoActivate = 0x08000000;
    private const nint ExtendedStyleToolWindow = 0x00000080;
    private const nint ExtendedStyleTransparent = 0x00000020;

    public StatusOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyNonActivatingStyles();
    }

    public void UpdateState(DictationSessionState state, OperationError? operationError)
    {
        if (state is DictationSessionState.Idle or
            DictationSessionState.Success or
            DictationSessionState.Cancelled or
            DictationSessionState.Error)
        {
            Hide();
            return;
        }

        StateText.Text = state switch
        {
            DictationSessionState.Listening => "Listening — release Right Ctrl to stop",
            DictationSessionState.Transcribing => "Transcribing",
            DictationSessionState.Pasting => "Pasting",
            _ => state.ToString(),
        };
        StateIndicator.Fill = state switch
        {
            DictationSessionState.Listening => System.Windows.Media.Brushes.LimeGreen,
            DictationSessionState.Transcribing => System.Windows.Media.Brushes.DodgerBlue,
            DictationSessionState.Pasting => System.Windows.Media.Brushes.Gold,
            _ => System.Windows.Media.Brushes.Gray,
        };
        PositionNearWorkArea();
        if (!IsVisible)
        {
            Show();
        }
    }

    private void PositionNearWorkArea()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 24;
        Top = workArea.Bottom - Height - 24;
    }

    private void ApplyNonActivatingStyles()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongPtr(handle, ExtendedStyleIndex);
        NativeMethods.SetWindowLongPtr(
            handle,
            ExtendedStyleIndex,
            style | ExtendedStyleNoActivate | ExtendedStyleToolWindow | ExtendedStyleTransparent);
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        public static extern nint GetWindowLongPtr(nint window, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        public static extern nint SetWindowLongPtr(nint window, int index, nint value);
    }
}

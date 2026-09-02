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

    public void UpdateInterimTranscript(string interimText)
    {
        if (string.IsNullOrWhiteSpace(interimText))
        {
            return;
        }

        StateText.Text = interimText;
        StateIndicator.Fill = System.Windows.Media.Brushes.LimeGreen;  // L5: stay green while live preview
        if (!IsVisible)
        {
            Show();
        }

        // H1: Defer position to Render priority so WPF layout has completed and Width/Height are accurate.
        Dispatcher.InvokeAsync(PositionNearWorkArea, System.Windows.Threading.DispatcherPriority.Render);
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
            DictationSessionState.Listening => "Listening...",
            DictationSessionState.Transcribing => "Transcribing...",
            DictationSessionState.Pasting => "Pasting...",
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
        Dispatcher.InvokeAsync(PositionNearWorkArea, System.Windows.Threading.DispatcherPriority.Render);
    }

    private void PositionNearWorkArea()
    {
        UpdateLayout();
        var workArea = SystemParameters.WorkArea;
        var width = ActualWidth > 0 ? ActualWidth : (double.IsNaN(Width) ? 280 : Width);
        var height = ActualHeight > 0 ? ActualHeight : (double.IsNaN(Height) ? 48 : Height);
        Left = workArea.Right - width - 24;
        Top = workArea.Bottom - height - 24;
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

    private static partial class NativeMethods
    {
        [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        public static partial nint GetWindowLongPtr(nint window, int index);

        [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        public static partial nint SetWindowLongPtr(nint window, int index, nint value);
    }
}

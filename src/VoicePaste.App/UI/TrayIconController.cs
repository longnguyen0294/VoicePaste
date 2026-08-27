using System.Drawing;
using VoicePaste.Core;
using Forms = System.Windows.Forms;

namespace VoicePaste.App.UI;

internal sealed class TrayIconController : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _pauseItem;

    public TrayIconController(Action openSettings, Action togglePause, Action exit)
    {
        var menu = new Forms.ContextMenuStrip();
        var settingsItem = new Forms.ToolStripMenuItem("Open Settings");
        settingsItem.Click += (_, _) => openSettings();
        _pauseItem = new Forms.ToolStripMenuItem("Pause");
        _pauseItem.Click += (_, _) => togglePause();
        var exitItem = new Forms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => exit();
        menu.Items.Add(settingsItem);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = SystemIcons.Information,
            Text = "VoicePaste — Idle",
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => openSettings();
    }

    public void UpdateState(DictationSessionState state, OperationError? operationError)
    {
        _notifyIcon.Text = $"VoicePaste — {state}";
        if (state == DictationSessionState.Error && operationError is not null)
        {
            _notifyIcon.ShowBalloonTip(
                3000,
                "VoicePaste",
                OperationErrorPresenter.ToUserMessage(operationError),
                Forms.ToolTipIcon.Warning);
        }
    }

    public void SetPaused(bool paused)
    {
        _pauseItem.Text = paused ? "Resume" : "Pause";
        _notifyIcon.Text = paused ? "VoicePaste — Paused" : "VoicePaste — Idle";
    }

    public void ShowRecoveryNotice(string message) =>
        _notifyIcon.ShowBalloonTip(4000, "VoicePaste", message, Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }

}

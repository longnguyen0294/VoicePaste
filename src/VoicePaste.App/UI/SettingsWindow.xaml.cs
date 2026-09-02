using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using VoicePaste.App.Composition;
using VoicePaste.Core;
using WpfClipboard = System.Windows.Clipboard;

namespace VoicePaste.App.UI;

public partial class SettingsWindow : Window
{
    private readonly AppStatusController _statusController;
    private readonly IMicrophoneCatalog _microphoneCatalog;
    private readonly ICredentialStore _credentialStore;
    private readonly ISettingsStore _settingsStore;
    private readonly Func<HotkeyGesture, Task>? _hotkeyChangedCallback;
    private readonly string _credentialReference;
    private AppSettings _appSettings;
    private IReadOnlyList<StoredCredentialDescriptor> _storedCredentials = [];
    private bool _credentialManagementBusy;
    private bool _allowClose;
    private bool _isInitializing;

    internal SettingsWindow(
        AppStatusController statusController,
        IMicrophoneCatalog microphoneCatalog,
        ICredentialStore credentialStore,
        ISettingsStore settingsStore,
        AppSettings appSettings,
        Func<HotkeyGesture, Task>? hotkeyChangedCallback,
        string credentialReference)
    {
        _statusController = statusController;
        _microphoneCatalog = microphoneCatalog;
        _credentialStore = credentialStore;
        _settingsStore = settingsStore;
        _appSettings = appSettings;
        _hotkeyChangedCallback = hotkeyChangedCallback;
        _credentialReference = credentialReference;
        InitializeComponent();
        InitializeHotkeySelection();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    internal void UpdateSettings(AppSettings settings)
    {
        _appSettings = settings;
        InitializeHotkeySelection();
    }

    private void InitializeHotkeySelection()
    {
        _isInitializing = true;
        try
        {
            if (_appSettings.Hotkey.Trigger.Side == KeySide.Left)
            {
                LeftCtrlRadioButton.IsChecked = true;
            }
            else
            {
                RightCtrlRadioButton.IsChecked = true;
            }
        }
        finally
        {
            _isInitializing = false;
        }
    }

    private async void RecordHotkey_Checked(object sender, RoutedEventArgs args)
    {
        if (_isInitializing)
        {
            return;
        }

        var targetGesture = LeftCtrlRadioButton.IsChecked == true
            ? HotkeyGesture.LeftControl
            : HotkeyGesture.RightControl;

        if (_appSettings.Hotkey == targetGesture)
        {
            return;
        }

        var validation = HotkeyBindingValidator.Validate(targetGesture, _appSettings.CancelHotkey);
        if (validation is OperationFailure<HotkeyGesture> failure)
        {
            HotkeyStatusText.Text = $"Selected record button is invalid: {failure.Error.UserMessageKey}";
            return;
        }

        try
        {
            var updatedSettings = _appSettings with { Hotkey = targetGesture };
            await _settingsStore.SaveAsync(updatedSettings, CancellationToken.None).ConfigureAwait(true);
            _appSettings = updatedSettings;

            if (_hotkeyChangedCallback is not null)
            {
                await _hotkeyChangedCallback(targetGesture).ConfigureAwait(true);
            }

            var keyName = targetGesture.Trigger.Side == KeySide.Left ? "Left Control" : "Right Control";
            HotkeyStatusText.Text = $"Record button set to {keyName}.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            HotkeyStatusText.Text = "The selected record button could not be saved.";
        }
    }

    internal void UpdateState(DictationSessionState state, OperationError? operationError)
    {
        StateText.Text = state.ToString();
        if (operationError is not null)
        {
            ErrorText.Text = OperationErrorPresenter.ToUserMessage(operationError);
        }
        else if (state is not DictationSessionState.Idle)
        {
            ErrorText.Text = string.Empty;
        }
    }

    internal void SetRetainedTranscript(string transcript, OperationError operationError)
    {
        FallbackTranscriptText.Text = transcript;
        FallbackPanel.Visibility = Visibility.Visible;
        ErrorText.Text = $"{OperationErrorPresenter.ToUserMessage(operationError)} " +
                         "The transcript is available below for manual copy.";
    }

    internal void ClearRetainedTranscript()
    {
        FallbackTranscriptText.Clear();
        FallbackPanel.Visibility = Visibility.Collapsed;
        ErrorText.Text = string.Empty;
    }

    internal void AllowClose() => _allowClose = true;

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (MicrophoneList.Items.Count == 0)
        {
            await LoadMicrophonesAsync();
        }

        await RefreshCredentialStatusAsync();
        await RefreshCredentialListAsync();
    }

    private async Task LoadMicrophonesAsync()
    {
        try
        {
            var devices = await _microphoneCatalog.GetAvailableAsync(CancellationToken.None);
            foreach (var device in devices)
            {
                MicrophoneList.Items.Add(device.IsDefault
                    ? $"{device.DisplayName} (default)"
                    : device.DisplayName);
            }

            if (devices.Count == 0)
            {
                MicrophoneErrorText.Text = "No active capture device was found.";
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException or COMException)
        {
            MicrophoneErrorText.Text = "Microphones could not be enumerated.";
        }
    }

    private async Task RefreshCredentialStatusAsync()
    {
        try
        {
            var credential = await _credentialStore.ReadAsync(
                _credentialReference,
                CancellationToken.None);
            ApiKeyStatusText.Text = string.IsNullOrWhiteSpace(credential)
                ? "No API key is configured. Dictation cannot be transcribed yet."
                : "API key is stored in Windows Credential Manager for the current user.";
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            ApiKeyStatusText.Text = "The API key status could not be read from Windows Credential Manager.";
        }
    }

    private async void SaveApiKey_Click(object sender, RoutedEventArgs args)
    {
        var apiKey = ApiKeyBox.Password;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            ApiKeyStatusText.Text = "Enter an API key before saving.";
            return;
        }

        try
        {
            await _credentialStore.SaveAsync(
                _credentialReference,
                apiKey,
                CancellationToken.None);
            ApiKeyBox.Clear();
            ApiKeyStatusText.Text = "API key saved securely. Switch to an editable field in another application, " +
                                    "then hold Right Ctrl to dictate.";
            await RefreshCredentialListAsync();
        }
        catch (Exception exception) when (exception is ArgumentException or Win32Exception)
        {
            ApiKeyStatusText.Text = "The API key could not be saved to Windows Credential Manager.";
        }
    }

    private async void ClearApiKey_Click(object sender, RoutedEventArgs args)
    {
        if (System.Windows.MessageBox.Show(
                this,
                "Delete the active OpenAI API key? This cannot be undone.",
                "VoicePaste",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _credentialStore.DeleteAsync(
                _credentialReference,
                CancellationToken.None);
            ApiKeyBox.Clear();
            ApiKeyStatusText.Text = "API key removed. Dictation transcription is disabled until a new key is saved.";
            await RefreshCredentialListAsync();
        }
        catch (Win32Exception)
        {
            ApiKeyStatusText.Text = "The API key could not be removed from Windows Credential Manager.";
        }
    }

    private async void RefreshCredentials_Click(object sender, RoutedEventArgs args) =>
        await RefreshCredentialListAsync();

    private void CredentialSearchBox_TextChanged(object sender, RoutedEventArgs args) =>
        ApplyCredentialFilter();

    private void SavedCredentialList_SelectionChanged(object sender, RoutedEventArgs args) =>
        DeleteSelectedCredentialButton.IsEnabled =
            !_credentialManagementBusy && SavedCredentialList.SelectedItem is SavedCredentialListItem;

    private async void DeleteSelectedCredential_Click(object sender, RoutedEventArgs args)
    {
        if (SavedCredentialList.SelectedItem is not SavedCredentialListItem selected)
        {
            CredentialManagerStatusText.Text = "Select a saved API key to delete.";
            return;
        }

        if (System.Windows.MessageBox.Show(
                this,
                $"Delete the saved API key '{selected.Reference}'? This cannot be undone.",
                "VoicePaste",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        SetCredentialManagementBusy(isBusy: true);
        try
        {
            await _credentialStore.DeleteAsync(selected.Reference, CancellationToken.None);
            await RefreshCredentialStatusAsync();
            await LoadCredentialListAsync();
            CredentialManagerStatusText.Text = $"Deleted saved API key '{selected.Reference}'.";
        }
        catch (Exception exception) when (exception is ArgumentException or Win32Exception)
        {
            CredentialManagerStatusText.Text = "The selected API key could not be deleted.";
        }
        finally
        {
            SetCredentialManagementBusy(isBusy: false);
        }
    }

    private async Task RefreshCredentialListAsync()
    {
        if (_credentialManagementBusy)
        {
            return;
        }

        SetCredentialManagementBusy(isBusy: true);
        try
        {
            await LoadCredentialListAsync();
            if (string.IsNullOrWhiteSpace(CredentialSearchBox.Text))
            {
                CredentialManagerStatusText.Text = _storedCredentials.Count == 0
                    ? "No VoicePaste API keys are stored for this Windows user."
                    : $"Found {_storedCredentials.Count} saved VoicePaste API key(s).";
            }
        }
        catch (Win32Exception)
        {
            _storedCredentials = [];
            ApplyCredentialFilter();
            CredentialManagerStatusText.Text =
                "Saved API keys could not be read from Windows Credential Manager.";
        }
        finally
        {
            SetCredentialManagementBusy(isBusy: false);
        }
    }

    private async Task LoadCredentialListAsync()
    {
        _storedCredentials = await _credentialStore.ListAsync(CancellationToken.None);
        ApplyCredentialFilter();
    }

    private void ApplyCredentialFilter()
    {
        var items = CredentialListFilter.Apply(
            _storedCredentials,
            CredentialSearchBox.Text,
            _credentialReference);
        SavedCredentialList.ItemsSource = items;
        DeleteSelectedCredentialButton.IsEnabled =
            !_credentialManagementBusy && SavedCredentialList.SelectedItem is SavedCredentialListItem;
        if (!string.IsNullOrWhiteSpace(CredentialSearchBox.Text))
        {
            CredentialManagerStatusText.Text = $"{items.Count} matching saved API key(s).";
        }
    }

    private void SetCredentialManagementBusy(bool isBusy)
    {
        _credentialManagementBusy = isBusy;
        RefreshCredentialsButton.IsEnabled = !isBusy;
        DeleteSelectedCredentialButton.IsEnabled =
            !isBusy && SavedCredentialList.SelectedItem is SavedCredentialListItem;
    }

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        args.Cancel = true;
        Hide();
    }

    private void CopyTranscript_Click(object sender, RoutedEventArgs args)
    {
        var transcript = _statusController.GetRetainedTranscript();
        if (!string.IsNullOrEmpty(transcript))
        {
            try
            {
                WpfClipboard.SetText(transcript);
                ErrorText.Text = "Transcript copied to the clipboard.";
            }
            catch (ExternalException)
            {
                ErrorText.Text = "The clipboard is busy. The transcript is still available; try copying again.";
            }
        }
    }
}

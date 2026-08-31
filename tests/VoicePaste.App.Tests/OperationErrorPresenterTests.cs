using Microsoft.VisualStudio.TestTools.UnitTesting;
using VoicePaste.App.UI;
using VoicePaste.Core;

namespace VoicePaste.App.Tests;

[TestClass]
public sealed class OperationErrorPresenterTests
{
    [TestMethod]
    public void UtStatus001VoicePasteTargetShowsRecoveryActionWithoutInternalCode()
    {
        var error = new OperationError(
            ErrorCategory.TargetUnavailable,
            "target.voicepaste_window",
            IsRetryable: true,
            "foreground_is_voicepaste");

        var message = OperationErrorPresenter.ToUserMessage(error);

        StringAssert.Contains(message, "editable field in another application");
        StringAssert.Contains(message, "Right Ctrl");
        Assert.IsFalse(message.Contains("target.voicepaste_window", StringComparison.Ordinal));
        Assert.IsFalse(message.Contains("foreground_is_voicepaste", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(ErrorCategory.NetworkUnavailable, "Check the network")]
    [DataRow(ErrorCategory.TargetPrivilegeMismatch, "same privilege level")]
    [DataRow(ErrorCategory.ClipboardBusy, "manual copy")]
    public void UtStatus001CategoryFallbackIncludesRecoveryAction(
        ErrorCategory category,
        string expectedRecoveryText)
    {
        var error = new OperationError(category, "unmapped.test_key", IsRetryable: true);

        var message = OperationErrorPresenter.ToUserMessage(error);

        StringAssert.Contains(message, expectedRecoveryText);
        Assert.IsFalse(message.Contains("unmapped.test_key", StringComparison.Ordinal));
    }

    [TestMethod]
    public void UtStatus001DirectUnicodeFailureShowsManualRecovery()
    {
        var message = OperationErrorPresenter.ToUserMessage(new OperationError(
            ErrorCategory.TargetUnavailable,
            "paste.unicode_send_input_failed",
            IsRetryable: true));

        StringAssert.Contains(message, "direct Unicode insertion");
        StringAssert.Contains(message, "manual copy");
    }
}

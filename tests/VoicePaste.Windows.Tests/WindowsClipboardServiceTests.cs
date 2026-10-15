using System.Runtime.Serialization;
using System.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VoicePaste.Core;
using VoicePaste.Windows.Input;
using VoicePaste.Windows.Insertion;

namespace VoicePaste.Windows.Tests;

[TestClass]
public sealed class WindowsClipboardServiceTests
{
    [TestMethod]
    [DataRow("platform")]
    [DataRow("unsupported")]
    [DataRow("serialization")]
    public void UtClip004UnsupportedDeserializationRejectsTheWholeSnapshot(string failureKind)
    {
        var reads = 0;
        var data = new TestDataObject(
            () => ["UnicodeText", "application-specific", "unread"],
            format =>
            {
                reads++;
                return format == "UnicodeText" ? "previous clipboard" : throw ReadFailure(failureKind);
            });

        var failure = WindowsClipboardService.CaptureSnapshot(data) as OperationFailure<ClipboardSnapshot>;

        Assert.IsNotNull(failure);
        Assert.AreEqual(ErrorCategory.ClipboardBusy, failure.Error.Category);
        Assert.AreEqual("clipboard.snapshot_unavailable", failure.Error.UserMessageKey);
        Assert.AreEqual(2, reads, "Do not continue reading or accept a partial snapshot after a failure.");
    }

    [TestMethod]
    [DataRow("platform")]
    [DataRow("invalid")]
    public void UtClip004FormatEnumerationFailureReturnsTypedClipboardError(string failureKind)
    {
        var data = new TestDataObject(() => throw ReadFailure(failureKind), _ =>
            throw new AssertFailedException("No data should be read after format enumeration fails."));

        var failure = WindowsClipboardService.CaptureSnapshot(data) as OperationFailure<ClipboardSnapshot>;

        Assert.IsNotNull(failure);
        Assert.AreEqual("clipboard.snapshot_unavailable", failure.Error.UserMessageKey);
    }

    [TestMethod]
    public void UtClip004SafeAndEmptySnapshotsStillSucceed()
    {
        var empty = WindowsClipboardService.CaptureSnapshot(null) as OperationSuccess<ClipboardSnapshot>;
        var data = new TestDataObject(() => ["UnicodeText"], _ => "Tiếng Việt – English");
        var text = WindowsClipboardService.CaptureSnapshot(data) as OperationSuccess<ClipboardSnapshot>;

        Assert.IsNotNull(empty);
        Assert.AreEqual(0, empty.Value.Values.Count);
        Assert.IsNotNull(text);
        Assert.AreEqual("Tiếng Việt – English", text.Value.Values["UnicodeText"]);
    }

    [TestMethod]
    public async Task UtClip004RepeatedReadFailuresDoNotWriteOrPoisonTheDispatcher()
    {
        var reads = 0;
        var data = new TestDataObject(() => ["application-specific"], _ =>
        {
            reads++;
            throw new PlatformNotSupportedException("clipboard payload must not be logged");
        });
        using var service = new WindowsClipboardService(() => data);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var failure = await service.PlaceTranscriptAsync("new transcript", CancellationToken.None)
                as OperationFailure<ClipboardTransaction>;
            Assert.IsNotNull(failure, "An unreadable snapshot must never proceed to a clipboard write.");
            Assert.AreEqual("clipboard.snapshot_unavailable", failure.Error.UserMessageKey);
        }

        Assert.AreEqual(3, reads);
    }

    [TestMethod]
    public async Task UtInsert003UnreadableClipboardRevalidatesTargetAndRetainsTranscript()
    {
        const string transcript = "Tiếng Việt – English";
        var data = new TestDataObject(() => ["application-specific"], _ =>
            throw new PlatformNotSupportedException());
        var validator = new TargetChangesBeforeFallback();
        var fallback = new RetainedTranscript();
        using var insertion = new WindowsTextInsertionService(
            validator, new WindowsClipboardService(() => data), fallback);
        var target = new TargetWindow(1, 1, 1, "test", null, 0, DateTimeOffset.UtcNow);

        var failure = await insertion.InsertAsync(transcript, target, CancellationToken.None)
            as OperationFailure<InsertionOutcome>;

        Assert.IsNotNull(failure);
        Assert.AreEqual(2, validator.Calls, "The typed clipboard error must enter the Unicode fallback.");
        Assert.AreEqual("target.not_foreground", failure.Error.UserMessageKey);
        Assert.AreEqual(transcript, fallback.Text);
        Assert.AreEqual(failure.Error, fallback.Error);
    }

    private static Exception ReadFailure(string failureKind) => failureKind switch
    {
        "platform" => new PlatformNotSupportedException(),
        "unsupported" => new NotSupportedException(),
        "serialization" => new SerializationException(),
        "invalid" => new InvalidOperationException(),
        _ => throw new ArgumentOutOfRangeException(nameof(failureKind)),
    };

    private sealed class TargetChangesBeforeFallback : ITargetWindowValidator
    {
        public int Calls { get; private set; }

        public OperationResult<Unit> ValidateForPaste(TargetWindow target) => ++Calls == 1
            ? OperationResult.Success(Unit.Value)
            : OperationResult.Failure<Unit>(new OperationError(
                ErrorCategory.TargetUnavailable, "target.not_foreground", IsRetryable: true));
    }

    private sealed class RetainedTranscript : ITranscriptFallbackStore
    {
        public string? Text { get; private set; }
        public OperationError? Error { get; private set; }

        public void Retain(string transcript, OperationError operationError)
        {
            Text = transcript;
            Error = operationError;
        }
    }

    private sealed class TestDataObject(Func<string[]> formats, Func<string, object?> read) : IDataObject
    {
        public object? GetData(string format, bool autoConvert) => read(format);
        public string[] GetFormats(bool autoConvert) => formats();
        public object? GetData(string format) => GetData(format, false);
        public object? GetData(Type format) => throw new NotSupportedException();
        public bool GetDataPresent(string format, bool autoConvert) => throw new NotSupportedException();
        public bool GetDataPresent(string format) => throw new NotSupportedException();
        public bool GetDataPresent(Type format) => throw new NotSupportedException();
        public string[] GetFormats() => GetFormats(false);
        public void SetData(string format, object data, bool autoConvert) => throw new NotSupportedException();
        public void SetData(string format, object data) => throw new NotSupportedException();
        public void SetData(Type format, object data) => throw new NotSupportedException();
        public void SetData(object data) => throw new NotSupportedException();
    }
}

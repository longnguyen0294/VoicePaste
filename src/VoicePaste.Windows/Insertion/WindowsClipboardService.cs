using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Windows;
using VoicePaste.Core;
using WpfClipboard = System.Windows.Clipboard;
using WpfDataFormats = System.Windows.DataFormats;
using WpfDataObject = System.Windows.DataObject;
using WpfIDataObject = System.Windows.IDataObject;

namespace VoicePaste.Windows.Insertion;

public sealed record ClipboardTransaction(
    ClipboardLease Lease,
    ClipboardSnapshot Snapshot);

public sealed partial class WindowsClipboardService : IDisposable
{
    public const string PrivateFormat = "VoicePaste.ClipboardLease.v1";
    private static readonly TimeSpan LeaseLifetime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(40);
    private const int MaximumAttempts = 5;
    private readonly StaDispatcher _dispatcher = new();
    private readonly Func<WpfIDataObject?> _readClipboard;

    public WindowsClipboardService() : this(WpfClipboard.GetDataObject)
    {
    }

    internal WindowsClipboardService(Func<WpfIDataObject?> readClipboard)
    {
        _readClipboard = readClipboard;
    }

    public async Task<OperationResult<ClipboardTransaction>> PlaceTranscriptAsync(
        string transcript,
        CancellationToken cancellationToken)
    {
        try
        {
            SessionDiagnostics.Write("clipboard.snapshot_begin");
            var snapshotResult = await InvokeWithRetryAsync(
                    () => CaptureSnapshot(_readClipboard()),
                    cancellationToken)
                .ConfigureAwait(false);
            if (snapshotResult is OperationFailure<ClipboardSnapshot> failure)
            {
                SessionDiagnostics.Write("clipboard.snapshot_failed", error: failure.Error);
                return OperationResult.Failure<ClipboardTransaction>(failure.Error);
            }

            var snapshot = ((OperationSuccess<ClipboardSnapshot>)snapshotResult).Value;
            var token = Guid.NewGuid();
            SessionDiagnostics.Write("clipboard.write_begin", $"SnapshotBytes={snapshot.SizeBytes}");
            var lease = await InvokeWithRetryAsync(
                    () => WriteTranscript(transcript, token, snapshot.SizeBytes),
                    cancellationToken)
                .ConfigureAwait(false);
            return OperationResult.Success(new ClipboardTransaction(lease, snapshot));
        }
        catch (ClipboardAccessException exception)
        {
            SessionDiagnostics.Write("clipboard.access_exhausted", exception: exception);
            return OperationResult.Failure<ClipboardTransaction>(Busy(exception.DiagnosticCode));
        }
    }

    public async Task<bool> RestoreIfOwnedAsync(
        ClipboardTransaction transaction,
        CancellationToken cancellationToken)
    {
        try
        {
            var restored = await InvokeWithRetryAsync(
                    () => RestoreIfOwned(transaction),
                    cancellationToken)
                .ConfigureAwait(false);
            SessionDiagnostics.Write("clipboard.restore_result", $"Restored={restored}");
            return restored;
        }
        catch (Exception exception) when (
            exception is ClipboardAccessException or
            COMException or
            ExternalException or
            InvalidOperationException or
            ObjectDisposedException)
        {
            SessionDiagnostics.Write("clipboard.restore_failed", exception: exception);
            return false;
        }
    }

    public void Dispose() => _dispatcher.Dispose();

    private async Task<T> InvokeWithRetryAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await _dispatcher.InvokeAsync(action, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is COMException or ExternalException)
            {
                SessionDiagnostics.Write("clipboard.retry", $"Attempt={attempt} MaximumAttempts={MaximumAttempts}", exception: exception);
                lastException = exception;
                if (attempt < MaximumAttempts)
                {
                    await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        throw new ClipboardAccessException(lastException?.GetType().Name ?? "clipboard_locked");
    }

    internal static OperationResult<ClipboardSnapshot> CaptureSnapshot(WpfIDataObject? dataObject)
    {
        if (dataObject is null)
        {
            return OperationResult.Success(new ClipboardSnapshot(
                new Dictionary<string, object>(),
                0));
        }

        try
        {
            var values = new List<KeyValuePair<string, object?>>();
            foreach (var format in dataObject.GetFormats(autoConvert: false))
            {
                values.Add(new KeyValuePair<string, object?>(
                    format,
                    dataObject.GetData(format, autoConvert: false)));
            }

            return ClipboardSnapshotPolicy.Create(values);
        }
        catch (Exception exception) when (
            exception is COMException or ExternalException or InvalidOperationException or
                NotSupportedException or SerializationException)
        {
            // WPF may deserialize application-specific clipboard objects. If that is unsupported
            // or malformed, abandon the entire snapshot before writing anything to the clipboard.
            SessionDiagnostics.Write("clipboard.snapshot_read_failed", exception: exception);
            return OperationResult.Failure<ClipboardSnapshot>(new OperationError(
                ErrorCategory.ClipboardBusy,
                "clipboard.snapshot_unavailable",
                IsRetryable: true,
                exception.GetType().Name));
        }
    }

    private static ClipboardLease WriteTranscript(string transcript, Guid token, long snapshotSize)
    {
        var dataObject = new WpfDataObject();
        dataObject.SetData(WpfDataFormats.UnicodeText, transcript);
        dataObject.SetData(PrivateFormat, token.ToString("D"));
        WpfClipboard.SetDataObject(dataObject, copy: true);
        var createdAt = DateTimeOffset.UtcNow;
        return new ClipboardLease(
            Guid.NewGuid().ToString("N"),
            token,
            PrivateFormat,
            NativeMethods.GetClipboardSequenceNumber(),
            NativeMethods.GetClipboardOwner(),
            snapshotSize,
            createdAt + LeaseLifetime,
            createdAt);
    }

    private static bool RestoreIfOwned(ClipboardTransaction transaction)
    {
        var lease = transaction.Lease;
        if (DateTimeOffset.UtcNow > lease.ExpiresAt ||
            NativeMethods.GetClipboardSequenceNumber() != lease.SequenceNumberAfterWrite ||
            NativeMethods.GetClipboardOwner() != lease.OwnerWindow)
        {
            return false;
        }

        var currentToken = WpfClipboard.GetData(PrivateFormat) as string;
        if (!Guid.TryParse(currentToken, out var token) || token != lease.LeaseToken)
        {
            return false;
        }

        if (transaction.Snapshot.Values.Count == 0)
        {
            WpfClipboard.Clear();
            return true;
        }

        var dataObject = new WpfDataObject();
        foreach (var pair in transaction.Snapshot.Values)
        {
            dataObject.SetData(pair.Key, pair.Value);
        }

        WpfClipboard.SetDataObject(dataObject, copy: true);
        return true;
    }

    private static OperationError Busy(string diagnosticCode) => new(
        ErrorCategory.ClipboardBusy,
        "clipboard.busy",
        IsRetryable: true,
        diagnosticCode);

    private sealed class ClipboardAccessException(string diagnosticCode) : Exception
    {
        public string DiagnosticCode { get; } = diagnosticCode;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("user32.dll")]
        public static partial uint GetClipboardSequenceNumber();

        [LibraryImport("user32.dll")]
        public static partial nint GetClipboardOwner();
    }
}

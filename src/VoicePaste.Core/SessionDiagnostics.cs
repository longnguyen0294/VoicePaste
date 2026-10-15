using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Threading.Channels;

namespace VoicePaste.Core;

/// <summary>Local, bounded diagnostics. Callers must supply metadata, never user content.</summary>
public static class SessionDiagnostics
{
    private static readonly AsyncLocal<Guid?> CurrentSession = new();
    private static readonly Channel<LogItem> Queue = Channel.CreateBounded<LogItem>(
        new BoundedChannelOptions(1024) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private static readonly DiagnosticFileWriter Writer = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VoicePaste", "logs", "application.log"));
    private static readonly Task WriteTask = Task.Run(WriteLoopAsync);
    private static long _droppedRecords;

    public static Guid? CurrentSessionId => CurrentSession.Value;

    public static IDisposable BeginSession(Guid? sessionId)
    {
        var previous = CurrentSession.Value;
        CurrentSession.Value = sessionId;
        return new SessionScope(previous);
    }

    public static void Write(
        string context,
        string metadata = "",
        OperationError? error = null,
        Exception? exception = null)
    {
        try
        {
            var line = FormatLine(context, metadata, error, exception);
            if (!Queue.Writer.TryWrite(new LogItem(line, null)))
            {
                Interlocked.Increment(ref _droppedRecords);
            }
        }
        catch
        {
            // Diagnostics must never interfere with capture, recovery, or UI dispatch.
        }
    }

    public static async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Queue.Writer.WriteAsync(new LogItem(null, flushed), cancellationToken).ConfigureAwait(false);
        await flushed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static string FormatLine(
        string context, string metadata, OperationError? error, Exception? exception) =>
        string.Create(CultureInfo.InvariantCulture,
            $"[{DateTimeOffset.Now:O}] PID={Environment.ProcessId} Session={CurrentSession.Value?.ToString("N") ?? "none"} {context}: {metadata}") +
        (error is null ? "" : $" Category={error.Category} Key={error.UserMessageKey} Retryable={error.IsRetryable}") +
        (exception is null ? "" : " " + FormatExceptionMetadata(exception));

    // Messages, Exception.Data, raw stack strings, paths, and provider payloads can contain secrets.
    public static string FormatExceptionMetadata(Exception exception)
    {
        var entries = new List<string>();
        for (var depth = 0; exception is not null && depth < 4; depth++, exception = exception.InnerException!)
        {
            var frames = new StackTrace(exception, true).GetFrames().Take(12).Select(frame =>
            {
                var method = frame.GetMethod();
                return $"{method?.DeclaringType?.FullName}.{method?.Name}:{frame.GetFileLineNumber()}";
            });
            var native = exception is SocketException socket
                ? $" SocketError={socket.SocketErrorCode}"
                : exception is HttpRequestException http
                    ? $" HttpError={http.HttpRequestError} HttpStatus={(int?)http.StatusCode}"
                    : "";
            entries.Add(string.Create(CultureInfo.InvariantCulture,
                $"Depth={depth} ExceptionType={exception.GetType().FullName} HResult={exception.HResult}{native} Frames=[{string.Join(" > ", frames)}]"));
        }

        return string.Join(" Inner=", entries);
    }

    private static async Task WriteLoopAsync()
    {
        await foreach (var item in Queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (item.Line is not null)
                {
                    var dropped = Interlocked.Exchange(ref _droppedRecords, 0);
                    Writer.Append(item.Line + (dropped == 0 ? "" : $" DroppedRecords={dropped}"));
                }
            }
            catch
            {
                // A locked/unwritable log file must not stop the background writer.
            }
            finally
            {
                item.Flushed?.TrySetResult();
            }
        }
    }

    private sealed record LogItem(string? Line, TaskCompletionSource? Flushed);

    private sealed class SessionScope(Guid? previous) : IDisposable
    {
        public void Dispose() => CurrentSession.Value = previous;
    }
}

internal sealed class DiagnosticFileWriter(string path, long maximumBytes = 1024 * 1024)
{
    public void Append(string line)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path) && new FileInfo(path).Length >= maximumBytes)
        {
            File.Move(path, Path.ChangeExtension(path, ".previous.log"), overwrite: true);
        }

        File.AppendAllText(path, line + Environment.NewLine);
    }
}

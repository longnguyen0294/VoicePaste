using System.Threading.Channels;
using System.IO;
using VoicePaste.Core;

namespace VoicePaste.Windows.Audio;

public interface IFreeSpaceProbe
{
    long GetAvailableBytes(string path);
}

public sealed class DriveFreeSpaceProbe : IFreeSpaceProbe
{
    public long GetAvailableBytes(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root))
        {
            throw new IOException("Unable to resolve temporary-storage volume.");
        }

        return new DriveInfo(root).AvailableFreeSpace;
    }
}

public static class AudioChunkPolicy
{
    public static readonly TimeSpan MaximumChunkDuration = TimeSpan.FromMinutes(5);
    public const long FreeSpaceReserveBytes = 256L * 1024 * 1024;
    public const int BufferCapacity = 32;

    public static long GetMaximumChunkBytes(AudioFormat format) =>
        checked((long)format.BytesPerSecond * (long)MaximumChunkDuration.TotalSeconds);
}

public sealed class ChunkedPcmSink : IAsyncDisposable
{
    private readonly string _directory;
    private readonly AudioFormat _format;
    private readonly IFreeSpaceProbe _freeSpaceProbe;
    private readonly long _reserveBytes;
    private readonly long _maximumChunkBytes;
    private readonly Channel<byte[]> _channel;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly List<string> _chunkPaths = [];
    private readonly object _failureLock = new();
    private readonly Task _writerTask;
    private OperationError? _failure;
    private long _bytesWritten;
    private int _completed;

    public ChunkedPcmSink(
        string directory,
        AudioFormat format,
        IFreeSpaceProbe? freeSpaceProbe = null,
        long reserveBytes = AudioChunkPolicy.FreeSpaceReserveBytes,
        long? maximumChunkBytes = null)
    {
        _directory = Path.GetFullPath(directory);
        _format = format;
        _freeSpaceProbe = freeSpaceProbe ?? new DriveFreeSpaceProbe();
        _reserveBytes = reserveBytes;
        _maximumChunkBytes = maximumChunkBytes ?? AudioChunkPolicy.GetMaximumChunkBytes(format);
        if (_maximumChunkBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumChunkBytes));
        }

        Directory.CreateDirectory(_directory);
        _channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(AudioChunkPolicy.BufferCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _writerTask = RunWriterAsync();
    }

    public event EventHandler? Faulted;

    public Task Completion => _writerTask;

    public OperationError? Failure
    {
        get
        {
            lock (_failureLock)
            {
                return _failure;
            }
        }
    }

    public long BytesWritten => Interlocked.Read(ref _bytesWritten);

    public IReadOnlyList<string> ChunkPaths
    {
        get
        {
            lock (_chunkPaths)
            {
                return _chunkPaths.ToArray();
            }
        }
    }

    public bool TryWrite(ReadOnlySpan<byte> data)
    {
        if (Volatile.Read(ref _completed) != 0 || data.IsEmpty || Failure is not null)
        {
            return false;
        }

        if (_channel.Writer.TryWrite(data.ToArray()))
        {
            return true;
        }

        SetFailure(new OperationError(
            ErrorCategory.AudioEncodingFailed,
            "audio.buffer_overflow",
            IsRetryable: true,
            "bounded_channel_full"));
        _channel.Writer.TryComplete();
        return false;
    }

    public void Complete()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return;
        }

        _channel.Writer.TryComplete();
    }

    public void Abort()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return;
        }

        _cancellation.Cancel();
        _channel.Writer.TryComplete();
    }

    public async ValueTask DisposeAsync()
    {
        Abort();
        try
        {
            await _writerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _cancellation.Dispose();
    }

    private async Task RunWriterAsync()
    {
        FileStream? currentChunk = null;
        long currentChunkBytes = 0;

        try
        {
            await foreach (var buffer in _channel.Reader.ReadAllAsync(_cancellation.Token)
                               .ConfigureAwait(false))
            {
                EnsureFreeSpace();
                var offset = 0;
                while (offset < buffer.Length)
                {
                    if (currentChunk is null || currentChunkBytes >= _maximumChunkBytes)
                    {
                        if (currentChunk is not null)
                        {
                            await currentChunk.DisposeAsync().ConfigureAwait(false);
                        }

                        currentChunk = OpenNextChunk();
                        currentChunkBytes = 0;
                    }

                    var writable = (int)Math.Min(
                        buffer.Length - offset,
                        _maximumChunkBytes - currentChunkBytes);
                    await currentChunk.WriteAsync(
                            buffer.AsMemory(offset, writable),
                            _cancellation.Token)
                        .ConfigureAwait(false);
                    offset += writable;
                    currentChunkBytes += writable;
                    Interlocked.Add(ref _bytesWritten, writable);
                }
            }

            if (currentChunk is not null)
            {
                await currentChunk.FlushAsync(_cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        catch (StorageReserveException exception)
        {
            SetFailure(new OperationError(
                ErrorCategory.StorageFull,
                "audio.storage_full",
                IsRetryable: true,
                exception.Message));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetFailure(new OperationError(
                ErrorCategory.StorageUnavailable,
                "audio.storage_unavailable",
                IsRetryable: true,
                exception.GetType().Name));
        }
        finally
        {
            if (currentChunk is not null)
            {
                await currentChunk.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private FileStream OpenNextChunk()
    {
        var path = Path.Combine(_directory, $"chunk-{_chunkPaths.Count:D5}.pcm");
        var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        lock (_chunkPaths)
        {
            _chunkPaths.Add(path);
        }

        return stream;
    }

    private void EnsureFreeSpace()
    {
        if (_freeSpaceProbe.GetAvailableBytes(_directory) < _reserveBytes)
        {
            throw new StorageReserveException("free_space_below_256_mib_reserve");
        }
    }

    private void SetFailure(OperationError operationError)
    {
        var changed = false;
        lock (_failureLock)
        {
            if (_failure is null)
            {
                _failure = operationError;
                changed = true;
            }
        }

        if (changed)
        {
            Faulted?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class StorageReserveException(string message) : IOException(message);
}

public sealed class ChunkedAudioContent : IAudioContent
{
    private readonly string _directory;
    private readonly IReadOnlyList<string> _chunkPaths;
    private int _disposed;

    public ChunkedAudioContent(
        string directory,
        IReadOnlyList<string> chunkPaths,
        AudioFormat format,
        TimeSpan duration,
        long lengthBytes)
    {
        _directory = Path.GetFullPath(directory);
        _chunkPaths = chunkPaths.Select(Path.GetFullPath).ToArray();
        var directoryPrefix = _directory + Path.DirectorySeparatorChar;
        if (_chunkPaths.Any(path => !path.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Audio chunk path is outside the recording directory.", nameof(chunkPaths));
        }

        Format = format;
        Duration = duration;
        LengthBytes = lengthBytes;
    }

    public AudioFormat Format { get; }

    public TimeSpan Duration { get; }

    public long? LengthBytes { get; }

    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(ChunkedAudioContent));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<Stream>(new ConcatenatedReadStream(_chunkPaths));
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        foreach (var path in _chunkPaths)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Startup cleanup retries any application-owned file that is temporarily locked.
            }
        }

        try
        {
            if (Directory.Exists(_directory) && !Directory.EnumerateFileSystemEntries(_directory).Any())
            {
                Directory.Delete(_directory);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort; stale application-owned directories are retried on startup.
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class ConcatenatedReadStream : Stream
{
    private readonly IReadOnlyList<string> _paths;
    private int _index;
    private FileStream? _current;
    private bool _disposed;

    public ConcatenatedReadStream(IReadOnlyList<string> paths)
    {
        _paths = paths;
    }

    public override bool CanRead => !_disposed;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => _paths.Sum(path => new FileInfo(path).Length);

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ConcatenatedReadStream));
        }
        while (true)
        {
            if (_current is null)
            {
                if (_index >= _paths.Count)
                {
                    return 0;
                }

                _current = new FileStream(
                    _paths[_index++],
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
            }

            var read = await _current.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read > 0)
            {
                return read;
            }

            await _current.DisposeAsync().ConfigureAwait(false);
            _current = null;
        }
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _current?.Dispose();
            _disposed = true;
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed && _current is not null)
        {
            await _current.DisposeAsync().ConfigureAwait(false);
        }

        _disposed = true;
        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}

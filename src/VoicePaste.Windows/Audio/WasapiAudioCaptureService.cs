using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.IO;
using System.Runtime.InteropServices;
using VoicePaste.Core;

namespace VoicePaste.Windows.Audio;

public sealed class WasapiMicrophoneCatalog : IMicrophoneCatalog
{
    public Task<IReadOnlyList<MicrophoneDevice>> GetAvailableAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var enumerator = new MMDeviceEnumerator();
        using var defaultDevice = TryGetDefault(enumerator);
        var defaultId = defaultDevice?.ID;
        var result = new List<MicrophoneDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (device)
            {
                result.Add(new MicrophoneDevice(
                    device.ID,
                    device.FriendlyName,
                    string.Equals(device.ID, defaultId, StringComparison.Ordinal),
                    IsAvailable: true));
            }
        }

        return Task.FromResult<IReadOnlyList<MicrophoneDevice>>(result);
    }

    private static MMDevice? TryGetDefault(MMDeviceEnumerator enumerator)
    {
        try
        {
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            return null;
        }
    }
}

public sealed class WasapiAudioCaptureService : IAudioCaptureService
{
    private static readonly TimeSpan CancellationStopTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan NormalStopTimeout = TimeSpan.FromSeconds(5);
    private readonly string _audioRoot;
    private readonly IFreeSpaceProbe _freeSpaceProbe;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WasapiRecorder? _capture;
    private MMDevice? _device;
    private ChunkedPcmSink? _sink;
    private string? _recordingDirectory;
    private string? _recordingId;
    private AudioFormat? _format;
    private OperationError? _captureFailure;
    private TaskCompletionSource<OperationError?>? _recordingStopped;
    private bool _disposed;

    public event EventHandler<AudioChunkAvailableEventArgs>? DataAvailable;

    public WasapiAudioCaptureService(string? audioRoot = null, IFreeSpaceProbe? freeSpaceProbe = null)
    {
        _audioRoot = audioRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VoicePaste",
            "Audio");
        _freeSpaceProbe = freeSpaceProbe ?? new DriveFreeSpaceProbe();
    }

    public async Task<OperationResult<Unit>> StartAsync(
        string microphoneId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_capture is not null)
            {
                return OperationResult.Failure<Unit>(new OperationError(
                    ErrorCategory.MicrophoneUnavailable,
                    "audio.already_recording",
                    IsRetryable: false));
            }

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                _device = string.Equals(microphoneId, "default", StringComparison.OrdinalIgnoreCase)
                    ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
                    : enumerator.GetDevice(microphoneId);
                _capture = new WasapiRecorderBuilder()
                    .WithDevice(_device)
                    .WithSharedMode()
                    .WithFormat(new WaveFormat(24_000, 16, 1))
                    .WithEventSync()
                    .WithBufferLength(50)
                    .WithMmcssThreadPriority("Capture")
                    .Build();
                var waveFormat = _capture.WaveFormat;
                _format = new AudioFormat(
                    waveFormat.SampleRate,
                    waveFormat.Channels,
                    waveFormat.BitsPerSample,
                    waveFormat.Encoding.ToString());
                _recordingId = Guid.NewGuid().ToString("N");
                _recordingDirectory = Path.Combine(_audioRoot, _recordingId);
                _sink = new ChunkedPcmSink(
                    _recordingDirectory,
                    _format,
                    _freeSpaceProbe);
                _sink.Faulted += OnSinkFaulted;
                _capture.DataAvailable += OnDataAvailable;
                _capture.RecordingStopped += OnRecordingStopped;
                _captureFailure = null;
                _recordingStopped = new TaskCompletionSource<OperationError?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _capture.StartRecording();
                return OperationResult.Success(Unit.Value);
            }
            catch (UnauthorizedAccessException exception)
            {
                CleanupFailedStart();
                return OperationResult.Failure<Unit>(new OperationError(
                    ErrorCategory.MicrophonePermissionDenied,
                    "audio.permission_denied",
                    IsRetryable: true,
                    exception.GetType().Name));
            }
            catch (Exception exception) when (exception is COMException or InvalidOperationException or IOException or NotSupportedException)
            {
                CleanupFailedStart();
                return OperationResult.Failure<Unit>(new OperationError(
                    ErrorCategory.MicrophoneUnavailable,
                    "audio.microphone_unavailable",
                    IsRetryable: true,
                    exception.GetType().Name));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OperationResult<AudioRecording>> StopAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_capture is null || _sink is null || _format is null ||
                _recordingDirectory is null || _recordingId is null)
            {
                return OperationResult.Failure<AudioRecording>(new OperationError(
                    ErrorCategory.MicrophoneUnavailable,
                    "audio.not_recording",
                    IsRetryable: false));
            }

            RequestCaptureStop();
            if (_recordingStopped is not null)
            {
                if (!await AudioCaptureStopPolicy
                        .CompletesWithinAsync(
                            _recordingStopped.Task,
                            NormalStopTimeout,
                            cancellationToken)
                        .ConfigureAwait(false))
                {
                    await DeleteCurrentRecordingAsync().ConfigureAwait(false);
                    return OperationResult.Failure<AudioRecording>(new OperationError(
                        ErrorCategory.MicrophoneUnavailable,
                        "audio.stop_timed_out",
                        IsRetryable: true,
                        "wasapi_recording_stopped_missing"));
                }

                _captureFailure ??= await _recordingStopped.Task.ConfigureAwait(false);
            }

            _sink.Complete();
            if (!await AudioCaptureStopPolicy
                    .CompletesWithinAsync(
                        _sink.Completion,
                        NormalStopTimeout,
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                await DeleteCurrentRecordingAsync().ConfigureAwait(false);
                return OperationResult.Failure<AudioRecording>(new OperationError(
                    ErrorCategory.StorageUnavailable,
                    "audio.flush_timed_out",
                    IsRetryable: true,
                    "audio_writer_did_not_finish"));
            }
            var failure = _captureFailure ?? _sink.Failure;
            var paths = _sink.ChunkPaths;
            var byteCount = _sink.BytesWritten;
            if (failure is not null)
            {
                await DeleteCurrentRecordingAsync().ConfigureAwait(false);
                return OperationResult.Failure<AudioRecording>(failure);
            }

            var duration = TimeSpan.FromSeconds((double)byteCount / _format.BytesPerSecond);
            var content = new ChunkedAudioContent(
                _recordingDirectory,
                paths,
                _format,
                duration,
                byteCount);
            var recording = new AudioRecording(
                _recordingId,
                content,
                DateTimeOffset.UtcNow - duration,
                null);
            _sink.Faulted -= OnSinkFaulted;
            CleanupCaptureObjects(disposeSink: false);
            return OperationResult.Success(recording);
        }
        catch (OperationCanceledException)
        {
            await DeleteCurrentRecordingAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CancelAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                if (_capture is not null)
                {
                    RequestCaptureStop();
                    if (_recordingStopped is not null)
                    {
                        try
                        {
                            await _recordingStopped.Task
                                .WaitAsync(CancellationStopTimeout, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (TimeoutException)
                        {
                            // A faulty audio driver must not keep cancellation or app shutdown stuck.
                        }
                    }
                }
            }
            finally
            {
                await DeleteCurrentRecordingAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await CancelAsync(CancellationToken.None).ConfigureAwait(false);
        _disposed = true;
        _gate.Dispose();
    }

    private void OnDataAvailable(
        ReadOnlySpan<byte> buffer,
        AudioClientBufferFlags flags,
        long devicePosition,
        long qpcPosition)
    {
        _sink?.TryWrite(buffer);
        if (DataAvailable is not null && !buffer.IsEmpty)
        {
            var chunk = buffer.ToArray();
            DataAvailable.Invoke(this, new AudioChunkAvailableEventArgs(chunk));
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        OperationError? failure = null;
        if (args.Exception is not null)
        {
            failure = new OperationError(
                ErrorCategory.MicrophoneUnavailable,
                "audio.capture_stopped",
                IsRetryable: true,
                args.Exception.GetType().Name);
            _captureFailure = failure;
        }

        _recordingStopped?.TrySetResult(failure);
    }

    private void OnSinkFaulted(object? sender, EventArgs args)
    {
        var capture = _capture;
        if (capture is null)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                capture.StopRecording();
            }
            catch (Exception exception)
            {
                _captureFailure ??= new OperationError(
                    ErrorCategory.MicrophoneUnavailable,
                    "audio.capture_stopped",
                    IsRetryable: true,
                    exception.GetType().Name);
            }
        });
    }

    private async Task DeleteCurrentRecordingAsync()
    {
        if (_sink is not null)
        {
            _sink.Faulted -= OnSinkFaulted;
            await _sink.DisposeAsync().ConfigureAwait(false);
        }

        CleanupCaptureObjects(disposeSink: false);
        if (_recordingDirectory is not null && Directory.Exists(_recordingDirectory))
        {
            TryDeleteDirectory(_recordingDirectory);
        }

        _recordingDirectory = null;
        _recordingId = null;
    }

    private void CleanupCaptureObjects(bool disposeSink = true)
    {
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.Dispose();
            _capture = null;
        }

        _device?.Dispose();
        _device = null;
        if (disposeSink && _sink is not null)
        {
            _sink.Faulted -= OnSinkFaulted;
            _sink.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _sink = null;
        _format = null;
        _captureFailure = null;
        _recordingStopped = null;
    }

    private void CleanupFailedStart()
    {
        var recordingDirectory = _recordingDirectory;
        CleanupCaptureObjects();
        if (!string.IsNullOrWhiteSpace(recordingDirectory) && Directory.Exists(recordingDirectory))
        {
            TryDeleteDirectory(recordingDirectory);
        }

        _recordingDirectory = null;
        _recordingId = null;
    }

    private void RequestCaptureStop()
    {
        if (_capture is null || _recordingStopped?.Task.IsCompleted == true)
        {
            return;
        }

        try
        {
            _capture.StopRecording();
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or ObjectDisposedException)
        {
            var failure = new OperationError(
                ErrorCategory.MicrophoneUnavailable,
                "audio.capture_stopped",
                IsRetryable: true,
                exception.GetType().Name);
            _captureFailure ??= failure;
            _recordingStopped?.TrySetResult(failure);
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Startup cleanup retries application-owned stale audio without destabilizing shutdown.
        }
    }
}

public sealed class TemporaryAudioCleanupService
{
    private readonly string _audioRoot;

    public TemporaryAudioCleanupService(string? audioRoot = null)
    {
        _audioRoot = Path.GetFullPath(audioRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VoicePaste",
            "Audio"));
    }

    public Task CleanupExpiredAsync(TimeSpan maximumAge, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_audioRoot))
        {
            return Task.CompletedTask;
        }

        var cutoff = DateTime.UtcNow - maximumAge;
        string[] directories;
        try
        {
            directories = Directory.GetDirectories(_audioRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Task.CompletedTask;
        }

        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(directory);
            if (!fullPath.StartsWith(_audioRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (Directory.GetLastWriteTimeUtc(fullPath) < cutoff)
                {
                    Directory.Delete(fullPath, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best effort: a later startup can retry a temporarily locked directory.
            }
        }

        return Task.CompletedTask;
    }
}

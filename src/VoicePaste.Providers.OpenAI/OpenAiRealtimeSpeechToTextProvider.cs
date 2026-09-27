using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using VoicePaste.Core;

namespace VoicePaste.Providers.OpenAI;

public sealed record OpenAiRealtimeProviderOptions(
    Uri WebSocketEndpoint,
    string Model,
    string CredentialReference,
    TimeSpan ConnectTimeout,
    TimeSpan ResponseTimeout,
    TimeSpan? ShutdownTimeout = null)
{
    public const string DefaultModel = "gpt-live-transcribe";
    public const string DefaultCredentialReference = "openai-api-key";

    public static OpenAiRealtimeProviderOptions Default { get; } = new(
        new Uri("wss://api.openai.com/v1/realtime?intent=transcription", UriKind.Absolute),
        DefaultModel,
        DefaultCredentialReference,
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(60));
}

public sealed class OpenAiRealtimeSpeechToTextProvider : IStreamingSpeechToTextProvider, IDisposable
{
    private readonly ICredentialStore _credentialStore;
    private readonly OpenAiRealtimeProviderOptions _options;
    private readonly Func<ClientWebSocket>? _webSocketFactory;
    private bool _disposed;

    public OpenAiRealtimeSpeechToTextProvider(
        ICredentialStore credentialStore,
        OpenAiRealtimeProviderOptions? options = null,
        Func<ClientWebSocket>? webSocketFactory = null)
    {
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _options = options ?? OpenAiRealtimeProviderOptions.Default;
        _webSocketFactory = webSocketFactory;
    }

    public string ProviderId => "openai-gpt-live-transcribe";

    public SpeechProviderCapabilities Capabilities { get; } = new(
        new HashSet<LanguageMode>(Enum.GetValues<LanguageMode>()),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "vi", "en", "vi-VN", "en-US" },
        MaximumRequestDuration: null,
        MaximumRequestBytes: null,
        SupportsStreaming: true,
        SendsAudioOffDevice: true);

    public async Task<OperationResult<IStreamingSpeechToTextSession>> StartSessionAsync(
        TranscriptionOptions options,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(options);

        string? apiKey;
        try
        {
            apiKey = await _credentialStore
                .ReadAsync(_options.CredentialReference, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return OperationResult.Failure<IStreamingSpeechToTextSession>(new OperationError(
                ErrorCategory.AuthenticationFailed,
                "provider.credential_unavailable",
                IsRetryable: true,
                exception.GetType().Name));
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return OperationResult.Failure<IStreamingSpeechToTextSession>(new OperationError(
                ErrorCategory.AuthenticationFailed,
                "provider.api_key_required",
                IsRetryable: false,
                "openai_credential_missing"));
        }

        var webSocket = _webSocketFactory?.Invoke() ?? new ClientWebSocket();
        webSocket.Options.SetRequestHeader("Authorization", $"Bearer {apiKey}");

        using var connectTimeoutCts = new CancellationTokenSource(_options.ConnectTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            connectTimeoutCts.Token);

        try
        {
            await webSocket.ConnectAsync(_options.WebSocketEndpoint, linkedCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            connectTimeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            webSocket.Dispose();
            return OperationResult.Failure<IStreamingSpeechToTextSession>(new OperationError(
                ErrorCategory.TimedOut,
                "provider.connect_timed_out",
                IsRetryable: true,
                "realtime_connect_timeout"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            webSocket.Dispose();
            throw;
        }
        catch (Exception exception)
        {
            webSocket.Dispose();
            DiagnosticLog.LogException("realtime.connect_failed", exception);
            return OperationResult.Failure<IStreamingSpeechToTextSession>(new OperationError(
                ErrorCategory.NetworkUnavailable,
                "provider.network_unavailable",
                IsRetryable: true,
                exception.GetType().Name));
        }

        var session = new OpenAiRealtimeSession(webSocket, _options);
        try
        {
            var initializationResult = await session
                .InitializeAsync(options, cancellationToken)
                .ConfigureAwait(false);
            if (initializationResult is OperationFailure<Unit> failure)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                return OperationResult.Failure<IStreamingSpeechToTextSession>(failure.Error);
            }

            return OperationResult.Success<IStreamingSpeechToTextSession>(session);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is WebSocketException or IOException)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            DiagnosticLog.LogException("realtime.session_init_failed", exception);
            return OperationResult.Failure<IStreamingSpeechToTextSession>(new OperationError(
                ErrorCategory.NetworkUnavailable,
                "provider.network_unavailable",
                IsRetryable: true,
                exception.GetType().Name));
        }
        catch (Exception exception)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            DiagnosticLog.LogException("realtime.session_init_failed_other", exception);
            return OperationResult.Failure<IStreamingSpeechToTextSession>(new OperationError(
                ErrorCategory.ProviderFailure,
                "provider.session_init_failed",
                IsRetryable: true,
                exception.GetType().Name));
        }
    }

    public void Dispose() => _disposed = true;
}

public sealed class OpenAiRealtimeSession : IStreamingSpeechToTextSession
{
    private static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly WebSocket _webSocket;
    private readonly OpenAiRealtimeProviderOptions _options;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _disposeGate = new();
    private readonly object _transcriptGate = new();
    private readonly StringBuilder _transcriptBuilder = new();
    private readonly TaskCompletionSource<OperationResult<Unit>> _initialization =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<OperationResult<TranscriptionOutput>> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _sessionCts = new();
    private readonly Stopwatch _stopwatch = new();
    private Task? _receiveTask;
    private Task? _disposeTask;
    private volatile bool _disposed;
    private bool _committed;

    public OpenAiRealtimeSession(WebSocket webSocket, OpenAiRealtimeProviderOptions options)
    {
        _webSocket = webSocket ?? throw new ArgumentNullException(nameof(webSocket));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public event EventHandler<string>? InterimTranscriptReceived;

    internal async Task<OperationResult<Unit>> InitializeAsync(
        TranscriptionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        _stopwatch.Start();
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_sessionCts.Token), _sessionCts.Token);

        var transcription = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["model"] = _options.Model,
        };
        var languages = GetLanguageHints(options);
        if (languages is not null)
        {
            transcription["languages"] = languages;
        }

        var sessionUpdate = new
        {
            type = "session.update",
            session = new
            {
                type = "transcription",
                audio = new
                {
                    input = new
                    {
                        format = new
                        {
                            type = "audio/pcm",
                            rate = 24000,
                        },
                        transcription,
                        turn_detection = (object?)null,
                    },
                },
            },
        };

        try
        {
            await SendJsonMessageAsync(sessionUpdate, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is WebSocketException or IOException)
        {
            // The server may have already rejected the connection (auth, model access, etc.)
            // and closed the socket before this send went out. The receive loop observes that
            // rejection first and records the real reason in _initialization; defer to it
            // below instead of masking it with a generic transport error here.
            DiagnosticLog.LogException("realtime.session_update_send_failed", exception);
        }

        using var initializationTimeoutCts = new CancellationTokenSource(_options.ConnectTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            initializationTimeoutCts.Token,
            _sessionCts.Token);
        try
        {
            return await _initialization.Task.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            initializationTimeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return OperationResult.Failure<Unit>(new OperationError(
                ErrorCategory.TimedOut,
                "provider.session_init_timed_out",
                IsRetryable: true,
                "realtime_session_update_timeout"));
        }
    }

    public async Task SendAudioChunkAsync(
        ReadOnlyMemory<byte> pcmChunk,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (pcmChunk.IsEmpty)
        {
            return;
        }

        var appendMessage = new
        {
            type = "input_audio_buffer.append",
            audio = Convert.ToBase64String(pcmChunk.Span),
        };

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_committed)
            {
                return;
            }

            await SendJsonMessageCoreAsync(appendMessage, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<OperationResult<TranscriptionOutput>> CompleteAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var responseTimeoutCts = new CancellationTokenSource(_options.ResponseTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            responseTimeoutCts.Token,
            _sessionCts.Token);

        try
        {
            await _sendLock.WaitAsync(linkedCts.Token).ConfigureAwait(false);
            try
            {
                if (!_committed)
                {
                    _committed = true;
                    await SendJsonMessageCoreAsync(
                            new { type = "input_audio_buffer.commit" },
                            linkedCts.Token)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                _sendLock.Release();
            }

            return await _completion.Task.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            responseTimeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            if (HasTranscript())
            {
                return CreateSuccessfulOutput();
            }

            return OperationResult.Failure<TranscriptionOutput>(new OperationError(
                ErrorCategory.TimedOut,
                "provider.response_timed_out",
                IsRetryable: true,
                "realtime_response_timeout"));
        }
        catch (OperationCanceledException) when (
            _sessionCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            if (HasTranscript())
            {
                return CreateSuccessfulOutput();
            }

            return OperationResult.Failure<TranscriptionOutput>(new OperationError(
                ErrorCategory.ProviderFailure,
                "provider.websocket_receive_error",
                IsRetryable: true,
                "realtime_session_ended"));
        }
        catch (Exception exception) when (exception is WebSocketException or IOException)
        {
            if (HasTranscript())
            {
                return CreateSuccessfulOutput();
            }

            return OperationResult.Failure<TranscriptionOutput>(new OperationError(
                ErrorCategory.ProviderFailure,
                "provider.websocket_error",
                IsRetryable: true,
                exception.GetType().Name));
        }
    }

    public async Task CancelAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _sessionCts.Cancel();
            if (_webSocket.State == WebSocketState.Open)
            {
                using var closeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                closeCts.CancelAfter(TimeSpan.FromSeconds(2));
                await _webSocket.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Cancelled by user",
                        closeCts.Token)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // Best-effort cancellation must not replace the user's cancellation result.
        }
        finally
        {
            _initialization.TrySetCanceled(CancellationToken.None);
            _completion.TrySetCanceled(CancellationToken.None);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        _sessionCts.Cancel();
        _initialization.TrySetCanceled(CancellationToken.None);
        _completion.TrySetCanceled(CancellationToken.None);

        try
        {
            if (_receiveTask is not null)
            {
                try
                {
                    await _receiveTask
                        .WaitAsync(_options.ShutdownTimeout ?? DefaultShutdownTimeout)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    _webSocket.Abort();
                    await _receiveTask
                        .WaitAsync(_options.ShutdownTimeout ?? DefaultShutdownTimeout)
                        .ConfigureAwait(false);
                }
            }
        }
        catch
        {
            // Background receiver shutdown is best effort during disposal.
        }
        finally
        {
            _webSocket.Dispose();
            _sessionCts.Dispose();
            // Do not dispose the semaphore: an uncooperative in-flight WebSocket send may still
            // execute its finally/release after bounded shutdown has returned.
        }
    }

    private async Task SendJsonMessageAsync<T>(T payload, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SendJsonMessageCoreAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task SendJsonMessageCoreAsync<T>(T payload, CancellationToken cancellationToken)
    {
        if (_webSocket.State != WebSocketState.Open)
        {
            throw new WebSocketException(
                WebSocketError.InvalidState,
                $"Realtime WebSocket is {_webSocket.State}.");
        }

        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        await _webSocket.SendAsync(
                jsonBytes,
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[32 * 1024];
        using var memoryStream = new MemoryStream();

        try
        {
            while (!cancellationToken.IsCancellationRequested && _webSocket.State == WebSocketState.Open)
            {
                memoryStream.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _webSocket.ReceiveAsync(buffer, cancellationToken)
                        .ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        DiagnosticLog.LogMessage(
                            "realtime.websocket_closed",
                            $"CloseStatus={_webSocket.CloseStatus}");
                        SetTransportFailure("realtime_websocket_closed");
                        return;
                    }

                    memoryStream.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    memoryStream.Position = 0;
                    ProcessServerEvent(memoryStream);
                }
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                SetTransportFailure("realtime_websocket_not_open");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal cancellation or disposal.
        }
        catch (Exception exception)
        {
            SetTransportFailure(exception.GetType().Name);
        }
    }

    private void ProcessServerEvent(Stream utf8Stream)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8Stream);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement))
            {
                return;
            }

            var eventType = typeElement.GetString();
            if (eventType is "error" or "conversation.item.input_audio_transcription.failed")
            {
                DiagnosticLog.LogMessage(
                    $"realtime.server_event.{eventType}",
                    GetSanitizedErrorMetadata(root));
            }

            switch (eventType)
            {
                case "session.updated":
                    _initialization.TrySetResult(OperationResult.Success(Unit.Value));
                    break;

                case "conversation.item.input_audio_transcription.delta":
                    AppendTranscriptDelta(root);
                    break;

                case "conversation.item.input_audio_transcription.completed":
                    CompleteTranscript(root);
                    break;

                case "conversation.item.input_audio_transcription.failed":
                    _completion.TrySetResult(OperationResult.Failure<TranscriptionOutput>(
                        MapServerError(root, "provider.transcription_failed")));
                    break;

                case "error":
                    HandleServerError(root);
                    break;
            }
        }
        catch (JsonException)
        {
            // A malformed intermediate frame is ignored; a later valid event can still finish the session.
        }
    }

    private void AppendTranscriptDelta(JsonElement root)
    {
        if (!root.TryGetProperty("delta", out var deltaElement))
        {
            return;
        }

        var delta = deltaElement.GetString();
        if (string.IsNullOrEmpty(delta))
        {
            return;
        }

        string interimTranscript;
        lock (_transcriptGate)
        {
            _transcriptBuilder.Append(delta);
            interimTranscript = _transcriptBuilder.ToString();
        }

        InterimTranscriptReceived?.Invoke(this, interimTranscript);
    }

    private void CompleteTranscript(JsonElement root)
    {
        _stopwatch.Stop();
        var completedText = root.TryGetProperty("transcript", out var transcriptElement)
            ? transcriptElement.GetString()?.Trim()
            : null;
        string finalText;
        lock (_transcriptGate)
        {
            if (!string.IsNullOrWhiteSpace(completedText))
            {
                _transcriptBuilder.Clear();
                _transcriptBuilder.Append(completedText);
            }

            finalText = _transcriptBuilder.ToString();
        }

        InterimTranscriptReceived?.Invoke(this, finalText);
        _completion.TrySetResult(OperationResult.Success(new TranscriptionOutput(
            finalText,
            DetectedLanguage: null,
            _stopwatch.Elapsed)));
    }

    private void HandleServerError(JsonElement root)
    {
        var errorCode = GetErrorProperty(root, "code");
        if (string.Equals(errorCode, "input_audio_buffer_commit_empty", StringComparison.Ordinal))
        {
            _stopwatch.Stop();
            _completion.TrySetResult(OperationResult.Success(new TranscriptionOutput(
                string.Empty,
                DetectedLanguage: null,
                _stopwatch.Elapsed)));
            return;
        }

        var error = MapServerError(root, "provider.realtime_error");
        _initialization.TrySetResult(OperationResult.Failure<Unit>(error));
        _completion.TrySetResult(OperationResult.Failure<TranscriptionOutput>(error));
    }

    private void SetTransportFailure(string diagnosticCode)
    {
        var error = new OperationError(
            ErrorCategory.ProviderFailure,
            "provider.websocket_receive_error",
            IsRetryable: true,
            diagnosticCode);
        _initialization.TrySetResult(OperationResult.Failure<Unit>(error));
        _completion.TrySetResult(OperationResult.Failure<TranscriptionOutput>(error));
    }

    private OperationResult<TranscriptionOutput> CreateSuccessfulOutput()
    {
        _stopwatch.Stop();
        string transcript;
        lock (_transcriptGate)
        {
            transcript = _transcriptBuilder.ToString();
        }

        return OperationResult.Success(new TranscriptionOutput(
            transcript,
            DetectedLanguage: null,
            _stopwatch.Elapsed));
    }

    private bool HasTranscript()
    {
        lock (_transcriptGate)
        {
            return _transcriptBuilder.Length > 0;
        }
    }

    internal static string GetSanitizedErrorMetadata(JsonElement root)
    {
        var type = GetErrorProperty(root, "type") ?? "unknown";
        var code = GetErrorProperty(root, "code") ?? "unknown";
        return $"Type={SanitizeDiagnosticToken(type)} Code={SanitizeDiagnosticToken(code)}";
    }

    private static string[]? GetLanguageHints(TranscriptionOptions options)
    {
        if (options.LanguageMode == LanguageMode.Automatic)
        {
            return null;
        }

        if (options.LanguageMode == LanguageMode.Vietnamese)
        {
            return ["vi"];
        }

        if (options.LanguageMode == LanguageMode.English)
        {
            return ["en"];
        }

        var languages = options.LocaleHints
            .Select(value => value.Split('-', 2, StringSplitOptions.TrimEntries)[0])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return languages.Length == 0 ? ["vi", "en"] : languages;
    }

    private static OperationError MapServerError(JsonElement root, string fallbackMessageKey)
    {
        var code = GetErrorProperty(root, "code");
        var type = GetErrorProperty(root, "type");
        var identifier = code ?? type;
        var diagnosticCode = string.IsNullOrWhiteSpace(identifier)
            ? "openai_realtime_error"
            : $"openai_{SanitizeDiagnosticToken(identifier)}";

        if (Contains(identifier, "invalid_api_key") || Contains(type, "authentication"))
        {
            return new OperationError(
                ErrorCategory.AuthenticationFailed,
                "provider.authentication_failed",
                IsRetryable: false,
                diagnosticCode);
        }

        if (Contains(identifier, "quota") || Contains(identifier, "rate_limit"))
        {
            return new OperationError(
                ErrorCategory.QuotaExceeded,
                "provider.quota_or_rate_limit",
                IsRetryable: !Contains(identifier, "insufficient_quota"),
                diagnosticCode);
        }

        if (Contains(identifier, "timeout"))
        {
            return new OperationError(
                ErrorCategory.TimedOut,
                "provider.timed_out",
                IsRetryable: true,
                diagnosticCode);
        }

        var isInvalidRequest = Contains(type, "invalid_request") ||
            Contains(identifier, "invalid_request") ||
            Contains(identifier, "unsupported");
        return new OperationError(
            ErrorCategory.ProviderFailure,
            isInvalidRequest ? "provider.request_rejected" : fallbackMessageKey,
            IsRetryable: !isInvalidRequest,
            diagnosticCode);
    }

    private static string? GetErrorProperty(JsonElement root, string propertyName)
    {
        var errorObject = root.TryGetProperty("error", out var nestedError) &&
            nestedError.ValueKind == JsonValueKind.Object
                ? nestedError
                : root;
        return errorObject.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static bool Contains(string? value, string fragment) =>
        value?.Contains(fragment, StringComparison.OrdinalIgnoreCase) == true;

    private static string SanitizeDiagnosticToken(string value)
    {
        const int maximumLength = 96;
        var builder = new StringBuilder(Math.Min(value.Length, maximumLength));
        foreach (var character in value.Take(maximumLength))
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.'
                ? character
                : '_');
        }

        return builder.ToString();
    }
}

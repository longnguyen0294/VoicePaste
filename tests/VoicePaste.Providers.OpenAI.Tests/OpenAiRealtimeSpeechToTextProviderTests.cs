using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VoicePaste.Core;
using VoicePaste.Providers.OpenAI;

namespace VoicePaste.Providers.OpenAI.Tests;

[TestClass]
public sealed class OpenAiRealtimeSpeechToTextProviderTests
{
    private static readonly string[] ExpectedMixedLanguages = ["vi", "en"];

    [TestMethod]
    public void UtRealtime001ProviderCapabilitiesExposeLiveTranscription()
    {
        var credentialStore = new FakeCredentialStore("test-api-key");
        var provider = new OpenAiRealtimeSpeechToTextProvider(credentialStore);

        Assert.AreEqual("openai-gpt-live-transcribe", provider.ProviderId);
        Assert.AreEqual("gpt-live-transcribe", OpenAiRealtimeProviderOptions.Default.Model);
        StringAssert.Contains(
            OpenAiRealtimeProviderOptions.Default.WebSocketEndpoint.Query,
            "intent=transcription");
        Assert.IsTrue(provider.Capabilities.SupportsStreaming);
        Assert.IsTrue(provider.Capabilities.SendsAudioOffDevice);
        Assert.IsTrue(provider.Capabilities.LocaleHints.Contains("vi"));
        Assert.IsTrue(provider.Capabilities.LocaleHints.Contains("en"));
    }

    [TestMethod]
    public async Task UtRealtime002StartSessionAsyncFailsWhenApiKeyMissing()
    {
        var credentialStore = new FakeCredentialStore(null);
        var provider = new OpenAiRealtimeSpeechToTextProvider(credentialStore);

        var options = new TranscriptionOptions(
            LanguageMode.VietnameseEnglishMixed,
            ["vi-VN", "en-US"],
            TrimWhitespace: true);
        var result = await provider.StartSessionAsync(options, CancellationToken.None);

        var failure = AssertFailure<IStreamingSpeechToTextSession>(result);
        Assert.AreEqual(ErrorCategory.AuthenticationFailed, failure.Error.Category);
        Assert.AreEqual("provider.api_key_required", failure.Error.UserMessageKey);
    }

    [TestMethod]
    public async Task UtRealtime003SessionUpdateUsesTranscriptionSchemaAndMixedLanguages()
    {
        using var webSocket = new ScriptedWebSocket(message =>
            GetMessageType(message) == "session.update"
                ? ["{\"type\":\"session.updated\"}"]
                : []);
        await using var session = new OpenAiRealtimeSession(webSocket, CreateOptions());

        var result = await session.InitializeAsync(
            new TranscriptionOptions(
                LanguageMode.VietnameseEnglishMixed,
                ["vi-VN", "en-US", "vi"],
                TrimWhitespace: true),
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        using var document = JsonDocument.Parse(webSocket.SentMessages.Single());
        var root = document.RootElement;
        Assert.AreEqual("session.update", root.GetProperty("type").GetString());
        var configuredSession = root.GetProperty("session");
        Assert.AreEqual("transcription", configuredSession.GetProperty("type").GetString());
        var input = configuredSession.GetProperty("audio").GetProperty("input");
        Assert.AreEqual("audio/pcm", input.GetProperty("format").GetProperty("type").GetString());
        Assert.AreEqual(24000, input.GetProperty("format").GetProperty("rate").GetInt32());
        Assert.AreEqual(JsonValueKind.Null, input.GetProperty("turn_detection").ValueKind);
        var transcription = input.GetProperty("transcription");
        Assert.AreEqual("gpt-live-transcribe", transcription.GetProperty("model").GetString());
        CollectionAssert.AreEqual(
            ExpectedMixedLanguages,
            transcription.GetProperty("languages").EnumerateArray().Select(value => value.GetString()).ToArray());
        Assert.IsFalse(transcription.TryGetProperty("language", out _));
        Assert.IsFalse(configuredSession.TryGetProperty("output_modalities", out _));
    }

    [TestMethod]
    public async Task UtRealtime004AutomaticLanguageOmitsLanguageHints()
    {
        using var webSocket = new ScriptedWebSocket(message =>
            GetMessageType(message) == "session.update"
                ? ["{\"type\":\"session.updated\"}"]
                : []);
        await using var session = new OpenAiRealtimeSession(webSocket, CreateOptions());

        var result = await session.InitializeAsync(
            new TranscriptionOptions(LanguageMode.Automatic, ["vi-VN", "en-US"], TrimWhitespace: true),
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        using var document = JsonDocument.Parse(webSocket.SentMessages.Single());
        var transcription = document.RootElement
            .GetProperty("session")
            .GetProperty("audio")
            .GetProperty("input")
            .GetProperty("transcription");
        Assert.IsFalse(transcription.TryGetProperty("languages", out _));
        Assert.IsFalse(transcription.TryGetProperty("language", out _));
    }

    [TestMethod]
    public async Task UtRealtime005StreamsAudioAndCompletesFromTranscriptEvents()
    {
        using var webSocket = new ScriptedWebSocket(message => GetMessageType(message) switch
        {
            "session.update" => ["{\"type\":\"session.updated\"}"],
            "input_audio_buffer.commit" =>
            [
                "{\"type\":\"conversation.item.input_audio_transcription.delta\",\"delta\":\"xin \"}",
                "{\"type\":\"conversation.item.input_audio_transcription.completed\",\"transcript\":\"xin chào OpenAI\"}",
            ],
            _ => [],
        });
        await using var session = new OpenAiRealtimeSession(webSocket, CreateOptions());
        string? interim = null;
        session.InterimTranscriptReceived += (_, transcript) => interim = transcript;
        var initialization = await session.InitializeAsync(
            new TranscriptionOptions(LanguageMode.VietnameseEnglishMixed, ["vi", "en"], true),
            CancellationToken.None);
        Assert.IsTrue(initialization.IsSuccess);

        await session.SendAudioChunkAsync(new byte[] { 1, 2, 3, 4 }, CancellationToken.None);
        var result = await session.CompleteAsync(CancellationToken.None);

        var success = AssertSuccess(result);
        Assert.AreEqual("xin chào OpenAI", success.Value.Text);
        Assert.AreEqual("xin chào OpenAI", interim);
        Assert.IsTrue(webSocket.SentMessages.Any(message => GetMessageType(message) == "input_audio_buffer.append"));
        Assert.AreEqual("input_audio_buffer.commit", GetMessageType(webSocket.SentMessages.Last()));
    }

    [TestMethod]
    public async Task UtRealtime006MapsTranscriptionFailureWithoutLeakingProviderMessage()
    {
        const string sensitiveMessage = "Request failed while transcribing customer secret text";
        using var webSocket = new ScriptedWebSocket(message => GetMessageType(message) switch
        {
            "session.update" => ["{\"type\":\"session.updated\"}"],
            "input_audio_buffer.commit" =>
            [
                "{\"type\":\"conversation.item.input_audio_transcription.failed\",\"error\":{" +
                "\"type\":\"rate_limit_error\",\"code\":\"rate_limit_exceeded\",\"message\":\"" +
                sensitiveMessage + "\"}}",
            ],
            _ => [],
        });
        await using var session = new OpenAiRealtimeSession(webSocket, CreateOptions());
        var initialization = await session.InitializeAsync(
            new TranscriptionOptions(LanguageMode.English, ["en"], true),
            CancellationToken.None);
        Assert.IsTrue(initialization.IsSuccess);

        await session.SendAudioChunkAsync(new byte[] { 1, 2 }, CancellationToken.None);
        var result = await session.CompleteAsync(CancellationToken.None);

        var failure = AssertFailure<TranscriptionOutput>(result);
        Assert.AreEqual(ErrorCategory.QuotaExceeded, failure.Error.Category);
        Assert.AreEqual("provider.quota_or_rate_limit", failure.Error.UserMessageKey);
        Assert.AreEqual("openai_rate_limit_exceeded", failure.Error.DiagnosticCode);
        Assert.IsFalse(failure.Error.DiagnosticCode!.Contains(sensitiveMessage, StringComparison.Ordinal));

        using var serverEvent = JsonDocument.Parse(
            "{\"type\":\"conversation.item.input_audio_transcription.failed\",\"error\":{" +
            "\"type\":\"rate_limit_error\",\"code\":\"rate_limit_exceeded\",\"message\":\"" +
            sensitiveMessage + "\"}}");
        var safeMetadata = OpenAiRealtimeSession.GetSanitizedErrorMetadata(serverEvent.RootElement);
        Assert.IsFalse(safeMetadata.Contains(sensitiveMessage, StringComparison.Ordinal));
        StringAssert.Contains(safeMetadata, "rate_limit_exceeded");

        var exceptionMetadata = DiagnosticLog.FormatExceptionMetadata(
            new InvalidOperationException(sensitiveMessage));
        Assert.IsFalse(exceptionMetadata.Contains(sensitiveMessage, StringComparison.Ordinal));
        StringAssert.Contains(exceptionMetadata, nameof(InvalidOperationException));
    }

    [TestMethod]
    public async Task UtRealtime007RejectsInvalidSessionUpdateBeforeListening()
    {
        using var webSocket = new ScriptedWebSocket(message =>
            GetMessageType(message) == "session.update"
                ?
                [
                    "{\"type\":\"error\",\"error\":{" +
                    "\"type\":\"invalid_request_error\",\"code\":\"invalid_value\"," +
                    "\"message\":\"Invalid session configuration\"}}",
                ]
                : []);
        await using var session = new OpenAiRealtimeSession(webSocket, CreateOptions());

        var result = await session.InitializeAsync(
            new TranscriptionOptions(LanguageMode.English, ["en"], true),
            CancellationToken.None);

        var failure = AssertFailure<Unit>(result);
        Assert.AreEqual(ErrorCategory.ProviderFailure, failure.Error.Category);
        Assert.AreEqual("provider.request_rejected", failure.Error.UserMessageKey);
        Assert.IsFalse(failure.Error.IsRetryable);
        Assert.AreEqual("openai_invalid_value", failure.Error.DiagnosticCode);
    }

    [TestMethod]
    public async Task UtRealtime008ConcurrentDisposeAbortsUncooperativeReceiverAndCompletes()
    {
        using var webSocket = new UncooperativeReceiveWebSocket();
        var options = CreateOptions() with { ShutdownTimeout = TimeSpan.FromMilliseconds(50) };
        var session = new OpenAiRealtimeSession(webSocket, options);
        var initialization = await session.InitializeAsync(
            new TranscriptionOptions(LanguageMode.English, ["en"], true),
            CancellationToken.None);
        Assert.IsTrue(initialization.IsSuccess);

        var firstDispose = session.DisposeAsync().AsTask();
        var secondDispose = session.DisposeAsync().AsTask();
        await Task.WhenAll(firstDispose, secondDispose).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual(1, webSocket.AbortCount);
        Assert.AreSame(firstDispose, secondDispose);
    }

    private static OpenAiRealtimeProviderOptions CreateOptions() => new(
        new Uri("wss://example.test/v1/realtime?model=gpt-live-transcribe"),
        "gpt-live-transcribe",
        "test-credential",
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(2));

    private static string? GetMessageType(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("type").GetString();
    }

    private static OperationFailure<T> AssertFailure<T>(OperationResult<T> result)
    {
        Assert.IsInstanceOfType<OperationFailure<T>>(result);
        return (OperationFailure<T>)result;
    }

    private static OperationSuccess<T> AssertSuccess<T>(OperationResult<T> result)
    {
        Assert.IsInstanceOfType<OperationSuccess<T>>(result);
        return (OperationSuccess<T>)result;
    }

    private sealed class FakeCredentialStore(string? apiKey) : ICredentialStore
    {
        public Task<string?> ReadAsync(string reference, CancellationToken cancellationToken) =>
            Task.FromResult(apiKey);

        public Task SaveAsync(string reference, string secret, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DeleteAsync(string reference, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<StoredCredentialDescriptor>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StoredCredentialDescriptor>>([]);
    }

    private sealed class ScriptedWebSocket(
        Func<string, IReadOnlyList<string>> responseFactory) : WebSocket
    {
        private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();
        private WebSocketState _state = WebSocketState.Open;
        private WebSocketCloseStatus? _closeStatus;
        private string? _closeStatusDescription;

        public ConcurrentQueue<string> SentMessages { get; } = new();

        public override WebSocketCloseStatus? CloseStatus => _closeStatus;

        public override string? CloseStatusDescription => _closeStatusDescription;

        public override WebSocketState State => _state;

        public override string? SubProtocol => null;

        public override void Abort()
        {
            _state = WebSocketState.Aborted;
            _incoming.Writer.TryComplete();
        }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _closeStatus = closeStatus;
            _closeStatusDescription = statusDescription;
            _state = WebSocketState.Closed;
            _incoming.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _closeStatus = closeStatus;
            _closeStatusDescription = statusDescription;
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            _state = WebSocketState.Closed;
            _incoming.Writer.TryComplete();
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            var message = await _incoming.Reader.ReadAsync(cancellationToken);
            var bytes = Encoding.UTF8.GetBytes(message);
            if (bytes.Length > buffer.Count)
            {
                throw new InvalidOperationException("Test WebSocket message exceeds the receive buffer.");
            }

            bytes.CopyTo(buffer.AsSpan());
            return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, endOfMessage: true);
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            var message = Encoding.UTF8.GetString(buffer);
            SentMessages.Enqueue(message);
            foreach (var response in responseFactory(message))
            {
                _incoming.Writer.TryWrite(response);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class UncooperativeReceiveWebSocket : WebSocket
    {
        private readonly TaskCompletionSource _sessionUpdated = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<WebSocketReceiveResult> _hungReceive = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private WebSocketState _state = WebSocketState.Open;
        private int _receiveCount;

        public int AbortCount { get; private set; }

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override WebSocketState State => _state;

        public override string? SubProtocol => null;

        public override void Abort()
        {
            AbortCount++;
            _state = WebSocketState.Aborted;
            _hungReceive.TrySetException(new WebSocketException("aborted"));
        }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            _state = WebSocketState.Closed;
            _hungReceive.TrySetException(new ObjectDisposedException(nameof(UncooperativeReceiveWebSocket)));
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _receiveCount) == 1)
            {
                await _sessionUpdated.Task;
                var bytes = Encoding.UTF8.GetBytes("{\"type\":\"session.updated\"}");
                bytes.CopyTo(buffer.AsSpan());
                return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true);
            }

            return await _hungReceive.Task;
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            _sessionUpdated.TrySetResult();
            return Task.CompletedTask;
        }
    }
}

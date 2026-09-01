using System.Buffers.Binary;
using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VoicePaste.Core;

namespace VoicePaste.Providers.OpenAI.Tests;

[TestClass]
public sealed class OpenAiSpeechToTextProviderTests
{
    private static readonly string[] ExpectedAuthorizationParameters =
        ["test-key", "test-key", "test-key"];

    [TestMethod]
    public async Task UtStt004SegmentsWavInOrderAndCombinesMixedTranscript()
    {
        var audioBytes = Enumerable.Range(0, 20).Select(value => (byte)value).ToArray();
        var handler = new RecordingHandler(index => JsonResponse($"segment-{index}"));
        using var httpClient = new HttpClient(handler);
        using var provider = CreateProvider(httpClient, new FakeCredentialStore("test-key"), maximumBytes: 8);

        var result = await provider.TranscribeAsync(
            CreateRecording(audioBytes),
            new TranscriptionOptions(
                LanguageMode.VietnameseEnglishMixed,
                ["vi-VN", "en-US"],
                TrimWhitespace: true),
            CancellationToken.None);

        var success = result as OperationSuccess<TranscriptionOutput>;
        Assert.IsNotNull(success);
        Assert.AreEqual("segment-0 segment-1 segment-2", success.Value.Text);
        Assert.AreEqual(3, handler.Bodies.Count);
        Assert.IsTrue(handler.AuthorizationSchemes.All(value => value == "Bearer"));
        CollectionAssert.AreEqual(ExpectedAuthorizationParameters, handler.AuthorizationParameters);

        var reconstructed = new List<byte>();
        for (var index = 0; index < handler.Bodies.Count; index++)
        {
            var body = handler.Bodies[index];
            var riffOffset = FindSequence(body, "RIFF"u8);
            Assert.IsTrue(riffOffset >= 0);
            Assert.AreEqual("WAVE", Encoding.ASCII.GetString(body, riffOffset + 8, 4));
            Assert.AreEqual(1, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(riffOffset + 20, 2)));
            Assert.AreEqual(16_000u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(riffOffset + 24, 4)));
            Assert.AreEqual(1, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(riffOffset + 22, 2)));
            Assert.AreEqual(16, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(riffOffset + 34, 2)));
            var dataLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
                body.AsSpan(riffOffset + 40, 4)));
            reconstructed.AddRange(body.AsSpan(riffOffset + 44, dataLength).ToArray());

            var multipartText = Encoding.UTF8.GetString(body);
            StringAssert.Contains(multipartText, "languages[]");
            StringAssert.Contains(multipartText, "vi");
            StringAssert.Contains(multipartText, "en");
            if (index > 0)
            {
                StringAssert.Contains(multipartText, "Previous transcript context");
            }
        }

        CollectionAssert.AreEqual(audioBytes, reconstructed.ToArray());
    }

    [TestMethod]
    public async Task UtStt001MissingCredentialDoesNotSendAudio()
    {
        var handler = new RecordingHandler(_ => JsonResponse("should-not-run"));
        using var provider = CreateProvider(
            new HttpClient(handler),
            new FakeCredentialStore(secret: null),
            maximumBytes: 1024,
            ownsHttpClient: true);

        var result = await provider.TranscribeAsync(
            CreateRecording([1, 2, 3, 4]),
            MixedOptions(),
            CancellationToken.None);

        var failure = result as OperationFailure<TranscriptionOutput>;
        Assert.IsNotNull(failure);
        Assert.AreEqual(ErrorCategory.AuthenticationFailed, failure.Error.Category);
        Assert.AreEqual(0, handler.Bodies.Count);
    }

    [TestMethod]
    public async Task UtStt001CredentialReadFailureDoesNotSendAudio()
    {
        var handler = new RecordingHandler(_ => JsonResponse("should-not-run"));
        using var provider = CreateProvider(
            new HttpClient(handler),
            new ThrowingCredentialStore(new InvalidOperationException("vault unavailable")),
            maximumBytes: 1024,
            ownsHttpClient: true);

        var result = await provider.TranscribeAsync(
            CreateRecording([1, 2, 3, 4]),
            MixedOptions(),
            CancellationToken.None);

        var failure = result as OperationFailure<TranscriptionOutput>;
        Assert.IsNotNull(failure);
        Assert.AreEqual(ErrorCategory.AuthenticationFailed, failure.Error.Category);
        Assert.AreEqual("provider.credential_unavailable", failure.Error.UserMessageKey);
        Assert.AreEqual(0, handler.Bodies.Count);
    }

    [TestMethod]
    public async Task UtStt003RejectsMisalignedPcmBeforeSendingAudio()
    {
        var handler = new RecordingHandler(_ => JsonResponse("should-not-run"));
        using var provider = CreateProvider(
            new HttpClient(handler),
            new FakeCredentialStore("test-key"),
            maximumBytes: 1024,
            ownsHttpClient: true);

        var result = await provider.TranscribeAsync(
            CreateRecording([1, 2, 3]),
            MixedOptions(),
            CancellationToken.None);

        var failure = result as OperationFailure<TranscriptionOutput>;
        Assert.IsNotNull(failure);
        Assert.AreEqual(ErrorCategory.AudioEncodingFailed, failure.Error.Category);
        Assert.AreEqual(0, handler.Bodies.Count);
    }

    [TestMethod]
    [DataRow(401, ErrorCategory.AuthenticationFailed, false)]
    [DataRow(413, ErrorCategory.ProviderPayloadTooLarge, true)]
    [DataRow(429, ErrorCategory.QuotaExceeded, false)]
    [DataRow(500, ErrorCategory.ProviderFailure, true)]
    public async Task UtStt001MapsProviderHttpErrors(
        int statusCode,
        ErrorCategory expectedCategory,
        bool expectedRetryable)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage((HttpStatusCode)statusCode)
        {
            Content = new StringContent(
                "{\"error\":{\"code\":\"insufficient_quota\"}}",
                Encoding.UTF8,
                "application/json"),
        });
        using var provider = CreateProvider(
            new HttpClient(handler),
            new FakeCredentialStore("test-key"),
            maximumBytes: 1024,
            ownsHttpClient: true);

        var result = await provider.TranscribeAsync(
            CreateRecording([1, 2, 3, 4]),
            MixedOptions(),
            CancellationToken.None);

        var failure = result as OperationFailure<TranscriptionOutput>;
        Assert.IsNotNull(failure);
        Assert.AreEqual(expectedCategory, failure.Error.Category);
        Assert.AreEqual(expectedRetryable, failure.Error.IsRetryable);
    }

    [TestMethod]
    public async Task UtStt001MapsNetworkFailureWithoutLeakingCredential()
    {
        var handler = new ThrowingHandler(new HttpRequestException("network unavailable"));
        using var provider = CreateProvider(
            new HttpClient(handler),
            new FakeCredentialStore("sensitive-test-key"),
            maximumBytes: 1024,
            ownsHttpClient: true);

        var result = await provider.TranscribeAsync(
            CreateRecording([1, 2, 3, 4]),
            MixedOptions(),
            CancellationToken.None);

        var failure = result as OperationFailure<TranscriptionOutput>;
        Assert.IsNotNull(failure);
        Assert.AreEqual(ErrorCategory.NetworkUnavailable, failure.Error.Category);
        Assert.IsFalse((failure.Error.DiagnosticCode ?? string.Empty).Contains(
            "sensitive-test-key",
            StringComparison.Ordinal));
    }

    private static OpenAiSpeechToTextProvider CreateProvider(
        HttpClient httpClient,
        ICredentialStore credentialStore,
        long maximumBytes,
        bool ownsHttpClient = false) => new(
        httpClient,
        credentialStore,
        new OpenAiSpeechProviderOptions(
            new Uri("https://api.openai.test/v1/audio/transcriptions"),
            OpenAiSpeechProviderOptions.DefaultModel,
            OpenAiSpeechProviderOptions.DefaultCredentialReference,
            TimeSpan.FromMinutes(1),
            maximumBytes),
        ownsHttpClient);

    private static AudioRecording CreateRecording(byte[] bytes) => new(
        "recording-test",
        new MemoryAudioContent(bytes),
        DateTimeOffset.UtcNow,
        ExpiresAt: null);

    private static TranscriptionOptions MixedOptions() => new(
        LanguageMode.VietnameseEnglishMixed,
        ["vi-VN", "en-US"],
        TrimWhitespace: true);

    private static HttpResponseMessage JsonResponse(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            $"{{\"text\":\"{text}\"}}",
            Encoding.UTF8,
            "application/json"),
    };

    private static int FindSequence(byte[] haystack, ReadOnlySpan<byte> needle)
    {
        for (var index = 0; index <= haystack.Length - needle.Length; index++)
        {
            if (haystack.AsSpan(index, needle.Length).SequenceEqual(needle))
            {
                return index;
            }
        }

        return -1;
    }

    private sealed class MemoryAudioContent(byte[] bytes) : IAudioContent
    {
        public AudioFormat Format { get; } = new(16_000, 1, 16, "pcm");

        public TimeSpan Duration { get; } = TimeSpan.FromSeconds((double)bytes.Length / 32_000);

        public long? LengthBytes => bytes.LongLength;

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeCredentialStore(string? secret) : ICredentialStore
    {
        public Task SaveAsync(string reference, string value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string?> ReadAsync(string reference, CancellationToken cancellationToken) =>
            Task.FromResult(secret);

        public Task<IReadOnlyList<StoredCredentialDescriptor>> ListAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string reference, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingCredentialStore(Exception exception) : ICredentialStore
    {
        public Task SaveAsync(string reference, string value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string?> ReadAsync(string reference, CancellationToken cancellationToken) =>
            Task.FromException<string?>(exception);

        public Task<IReadOnlyList<StoredCredentialDescriptor>> ListAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string reference, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingHandler(Func<int, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public List<byte[]> Bodies { get; } = [];

        public List<string> AuthorizationSchemes { get; } = [];

        public string[] AuthorizationParameters { get; private set; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var index = Bodies.Count;
            Bodies.Add(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            AuthorizationSchemes.Add(request.Headers.Authorization?.Scheme ?? string.Empty);
            AuthorizationParameters = [
                .. AuthorizationParameters,
                request.Headers.Authorization?.Parameter ?? string.Empty,
            ];
            return responseFactory(index);
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromException<HttpResponseMessage>(exception);
    }
}

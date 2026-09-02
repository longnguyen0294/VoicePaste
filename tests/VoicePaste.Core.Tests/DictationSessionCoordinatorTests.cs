using Microsoft.VisualStudio.TestTools.UnitTesting;
using VoicePaste.Core;

namespace VoicePaste.Core.Tests;

[TestClass]
public sealed class DictationSessionCoordinatorTests
{
    [TestMethod]
    public async Task UtState001SuccessPathTransitionsAndReturnsToIdle()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();
        var observed = new List<DictationSessionState>();
        coordinator.StateChanged += (_, args) => observed.Add(args.Current);

        var started = await coordinator.StartListeningAsync();
        await coordinator.CompleteAsync();

        Assert.IsTrue(started);
        CollectionAssert.AreEqual(
            new[]
            {
                DictationSessionState.Listening,
                DictationSessionState.Transcribing,
                DictationSessionState.Pasting,
                DictationSessionState.Success,
                DictationSessionState.Idle,
            },
            observed);
        Assert.AreEqual("Xin chào world", fixture.Insertion.LastText);
        Assert.IsTrue(fixture.Audio.Content.Disposed);
    }

    [TestMethod]
    public async Task UtState002RejectsOverlappingSession()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();

        Assert.IsTrue(await coordinator.StartListeningAsync());
        Assert.IsFalse(await coordinator.StartListeningAsync());

        await coordinator.CancelAsync();
        Assert.AreEqual(DictationSessionState.Idle, coordinator.State);
        Assert.AreEqual(1, fixture.Audio.StartCount);
    }

    [TestMethod]
    public async Task UtStt002EmptyTranscriptNeverReachesInsertion()
    {
        var fixture = new CoordinatorFixture
        {
            ProviderResult = OperationResult.Success(
                new TranscriptionOutput("   ", null, TimeSpan.Zero)),
        };
        await using var coordinator = fixture.CreateCoordinator();

        await coordinator.StartListeningAsync();
        await coordinator.CompleteAsync();

        Assert.AreEqual(0, fixture.Insertion.CallCount);
        Assert.AreEqual(DictationSessionState.Idle, coordinator.State);
        Assert.IsTrue(fixture.Status.States.Any(entry =>
            entry.State == DictationSessionState.Error &&
            entry.Error?.Category == ErrorCategory.NoSpeech));
    }

    [TestMethod]
    public async Task UtState001CancelReleasesCaptureAndReturnsToIdle()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();

        await coordinator.StartListeningAsync();
        await coordinator.CancelAsync();

        Assert.AreEqual(1, fixture.Audio.CancelCount);
        Assert.AreEqual(DictationSessionState.Idle, coordinator.State);
        Assert.IsTrue(fixture.Status.States.Any(entry => entry.State == DictationSessionState.Cancelled));
    }

    [TestMethod]
    public async Task UtState005RealtimeStreamingSessionPipesInterimAndPastesFinalTranscript()
    {
        var fakeStreamingSession = new FakeStreamingSession("Realtime transcribed text");
        var fakeStreamingProvider = new FakeStreamingProvider(fakeStreamingSession);
        var fixture = new CoordinatorFixture
        {
            StreamingProvider = fakeStreamingProvider,
            Settings = AppSettings.Default with { DictationMode = DictationMode.Realtime },
        };

        await using var coordinator = fixture.CreateCoordinator();
        var observed = new List<DictationSessionState>();
        var interimList = new List<string>();
        coordinator.StateChanged += (_, args) =>
        {
            if (args.Previous != args.Current)
            {
                observed.Add(args.Current);
            }

            if (args.InterimTranscript is not null)
            {
                interimList.Add(args.InterimTranscript);
            }
        };

        var started = await coordinator.StartListeningAsync();
        Assert.IsTrue(started);

        fixture.Audio.EmitData(new byte[160]);
        fakeStreamingSession.EmitInterim("Xin chào realtime");
        await coordinator.CompleteAsync();

        CollectionAssert.AreEqual(
            new[]
            {
                DictationSessionState.Listening,
                DictationSessionState.Transcribing,
                DictationSessionState.Pasting,
                DictationSessionState.Success,
                DictationSessionState.Idle,
            },
            observed);

        Assert.AreEqual("Realtime transcribed text", fixture.Insertion.LastText);
        Assert.IsTrue(fakeStreamingSession.ChunkCount > 0);
        Assert.IsTrue(interimList.Contains("Xin chào realtime"));
        Assert.IsTrue(fixture.Status.InterimTranscripts.Contains("Xin chào realtime"));
    }

    private sealed class CoordinatorFixture
    {
        public FakeAudioCapture Audio { get; } = new();

        public FakeInsertion Insertion { get; } = new();

        public FakeStatusSink Status { get; } = new();

        public IStreamingSpeechToTextProvider? StreamingProvider { get; init; }

        public AppSettings Settings { get; init; } = AppSettings.Default;

        public OperationResult<TranscriptionOutput> ProviderResult { get; init; } =
            OperationResult.Success(
                new TranscriptionOutput("  Xin chào world  ", "vi", TimeSpan.FromMilliseconds(10)));

        public DictationSessionCoordinator CreateCoordinator() => new(
            new FakeTargetTracker(),
            Audio,
            new FakeProvider(() => ProviderResult),
            Insertion,
            Status,
            Settings,
            StreamingProvider);
    }

    private sealed class FakeTargetTracker : IForegroundWindowTracker
    {
        public Task<OperationResult<TargetWindow>> CaptureAsync(CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Success(new TargetWindow(
                (nint)123,
                42,
                7,
                "test",
                DateTimeOffset.UtcNow,
                0x2000,
                DateTimeOffset.UtcNow)));
    }

    private sealed class FakeAudioCapture : IAudioCaptureService
    {
        public event EventHandler<AudioChunkAvailableEventArgs>? DataAvailable;

        public void EmitData(byte[] bytes) =>
            DataAvailable?.Invoke(this, new AudioChunkAvailableEventArgs(bytes));

        public FakeAudioContent Content { get; } = new(TimeSpan.FromSeconds(1));

        public int StartCount { get; private set; }

        public int CancelCount { get; private set; }

        public Task<OperationResult<Unit>> StartAsync(
            string microphoneId,
            CancellationToken cancellationToken)
        {
            StartCount++;
            return Task.FromResult(OperationResult.Success(Unit.Value));
        }

        public Task<OperationResult<AudioRecording>> StopAsync(CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Success(new AudioRecording(
                "test-recording",
                Content,
                DateTimeOffset.UtcNow,
                null)));

        public Task CancelAsync(CancellationToken cancellationToken)
        {
            CancelCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeAudioContent(TimeSpan duration) : IAudioContent
    {
        public bool Disposed { get; private set; }

        public AudioFormat Format { get; } = new(16_000, 1, 16, "pcm");

        public TimeSpan Duration { get; } = duration;

        public long? LengthBytes => 32_000;

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<Stream>(new MemoryStream(new byte[32_000], writable: false));

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeProvider(Func<OperationResult<TranscriptionOutput>> resultFactory)
        : ISpeechToTextProvider
    {
        public string ProviderId => "fake";

        public SpeechProviderCapabilities Capabilities { get; } = new(
            new HashSet<LanguageMode>(Enum.GetValues<LanguageMode>()),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "vi-VN", "en-US" },
            null,
            null,
            SupportsStreaming: false,
            SendsAudioOffDevice: false);

        public Task<OperationResult<TranscriptionOutput>> TranscribeAsync(
            AudioRecording audio,
            TranscriptionOptions options,
            CancellationToken cancellationToken) => Task.FromResult(resultFactory());
    }

    private sealed class FakeStreamingSession(string finalText) : IStreamingSpeechToTextSession
    {
        public event EventHandler<string>? InterimTranscriptReceived;

        public int ChunkCount { get; private set; }

        public void EmitInterim(string text) => InterimTranscriptReceived?.Invoke(this, text);

        public Task SendAudioChunkAsync(ReadOnlyMemory<byte> pcmChunk, CancellationToken cancellationToken)
        {
            ChunkCount++;
            return Task.CompletedTask;
        }

        public Task<OperationResult<TranscriptionOutput>> CompleteAsync(CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Success(new TranscriptionOutput(finalText, "vi", TimeSpan.FromMilliseconds(5))));

        public Task CancelAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeStreamingProvider(IStreamingSpeechToTextSession session) : IStreamingSpeechToTextProvider
    {
        public string ProviderId => "fake-streaming";

        public SpeechProviderCapabilities Capabilities { get; } = new(
            new HashSet<LanguageMode>(Enum.GetValues<LanguageMode>()),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "vi-VN", "en-US" },
            null,
            null,
            SupportsStreaming: true,
            SendsAudioOffDevice: true);

        public Task<OperationResult<IStreamingSpeechToTextSession>> StartSessionAsync(
            TranscriptionOptions options,
            CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Success(session));
    }

    private sealed class FakeInsertion : ITextInsertionService
    {
        public int CallCount { get; private set; }

        public string? LastText { get; private set; }

        public Task<OperationResult<InsertionOutcome>> InsertAsync(
            string text,
            TargetWindow target,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastText = text;
            return Task.FromResult(OperationResult.Success(
                new InsertionOutcome(WasAutomaticallyPasted: true, IsAvailableForManualCopy: false)));
        }
    }

    private sealed class FakeStatusSink : IStatusSink
    {
        public List<(DictationSessionState State, OperationError? Error)> States { get; } = [];

        public List<string> InterimTranscripts { get; } = [];

        public ValueTask PublishAsync(
            DictationSessionState state,
            OperationError? error,
            CancellationToken cancellationToken)
        {
            States.Add((state, error));
            return ValueTask.CompletedTask;
        }

        public ValueTask PublishInterimTranscriptAsync(
            string interimTranscript,
            CancellationToken cancellationToken)
        {
            InterimTranscripts.Add(interimTranscript);
            return ValueTask.CompletedTask;
        }
    }
}

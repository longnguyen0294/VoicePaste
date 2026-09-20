using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VoicePaste.Core;

namespace VoicePaste.Providers.OpenAI;

public sealed record OpenAiSpeechProviderOptions(
    Uri Endpoint,
    string Model,
    string CredentialReference,
    TimeSpan RequestTimeout,
    long MaximumPcmBytesPerRequest)
{
    public const long OpenAiMaximumFileBytes = 25L * 1024 * 1024;
    public const long DefaultMaximumPcmBytesPerRequest = 24L * 1024 * 1024;
    public const string DefaultCredentialReference = "openai-api-key";
    public const string DefaultModel = "gpt-transcribe";

    public static OpenAiSpeechProviderOptions Default { get; } = new(
        new Uri("https://api.openai.com/v1/audio/transcriptions", UriKind.Absolute),
        DefaultModel,
        DefaultCredentialReference,
        TimeSpan.FromMinutes(5),
        DefaultMaximumPcmBytesPerRequest);
}

public sealed class OpenAiSpeechToTextProvider : ISpeechToTextProvider, IDisposable
{
    private const int MaximumContextCharacters = 500;
    private readonly HttpClient _httpClient;
    private readonly ICredentialStore _credentialStore;
    private readonly OpenAiSpeechProviderOptions _options;
    private readonly bool _ownsHttpClient;
    private bool _disposed;

    public OpenAiSpeechToTextProvider(
        HttpClient httpClient,
        ICredentialStore credentialStore,
        OpenAiSpeechProviderOptions? options = null,
        bool ownsHttpClient = false)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _options = options ?? OpenAiSpeechProviderOptions.Default;
        _ownsHttpClient = ownsHttpClient;
        ValidateOptions(_options);
    }

    public string ProviderId => "openai-gpt-transcribe";

    public SpeechProviderCapabilities Capabilities { get; } = new(
        new HashSet<LanguageMode>(Enum.GetValues<LanguageMode>()),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "vi", "en", "vi-VN", "en-US" },
        MaximumRequestDuration: null,
        MaximumRequestBytes: OpenAiSpeechProviderOptions.OpenAiMaximumFileBytes,
        SupportsStreaming: false,
        SendsAudioOffDevice: true);

    public async Task<OperationResult<TranscriptionOutput>> TranscribeAsync(
        AudioRecording audio,
        TranscriptionOptions options,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(options);

        var formatError = ValidateAudioFormat(audio.Content.Format);
        if (formatError is not null)
        {
            return OperationResult.Failure<TranscriptionOutput>(formatError);
        }

        var length = audio.Content.LengthBytes;
        if (length is null or <= 0)
        {
            return OperationResult.Failure<TranscriptionOutput>(new OperationError(
                ErrorCategory.NoSpeech,
                "transcription.no_audio",
                IsRetryable: false,
                "audio_length_missing_or_zero"));
        }

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
            return OperationResult.Failure<TranscriptionOutput>(new OperationError(
                ErrorCategory.AuthenticationFailed,
                "provider.credential_unavailable",
                IsRetryable: true,
                exception.GetType().Name));
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return OperationResult.Failure<TranscriptionOutput>(new OperationError(
                ErrorCategory.AuthenticationFailed,
                "provider.api_key_required",
                IsRetryable: false,
                "openai_credential_missing"));
        }

        var format = audio.Content.Format;
        var blockAlign = checked(format.Channels * format.BitsPerSample / 8);
        if (length.Value % blockAlign != 0)
        {
            return OperationResult.Failure<TranscriptionOutput>(new OperationError(
                ErrorCategory.AudioEncodingFailed,
                "audio.invalid_trailing_frame",
                IsRetryable: false,
                "pcm_length_not_block_aligned"));
        }

        var maximumSegmentBytes = _options.MaximumPcmBytesPerRequest -
                                  (_options.MaximumPcmBytesPerRequest % blockAlign);
        if (maximumSegmentBytes <= 0)
        {
            return OperationResult.Failure<TranscriptionOutput>(new OperationError(
                ErrorCategory.AudioEncodingFailed,
                "audio.invalid_segment_size",
                IsRetryable: false,
                "segment_below_block_alignment"));
        }

        var stopwatch = Stopwatch.StartNew();
        var transcripts = new List<string>();
        var languages = GetLanguageHints(options);
        await using var audioStream = await audio.Content
            .OpenReadAsync(cancellationToken)
            .ConfigureAwait(false);

        var remaining = length.Value;
        var segmentIndex = 0;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segmentBytes = Math.Min(remaining, maximumSegmentBytes);
            segmentBytes -= segmentBytes % blockAlign;
            var prompt = BuildContinuationPrompt(transcripts);
            var segmentResult = await TranscribeSegmentAsync(
                    apiKey,
                    audioStream,
                    segmentBytes,
                    format,
                    languages,
                    prompt,
                    segmentIndex,
                    cancellationToken)
                .ConfigureAwait(false);
            if (segmentResult is OperationFailure<ProviderSegmentResult> failure)
            {
                return OperationResult.Failure<TranscriptionOutput>(failure.Error);
            }

            var segment = ((OperationSuccess<ProviderSegmentResult>)segmentResult).Value;
            if (!string.IsNullOrWhiteSpace(segment.Text))
            {
                transcripts.Add(segment.Text.Trim());
            }

            remaining -= segmentBytes;
            segmentIndex++;
        }

        stopwatch.Stop();
        var combined = string.Join(' ', transcripts);
        if (string.IsNullOrWhiteSpace(combined))
        {
            return OperationResult.Failure<TranscriptionOutput>(new OperationError(
                ErrorCategory.NoSpeech,
                "transcription.no_speech",
                IsRetryable: false,
                "provider_returned_empty_text"));
        }

        return OperationResult.Success(new TranscriptionOutput(
            combined,
            DetectedLanguage: null,
            stopwatch.Elapsed));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }

        _disposed = true;
    }

    private async Task<OperationResult<ProviderSegmentResult>> TranscribeSegmentAsync(
        string apiKey,
        Stream source,
        long pcmLength,
        AudioFormat format,
        IReadOnlyList<string> languages,
        string? prompt,
        int segmentIndex,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.UserAgent.ParseAdd("VoicePaste/1.0");
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(_options.Model, Encoding.UTF8), "model");
        form.Add(new StringContent("json", Encoding.UTF8), "response_format");
        foreach (var language in languages)
        {
            form.Add(new StringContent(language, Encoding.UTF8), "languages[]");
        }

        if (!string.IsNullOrWhiteSpace(prompt))
        {
            form.Add(new StringContent(prompt, Encoding.UTF8), "prompt");
        }

        using var wavContent = new WavPcmHttpContent(source, pcmLength, format);
        form.Add(wavContent, "file", $"recording-{segmentIndex:D4}.wav");
        request.Content = form;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        try
        {
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var providerCode = await ReadProviderErrorCodeAsync(response, timeout.Token)
                    .ConfigureAwait(false);
                return OperationResult.Failure<ProviderSegmentResult>(
                    MapHttpError(response.StatusCode, providerCode));
            }

            await using var responseStream = await response.Content
                .ReadAsStreamAsync(timeout.Token)
                .ConfigureAwait(false);
            using var document = await JsonDocument
                .ParseAsync(responseStream, cancellationToken: timeout.Token)
                .ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("text", out var textElement) ||
                textElement.ValueKind != JsonValueKind.String)
            {
                return OperationResult.Failure<ProviderSegmentResult>(new OperationError(
                    ErrorCategory.ProviderFailure,
                    "provider.invalid_response",
                    IsRetryable: true,
                    "openai_response_missing_text"));
            }

            return OperationResult.Success(new ProviderSegmentResult(textElement.GetString() ?? string.Empty));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return OperationResult.Failure<ProviderSegmentResult>(new OperationError(
                ErrorCategory.TimedOut,
                "provider.timed_out",
                IsRetryable: true,
                "openai_request_timeout"));
        }
        catch (HttpRequestException exception)
        {
            if (exception.StatusCode is HttpStatusCode statusCode)
            {
                return OperationResult.Failure<ProviderSegmentResult>(MapHttpError(statusCode, null));
            }

            DiagnosticLog.LogException("standard.request_failed", exception);
            return OperationResult.Failure<ProviderSegmentResult>(new OperationError(
                ErrorCategory.NetworkUnavailable,
                "provider.network_unavailable",
                IsRetryable: true,
                exception.GetType().Name));
        }
        catch (IOException exception)
        {
            return OperationResult.Failure<ProviderSegmentResult>(new OperationError(
                ErrorCategory.AudioEncodingFailed,
                "audio.read_failed",
                IsRetryable: true,
                exception.GetType().Name));
        }
        catch (JsonException exception)
        {
            return OperationResult.Failure<ProviderSegmentResult>(new OperationError(
                ErrorCategory.ProviderFailure,
                "provider.invalid_response",
                IsRetryable: true,
                exception.GetType().Name));
        }
    }

    private static string[] GetLanguageHints(TranscriptionOptions options)
    {
        if (options.LanguageMode == LanguageMode.Automatic)
        {
            return [];
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
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return languages.Length == 0 ? ["vi", "en"] : languages;
    }

    private static string? BuildContinuationPrompt(List<string> transcripts)
    {
        if (transcripts.Count == 0)
        {
            return null;
        }

        var text = transcripts[^1];
        var tail = text.Length <= MaximumContextCharacters
            ? text
            : text[^MaximumContextCharacters..];
        return $"Previous transcript context: {tail}";
    }

    private static OperationError? ValidateAudioFormat(AudioFormat format)
    {
        if (!string.Equals(format.Encoding, "pcm", StringComparison.OrdinalIgnoreCase) ||
            format.BitsPerSample != 16 ||
            format.SampleRate <= 0 ||
            format.Channels <= 0 ||
            format.Channels > ushort.MaxValue)
        {
            return new OperationError(
                ErrorCategory.AudioEncodingFailed,
                "audio.unsupported_provider_format",
                IsRetryable: false,
                $"expected_pcm16_actual_{format.Encoding}_{format.BitsPerSample}");
        }

        return null;
    }

    private static void ValidateOptions(OpenAiSpeechProviderOptions options)
    {
        if (!options.Endpoint.IsAbsoluteUri ||
            string.IsNullOrWhiteSpace(options.Model) ||
            string.IsNullOrWhiteSpace(options.CredentialReference) ||
            options.RequestTimeout <= TimeSpan.Zero ||
            options.MaximumPcmBytesPerRequest <= 0 ||
            options.MaximumPcmBytesPerRequest + WavPcmHttpContent.HeaderLength >
            OpenAiSpeechProviderOptions.OpenAiMaximumFileBytes)
        {
            throw new ArgumentException("OpenAI speech provider options are invalid.", nameof(options));
        }
    }

    private static async Task<string?> ReadProviderErrorCodeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var document = await JsonDocument
                .ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (document.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("code", out var code) &&
                code.ValueKind == JsonValueKind.String)
            {
                return code.GetString();
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
        }

        return null;
    }

    private static OperationError MapHttpError(HttpStatusCode statusCode, string? providerCode)
    {
        var numericStatus = (int)statusCode;
        return statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new OperationError(
                ErrorCategory.AuthenticationFailed,
                "provider.authentication_failed",
                IsRetryable: false,
                $"openai_http_{numericStatus}"),
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => new OperationError(
                ErrorCategory.TimedOut,
                "provider.timed_out",
                IsRetryable: true,
                $"openai_http_{numericStatus}"),
            HttpStatusCode.RequestEntityTooLarge => new OperationError(
                ErrorCategory.ProviderPayloadTooLarge,
                "provider.payload_too_large",
                IsRetryable: true,
                $"openai_http_{numericStatus}"),
            (HttpStatusCode)429 => new OperationError(
                ErrorCategory.QuotaExceeded,
                "provider.quota_or_rate_limit",
                IsRetryable: !string.Equals(providerCode, "insufficient_quota", StringComparison.OrdinalIgnoreCase),
                string.IsNullOrWhiteSpace(providerCode)
                    ? "openai_http_429"
                    : $"openai_{providerCode}"),
            _ when numericStatus >= 500 => new OperationError(
                ErrorCategory.ProviderFailure,
                "provider.unavailable",
                IsRetryable: true,
                $"openai_http_{numericStatus}"),
            _ => new OperationError(
                ErrorCategory.ProviderFailure,
                "provider.request_rejected",
                IsRetryable: false,
                string.IsNullOrWhiteSpace(providerCode)
                    ? $"openai_http_{numericStatus}"
                    : $"openai_{providerCode}"),
        };
    }

    private sealed record ProviderSegmentResult(string Text);
}

internal sealed class WavPcmHttpContent : HttpContent
{
    public const int HeaderLength = 44;
    private readonly Stream _source;
    private readonly long _pcmLength;
    private readonly AudioFormat _format;

    public WavPcmHttpContent(Stream source, long pcmLength, AudioFormat format)
    {
        _source = source;
        _pcmLength = pcmLength;
        _format = format;
        Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context,
        CancellationToken cancellationToken)
    {
        var header = CreateHeader(_format, _pcmLength);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);

        var remaining = _pcmLength;
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (remaining > 0)
            {
                var read = await _source
                    .ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException("PCM stream ended before the declared segment length.");
                }

                await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = checked(HeaderLength + _pcmLength);
        return true;
    }

    private static byte[] CreateHeader(AudioFormat format, long pcmLength)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pcmLength, (long)uint.MaxValue);

        var header = new byte[HeaderLength];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), checked((uint)(36 + pcmLength)));
        "WAVE"u8.CopyTo(header.AsSpan(8));
        "fmt "u8.CopyTo(header.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22), checked((ushort)format.Channels));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), checked((uint)format.SampleRate));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), checked((uint)format.BytesPerSecond));
        BinaryPrimitives.WriteUInt16LittleEndian(
            header.AsSpan(32),
            checked((ushort)(format.Channels * format.BitsPerSample / 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34), checked((ushort)format.BitsPerSample));
        "data"u8.CopyTo(header.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), checked((uint)pcmLength));
        return header;
    }
}

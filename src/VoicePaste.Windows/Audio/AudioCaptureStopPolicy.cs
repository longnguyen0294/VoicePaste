namespace VoicePaste.Windows.Audio;

internal static class AudioCaptureStopPolicy
{
    public static async Task<bool> CompletesWithinAsync(
        Task task,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            await task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}

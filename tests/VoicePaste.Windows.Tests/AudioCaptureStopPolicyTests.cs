using Microsoft.VisualStudio.TestTools.UnitTesting;
using VoicePaste.Windows.Audio;

namespace VoicePaste.Windows.Tests;

[TestClass]
public sealed class AudioCaptureStopPolicyTests
{
    [TestMethod]
    public async Task UtAudio004MissingStopCallbackTimesOut()
    {
        var pendingStop = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var completed = await AudioCaptureStopPolicy.CompletesWithinAsync(
            pendingStop.Task,
            TimeSpan.FromMilliseconds(20),
            CancellationToken.None);

        Assert.IsFalse(completed);
    }

    [TestMethod]
    public async Task UtAudio004StopCallbackCompletesNormally()
    {
        var completed = await AudioCaptureStopPolicy.CompletesWithinAsync(
            Task.CompletedTask,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.IsTrue(completed);
    }

    [TestMethod]
    public async Task UtAudio004CancellationIsNotMisreportedAsTimeout()
    {
        var pendingStop = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() =>
            AudioCaptureStopPolicy.CompletesWithinAsync(
                pendingStop.Task,
                TimeSpan.FromSeconds(1),
                cancellation.Token));
    }
}

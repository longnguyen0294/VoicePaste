using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VoicePaste.Core;
using VoicePaste.Windows.Audio;

namespace VoicePaste.Windows.Tests;

[TestClass]
public sealed class ChunkedAudioStorageTests
{
    [TestMethod]
    public async Task UtAudio003SplitsWithoutReorderingOrExceedingChunkLimit()
    {
        var root = CreateTestRoot();
        var recordingDirectory = Path.Combine(root, "recording");
        var expected = Enumerable.Range(0, 25).Select(value => (byte)value).ToArray();
        try
        {
            await using var sink = new ChunkedPcmSink(
                recordingDirectory,
                new AudioFormat(10, 1, 8, "pcm"),
                new FixedFreeSpaceProbe(long.MaxValue),
                reserveBytes: 0,
                maximumChunkBytes: 10);

            Assert.IsTrue(sink.TryWrite(expected));
            sink.Complete();
            await sink.Completion;

            Assert.IsNull(sink.Failure);
            Assert.AreEqual(3, sink.ChunkPaths.Count);
            Assert.IsTrue(sink.ChunkPaths.All(path => new FileInfo(path).Length <= 10));
            var content = new ChunkedAudioContent(
                recordingDirectory,
                sink.ChunkPaths,
                new AudioFormat(10, 1, 8, "pcm"),
                TimeSpan.FromSeconds(2.5),
                expected.Length);
            await using (var stream = await content.OpenReadAsync(CancellationToken.None))
            {
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory);
                CollectionAssert.AreEqual(expected, memory.ToArray());
            }

            await content.DisposeAsync();
            Assert.IsFalse(Directory.Exists(recordingDirectory));
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [TestMethod]
    public async Task UtAudio003ReturnsStorageFullBelowReserve()
    {
        var root = CreateTestRoot();
        try
        {
            await using var sink = new ChunkedPcmSink(
                Path.Combine(root, "recording"),
                new AudioFormat(16_000, 1, 16, "pcm"),
                new FixedFreeSpaceProbe(0),
                reserveBytes: 1,
                maximumChunkBytes: 32_000);

            Assert.IsTrue(sink.TryWrite(new byte[128]));
            sink.Complete();
            await sink.Completion;

            Assert.IsNotNull(sink.Failure);
            Assert.AreEqual(ErrorCategory.StorageFull, sink.Failure.Category);
            Assert.AreEqual(0, sink.BytesWritten);
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    private static string CreateTestRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "VoicePaste.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTestRoot(string root)
    {
        var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VoicePaste.Tests")) +
                             Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(root);
        if (resolved.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved))
        {
            Directory.Delete(resolved, recursive: true);
        }
    }

    private sealed class FixedFreeSpaceProbe(long availableBytes) : IFreeSpaceProbe
    {
        public long GetAvailableBytes(string path) => availableBytes;
    }
}


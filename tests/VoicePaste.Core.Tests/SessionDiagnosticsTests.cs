using Microsoft.VisualStudio.TestTools.UnitTesting;
using VoicePaste.Core;

namespace VoicePaste.Core.Tests;

[TestClass]
public sealed class SessionDiagnosticsTests
{
    [TestMethod]
    public void UtPriv002ExceptionDiagnosticsKeepFramesAndExcludeSensitiveContent()
    {
        const string secret = "sk-secret-transcript-audio-path";
        Exception captured;
        try
        {
            throw new InvalidOperationException(secret, new IOException(secret));
        }
        catch (InvalidOperationException exception)
        {
            exception.Data["credential"] = secret;
            captured = exception;
        }

        var line = SessionDiagnostics.FormatLine("session.complete_exception", "Stage=audio.stop",
            new OperationError(ErrorCategory.SessionInterrupted, "session.unexpected_error", true, secret),
            captured);

        StringAssert.Contains(line, "Stage=audio.stop");
        StringAssert.Contains(line, "System.InvalidOperationException");
        StringAssert.Contains(line, "System.IO.IOException");
        StringAssert.Contains(line, nameof(UtPriv002ExceptionDiagnosticsKeepFramesAndExcludeSensitiveContent));
        StringAssert.Contains(line, "HResult=");
        Assert.IsFalse(line.Contains(secret, StringComparison.Ordinal));
        Assert.IsFalse(line.Contains(Path.GetFullPath("."), StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task UtDiag001SessionCorrelationFlowsToTasksAndRestoresAfterScope()
    {
        var sessionId = Guid.NewGuid();
        using (SessionDiagnostics.BeginSession(sessionId))
        {
            var line = await Task.Run(() => SessionDiagnostics.FormatLine("realtime.send", "", null, null));
            StringAssert.Contains(line, $"Session={sessionId:N}");
            using (SessionDiagnostics.BeginSession(Guid.NewGuid()))
            {
                Assert.IsFalse(SessionDiagnostics.FormatLine("nested", "", null, null).Contains(sessionId.ToString("N"), StringComparison.Ordinal));
            }

            StringAssert.Contains(SessionDiagnostics.FormatLine("restored", "", null, null), $"Session={sessionId:N}");
        }

        StringAssert.Contains(SessionDiagnostics.FormatLine("outside", "", null, null), "Session=none");
    }

    [TestMethod]
    public void UtDiag002LogRotationKeepsOnlyCurrentAndPreviousFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "VoicePaste-log-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "application.log");
        try
        {
            var writer = new DiagnosticFileWriter(path, maximumBytes: 10);
            writer.Append("first-record-is-long");
            writer.Append("second-record-is-long");
            writer.Append("third-record");

            StringAssert.Contains(File.ReadAllText(path), "third-record");
            var previous = File.ReadAllText(Path.ChangeExtension(path, ".previous.log"));
            StringAssert.Contains(previous, "second-record-is-long");
            Assert.IsFalse(previous.Contains("first-record", StringComparison.Ordinal));
            Assert.AreEqual(2, Directory.GetFiles(directory).Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

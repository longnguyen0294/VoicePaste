using System.Globalization;

namespace VoicePaste.Providers.OpenAI;

internal static class DiagnosticLog
{
    private const long MaximumLogBytes = 1024 * 1024;
    private static readonly string LogFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VoicePaste",
        "logs",
        "provider.log");

    private static readonly object WriteLock = new();

    public static void LogException(string context, Exception exception) =>
        LogMessage(context, FormatExceptionMetadata(exception));

    internal static string FormatExceptionMetadata(Exception exception) =>
        $"ExceptionType={exception.GetType().Name} " +
        $"HResult={exception.HResult.ToString(CultureInfo.InvariantCulture)}";

    public static void LogMessage(string context, string message)
    {
        try
        {
            var directory = Path.GetDirectoryName(LogFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var line = string.Format(
                CultureInfo.InvariantCulture,
                "[{0:O}] {1}: {2}{3}",
                DateTimeOffset.Now,
                context,
                message,
                Environment.NewLine);

            lock (WriteLock)
            {
                RotateIfNeeded();
                File.AppendAllText(LogFilePath, line);
            }
        }
        catch
        {
            // Diagnostics must never break the calling operation.
        }
    }

    private static void RotateIfNeeded()
    {
        if (!File.Exists(LogFilePath) || new FileInfo(LogFilePath).Length < MaximumLogBytes)
        {
            return;
        }

        var archivedPath = Path.ChangeExtension(LogFilePath, ".previous.log");
        File.Move(LogFilePath, archivedPath, overwrite: true);
    }
}

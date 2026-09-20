using System.Globalization;

namespace VoicePaste.Providers.OpenAI;

internal static class DiagnosticLog
{
    private static readonly string LogFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VoicePaste",
        "logs",
        "provider.log");

    private static readonly object WriteLock = new();

    public static void LogException(string context, Exception exception) =>
        LogMessage(context, exception.ToString());

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
                File.AppendAllText(LogFilePath, line);
            }
        }
        catch
        {
            // Diagnostics must never break the calling operation.
        }
    }
}

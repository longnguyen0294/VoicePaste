using VoicePaste.Core;

namespace VoicePaste.Providers.OpenAI;

internal static class DiagnosticLog
{
    public static void LogException(string context, Exception exception) =>
        SessionDiagnostics.Write(context, exception: exception);

    internal static string FormatExceptionMetadata(Exception exception) =>
        SessionDiagnostics.FormatExceptionMetadata(exception);

    public static void LogMessage(string context, string message) =>
        SessionDiagnostics.Write(context, message);
}

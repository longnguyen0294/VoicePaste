using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using VoicePaste.Core;

namespace VoicePaste.Windows.Input;

public interface ITargetWindowValidator
{
    OperationResult<Unit> ValidateForPaste(TargetWindow target);
}

public sealed partial class WindowsForegroundWindowTracker : IForegroundWindowTracker, ITargetWindowValidator
{
    public Task<OperationResult<TargetWindow>> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var handle = NativeMethods.GetForegroundWindow();
        if (handle == nint.Zero)
        {
            return Task.FromResult(TargetUnavailable("no_foreground_window"));
        }

        var threadId = NativeMethods.GetWindowThreadProcessId(handle, out var processId);
        if (processId == 0)
        {
            return Task.FromResult(TargetUnavailable("foreground_process_unavailable"));
        }

        if (processId == (uint)Environment.ProcessId)
        {
            return Task.FromResult(OperationResult.Failure<TargetWindow>(new OperationError(
                ErrorCategory.TargetUnavailable,
                "target.voicepaste_window",
                IsRetryable: true,
                "foreground_is_voicepaste")));
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            var target = new TargetWindow(
                handle,
                processId,
                threadId,
                process.ProcessName,
                new DateTimeOffset(process.StartTime.ToUniversalTime()),
                IntegrityLevelReader.Read(process.Handle),
                DateTimeOffset.UtcNow);
            return Task.FromResult(OperationResult.Success(target));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return Task.FromResult(TargetUnavailable(exception.GetType().Name));
        }
    }

    public OperationResult<Unit> ValidateForPaste(TargetWindow target)
    {
        var currentForeground = NativeMethods.GetForegroundWindow();
        if (currentForeground != target.Handle || currentForeground == nint.Zero)
        {
            return TargetValidationFailure(
                ErrorCategory.TargetUnavailable,
                "target.not_foreground",
                "foreground_changed");
        }

        var threadId = NativeMethods.GetWindowThreadProcessId(currentForeground, out var processId);
        if (processId != target.ProcessId)
        {
            return TargetValidationFailure(
                ErrorCategory.TargetUnavailable,
                "target.changed",
                "process_id_changed");
        }

        if (threadId != target.ThreadId)
        {
            return TargetValidationFailure(
                ErrorCategory.TargetUnavailable,
                "target.changed",
                "window_thread_changed");
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            var startedAt = new DateTimeOffset(process.StartTime.ToUniversalTime());
            if (target.ProcessStartedAt is not null && startedAt != target.ProcessStartedAt)
            {
                return TargetValidationFailure(
                    ErrorCategory.TargetUnavailable,
                    "target.changed",
                    "process_start_changed");
            }

            using var currentProcess = Process.GetCurrentProcess();
            var currentIntegrity = IntegrityLevelReader.Read(currentProcess.Handle);
            var targetIntegrity = IntegrityLevelReader.Read(process.Handle);
            if (targetIntegrity > 0 && currentIntegrity > 0 && targetIntegrity > currentIntegrity)
            {
                return TargetValidationFailure(
                    ErrorCategory.TargetPrivilegeMismatch,
                    "target.elevated",
                    "integrity_level_mismatch");
            }

            return OperationResult.Success(Unit.Value);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return TargetValidationFailure(
                ErrorCategory.TargetUnavailable,
                "target.unavailable",
                exception.GetType().Name);
        }
    }

    private static OperationResult<TargetWindow> TargetUnavailable(string diagnosticCode) =>
        OperationResult.Failure<TargetWindow>(new OperationError(
            ErrorCategory.TargetUnavailable,
            "target.unavailable",
            IsRetryable: true,
            diagnosticCode));

    private static OperationResult<Unit> TargetValidationFailure(
        ErrorCategory category,
        string messageKey,
        string diagnosticCode) =>
        OperationResult.Failure<Unit>(new OperationError(
            category,
            messageKey,
            IsRetryable: true,
            diagnosticCode));

    private static partial class NativeMethods
    {
        [LibraryImport("user32.dll")]
        public static partial nint GetForegroundWindow();

        [LibraryImport("user32.dll", SetLastError = true)]
        public static partial uint GetWindowThreadProcessId(nint window, out uint processId);
    }
}

internal static partial class IntegrityLevelReader
{
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;

    public static int Read(nint processHandle)
    {
        if (!NativeMethods.OpenProcessToken(processHandle, TokenQuery, out var tokenHandle))
        {
            return 0;
        }

        try
        {
            NativeMethods.GetTokenInformation(
                tokenHandle,
                TokenIntegrityLevel,
                nint.Zero,
                0,
                out var requiredLength);
            if (requiredLength == 0)
            {
                return 0;
            }

            var buffer = Marshal.AllocHGlobal((int)requiredLength);
            try
            {
                if (!NativeMethods.GetTokenInformation(
                        tokenHandle,
                        TokenIntegrityLevel,
                        buffer,
                        requiredLength,
                        out _))
                {
                    return 0;
                }

                var label = Marshal.PtrToStructure<TokenMandatoryLabel>(buffer);
                var countPointer = NativeMethods.GetSidSubAuthorityCount(label.Label.Sid);
                if (countPointer == nint.Zero)
                {
                    return 0;
                }

                var count = Marshal.ReadByte(countPointer);
                if (count == 0)
                {
                    return 0;
                }

                var authorityPointer = NativeMethods.GetSidSubAuthority(label.Label.Sid, (uint)(count - 1));
                return authorityPointer == nint.Zero ? 0 : Marshal.ReadInt32(authorityPointer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(tokenHandle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public nint Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenMandatoryLabel
    {
        public SidAndAttributes Label;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool OpenProcessToken(
            nint processHandle,
            uint desiredAccess,
            out nint tokenHandle);

        [LibraryImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetTokenInformation(
            nint tokenHandle,
            int tokenInformationClass,
            nint tokenInformation,
            uint tokenInformationLength,
            out uint returnLength);

        [LibraryImport("advapi32.dll")]
        public static partial nint GetSidSubAuthorityCount(nint sid);

        [LibraryImport("advapi32.dll")]
        public static partial nint GetSidSubAuthority(nint sid, uint subAuthority);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CloseHandle(nint handle);
    }
}

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using VoicePaste.Core;

namespace VoicePaste.Windows.Infrastructure;

public sealed partial class WindowsCredentialStore : ICredentialStore
{
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaximumCredentialBlobBytes = 5 * 512;
    private const string TargetPrefix = "VoicePaste:";

    public Task SaveAsync(string reference, string secret, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateReference(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        var secretBytes = Encoding.Unicode.GetBytes(secret);
        if (secretBytes.Length > MaximumCredentialBlobBytes)
        {
            CryptographicOperations.ZeroMemory(secretBytes);
            throw new ArgumentException("Credential exceeds the Windows credential blob limit.", nameof(secret));
        }

        nint secretPointer = nint.Zero;
        nint targetPointer = nint.Zero;
        nint userPointer = nint.Zero;
        nint credentialPointer = nint.Zero;
        try
        {
            secretPointer = Marshal.AllocCoTaskMem(secretBytes.Length);
            targetPointer = Marshal.StringToCoTaskMemUni(TargetPrefix + reference);
            userPointer = Marshal.StringToCoTaskMemUni(Environment.UserName);
            credentialPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<NativeCredential>());
            Marshal.Copy(secretBytes, 0, secretPointer, secretBytes.Length);
            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = targetPointer,
                CredentialBlobSize = (uint)secretBytes.Length,
                CredentialBlob = secretPointer,
                Persist = CredentialPersistLocalMachine,
                UserName = userPointer,
            };

            Marshal.StructureToPtr(credential, credentialPointer, fDeleteOld: false);
            if (!NativeMethods.CredWrite(credentialPointer, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to store provider credential.");
            }

            return Task.CompletedTask;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
            FreeIfAllocated(secretPointer);
            FreeIfAllocated(targetPointer);
            FreeIfAllocated(userPointer);
            FreeIfAllocated(credentialPointer);
        }
    }

    public Task<string?> ReadAsync(string reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateReference(reference);
        if (!NativeMethods.CredRead(
                TargetPrefix + reference,
                CredentialTypeGeneric,
                0,
                out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return Task.FromResult<string?>(null);
            }

            throw new Win32Exception(error, "Unable to read provider credential.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == nint.Zero || credential.CredentialBlobSize == 0)
            {
                return Task.FromResult<string?>(string.Empty);
            }

            var secret = Marshal.PtrToStringUni(
                credential.CredentialBlob,
                checked((int)credential.CredentialBlobSize / sizeof(char)));
            return Task.FromResult<string?>(secret);
        }
        finally
        {
            NativeMethods.CredFree(credentialPointer);
        }
    }

    public Task<IReadOnlyList<StoredCredentialDescriptor>> ListAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!NativeMethods.CredEnumerate(
                TargetPrefix + "*",
                0,
                out var credentialCount,
                out var credentialsPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return Task.FromResult<IReadOnlyList<StoredCredentialDescriptor>>([]);
            }

            throw new Win32Exception(error, "Unable to enumerate provider credentials.");
        }

        try
        {
            var result = new List<StoredCredentialDescriptor>(checked((int)credentialCount));
            for (var index = 0u; index < credentialCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var itemPointer = Marshal.ReadIntPtr(
                    credentialsPointer,
                    checked((int)index * IntPtr.Size));
                if (itemPointer == nint.Zero)
                {
                    continue;
                }

                var credential = Marshal.PtrToStructure<NativeCredential>(itemPointer);
                if (credential.Type != CredentialTypeGeneric)
                {
                    continue;
                }

                var targetName = Marshal.PtrToStringUni(credential.TargetName);
                if (string.IsNullOrEmpty(targetName) ||
                    !targetName.StartsWith(TargetPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var reference = targetName[TargetPrefix.Length..];
                if (!IsValidReference(reference))
                {
                    continue;
                }

                result.Add(new StoredCredentialDescriptor(
                    reference,
                    ReadLastWritten(credential.LastWritten)));
            }

            return Task.FromResult<IReadOnlyList<StoredCredentialDescriptor>>(
                result
                    .OrderBy(item => item.Reference, StringComparer.OrdinalIgnoreCase)
                    .ToArray());
        }
        finally
        {
            NativeMethods.CredFree(credentialsPointer);
        }
    }

    public Task DeleteAsync(string reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateReference(reference);
        if (!NativeMethods.CredDelete(TargetPrefix + reference, CredentialTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw new Win32Exception(error, "Unable to delete provider credential.");
            }
        }

        return Task.CompletedTask;
    }

    private static void ValidateReference(string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        if (!IsValidReference(reference))
        {
            throw new ArgumentException("Credential reference is invalid.", nameof(reference));
        }
    }

    private static bool IsValidReference(string reference) =>
        !string.IsNullOrWhiteSpace(reference) &&
        reference.Length <= 128 &&
        !reference.Any(char.IsControl);

    private static DateTimeOffset? ReadLastWritten(
        System.Runtime.InteropServices.ComTypes.FILETIME lastWritten)
    {
        var fileTime = unchecked(
            ((long)(uint)lastWritten.dwHighDateTime << 32) |
            (uint)lastWritten.dwLowDateTime);
        if (fileTime <= 0)
        {
            return null;
        }

        try
        {
            return new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static void FreeIfAllocated(nint pointer)
    {
        if (pointer != nint.Zero)
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public nint TargetName;
        public nint Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public nint TargetAlias;
        public nint UserName;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CredWrite(nint credential, uint flags);

        [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CredRead(
            string target,
            uint type,
            uint flags,
            out nint credential);

        [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CredDelete(string target, uint type, uint flags);

        [LibraryImport("advapi32.dll", EntryPoint = "CredEnumerateW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CredEnumerate(
            string filter,
            uint flags,
            out uint count,
            out nint credentials);

        [LibraryImport("advapi32.dll")]
        public static partial void CredFree(nint buffer);
    }
}

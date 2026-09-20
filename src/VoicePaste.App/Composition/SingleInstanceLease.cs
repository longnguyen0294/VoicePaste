namespace VoicePaste.App.Composition;

internal sealed class SingleInstanceLease : IDisposable
{
    private const string MutexName = @"Local\VoicePaste.App.SingleInstance";
    private readonly Mutex _mutex;
    private bool _disposed;

    private SingleInstanceLease(Mutex mutex)
    {
        _mutex = mutex;
    }

    public static bool TryAcquire(out SingleInstanceLease? lease) =>
        TryAcquire(MutexName, out lease);

    internal static bool TryAcquire(string mutexName, out SingleInstanceLease? lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);
        var mutex = new Mutex(initiallyOwned: false, mutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            lease = null;
            return false;
        }

        lease = new SingleInstanceLease(mutex);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _mutex.Dispose();
        _disposed = true;
    }
}

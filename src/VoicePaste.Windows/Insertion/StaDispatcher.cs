using System.Windows.Threading;
using VoicePaste.Core;

namespace VoicePaste.Windows.Insertion;

internal sealed class StaDispatcher : IDisposable
{
    private readonly Thread _thread;
    private readonly TaskCompletionSource<Dispatcher> _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    public StaDispatcher()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "VoicePaste.ClipboardSTA",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public async Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var dispatcher = await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        SessionDiagnostics.Write("clipboard.dispatcher_invoke", $"ThreadAlive={_thread.IsAlive} ShutdownStarted={dispatcher.HasShutdownStarted} ShutdownFinished={dispatcher.HasShutdownFinished}");
        var operation = dispatcher.InvokeAsync(action, DispatcherPriority.Send, cancellationToken);
        return await operation.Task.ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_ready.Task.IsCompletedSuccessfully)
        {
            _ready.Task.Result.BeginInvokeShutdown(DispatcherPriority.Send);
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        _disposed = true;
    }

    private void Run()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        SessionDiagnostics.Write("clipboard.dispatcher_start");
        _ready.TrySetResult(dispatcher);
        Dispatcher.Run();
        SessionDiagnostics.Write("clipboard.dispatcher_exit");
    }
}


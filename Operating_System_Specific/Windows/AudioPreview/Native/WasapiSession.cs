using System.Runtime.InteropServices;

namespace BatCave.AudioPreview.Native;

/// <summary>Owns a dedicated COM worker. Start is explicit; construction never activates audio.</summary>
public abstract class WasapiSession : IDisposable
{
    private readonly object _lifecycle = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? _thread;
    private bool _disposed;
    private bool _resourcesDisposed;
    private int _running;
    public event Action<Exception>? Faulted;
    public bool IsRunning => Volatile.Read(ref _running) == 1;
    protected CancellationToken StopToken => _stop.Token;
    protected bool DisposalRequested => Volatile.Read(ref _disposed);

    public void Start()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is not null) throw new InvalidOperationException("Create a new audio session to restart a stopped stream.");
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                throw new PlatformNotSupportedException("The audio preview requires Windows 11 and process-loopback capture support.");
            _thread = new Thread(RunWorker) { IsBackground = true, Name = "BatCave audio" };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }
        try { _ready.Task.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult(); }
        catch { Dispose(); throw; }
    }

    protected void ReportStarted()
    {
        StopToken.ThrowIfCancellationRequested();
        Volatile.Write(ref _running, 1);
        _ready.TrySetResult();
    }

    private void RunWorker()
    {
        int initialized = CoreAudioInterop.CoInitializeEx(IntPtr.Zero, 0);
        try
        {
            CoreAudioInterop.Check(initialized, "Initialize Windows audio thread");
            RunAudio();
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { _ready.TrySetCanceled(); }
        catch (Exception error)
        {
            _ready.TrySetException(error);
            // Subscribers must post UI work and return promptly. Never allow a
            // subscriber failure to skip COM cleanup or terminate the process.
            try { Faulted?.Invoke(error); } catch { }
        }
        finally
        {
            Volatile.Write(ref _running, 0);
            _ready.TrySetException(new InvalidOperationException("The audio stream stopped before startup completed."));
            OnStopped();
            if (initialized >= 0) CoreAudioInterop.CoUninitialize();
            lock (_lifecycle)
            {
                if (_disposed && !_resourcesDisposed) { _stop.Dispose(); _resourcesDisposed = true; }
            }
        }
    }

    protected abstract void RunAudio();
    protected virtual void OnStopped() { }

    public void Dispose()
    {
        Thread? thread;
        lock (_lifecycle)
        {
            if (!_disposed) { _disposed = true; _stop.Cancel(); }
            thread = _thread;
        }
        // Calling Dispose from a callback requests stop without joining itself.
        // Native calls and buffer release finish on the owning worker thread.
        if (thread is not null && thread != Thread.CurrentThread) thread.Join(TimeSpan.FromSeconds(5));
        OnDisposed();
        lock (_lifecycle)
        {
            if (!_resourcesDisposed && (thread is null || !thread.IsAlive)) { _stop.Dispose(); _resourcesDisposed = true; }
        }
        GC.SuppressFinalize(this);
    }

    protected virtual void OnDisposed() { }
}

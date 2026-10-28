using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;

namespace Cut.UpdateEngine;

public sealed class SingleInstanceHost : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private Task? _listener;
    private bool _disposed;
    public bool IsPrimary { get; }

    // No data-directory or executable path in the key: copied builds share the same application lock.
    public SingleInstanceHost(string? key = null)
    {
        if (key is null)
        {
            using var identity = WindowsIdentity.GetCurrent();
            using var process = Process.GetCurrentProcess();
            key = "Cut.Application." + identity.User!.Value + "." + process.SessionId;
        }
        _pipeName = key;
        _mutex = new Mutex(false, @"Local\" + (key.StartsWith("Cut.Application.", StringComparison.Ordinal) ? "Cut.Screenshot.SingleInstance" : key));
        try { IsPrimary = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsPrimary = true; }
    }

    public void StartListening(Action activate)
    {
        if (!IsPrimary || _listener is not null) throw new InvalidOperationException("只有主实例可以接收激活请求。");
        var token = _stop.Token;
        _listener = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                    using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
                    request.CancelAfter(TimeSpan.FromSeconds(2));
                    var message = new byte[1];
                    if (await pipe.ReadAsync(message, request.Token).ConfigureAwait(false) == 1 && message[0] == 1 && !token.IsCancellationRequested)
                        activate();
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { return; }
            }
        });
    }

    public async Task<bool> NotifyExistingAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            await pipe.WriteAsync(new byte[] { 1 }, timeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException) { return false; }
    }

    // The UI thread owns the mutex and also disposes the application host on exit.
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        _listener?.GetAwaiter().GetResult();
        if (IsPrimary) _mutex.ReleaseMutex();
        _mutex.Dispose(); _stop.Dispose();
    }
}

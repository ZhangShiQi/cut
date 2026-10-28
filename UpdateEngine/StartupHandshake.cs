using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace Cut.UpdateEngine;

public sealed class StartupHandshake : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private StartupHandshake(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
    }
    public static async Task<StartupHandshake> ConnectAsync(string name, string nonce, CancellationToken token)
    {
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(token).ConfigureAwait(false);
            var session = new StartupHandshake(pipe);
            await session._writer.WriteLineAsync($"{nonce}:{Environment.ProcessId}".AsMemory(), token).ConfigureAwait(false);
            if (await session._reader.ReadLineAsync(token).ConfigureAwait(false) != "initialize") throw new IOException("启动器未授权初始化。");
            return session;
        }
        catch { pipe.Dispose(); throw; }
    }
    public async Task ReadyAsync(CancellationToken token)
    {
        await _writer.WriteLineAsync("ready".AsMemory(), token).ConfigureAwait(false);
        if (await _reader.ReadLineAsync(token).ConfigureAwait(false) != "commit") throw new IOException("启动未确认，停止运行以便恢复旧版。");
    }
    public async ValueTask DisposeAsync()
    { _writer.Dispose(); _reader.Dispose(); await _pipe.DisposeAsync().ConfigureAwait(false); }
}

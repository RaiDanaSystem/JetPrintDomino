using System.Net;
using System.Net.Sockets;

namespace DominoBridge.Tests;

/// <summary>Minimal in-process EDC TCP printer: collects STX..ETX frames and answers with a configurable byte.</summary>
public sealed class FakeEdcServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    public List<byte[]> Frames { get; } = new();
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Decides the reply for frame number n (1-based). Null = stay silent. Empty = close connection.</summary>
    public Func<int, byte[]?> Reply { get; set; } = _ => new byte[] { 0x06 };
    public int Connections;

    public FakeEdcServer()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    public int FrameCount { get { lock (_gate) return Frames.Count; } }

    private async Task AcceptLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var c = await _listener.AcceptTcpClientAsync(_cts.Token);
                Interlocked.Increment(ref Connections);
                _ = Task.Run(() => Serve(c));
            }
        }
        catch { }
    }

    private async Task Serve(TcpClient c)
    {
        using var _ = c;
        var s = c.GetStream();
        var buf = new byte[1024];
        var cur = new List<byte>();
        try
        {
            while (true)
            {
                int n = await s.ReadAsync(buf, _cts.Token);
                if (n == 0) return;
                for (int i = 0; i < n; i++)
                {
                    cur.Add(buf[i]);
                    if (buf[i] == 0x03)
                    {
                        int idx;
                        lock (_gate) { Frames.Add(cur.ToArray()); idx = Frames.Count; }
                        cur.Clear();
                        var reply = Reply(idx);
                        if (reply == null) continue;
                        if (reply.Length == 0) { c.Close(); return; }
                        await s.WriteAsync(reply, _cts.Token);
                    }
                }
            }
        }
        catch { }
    }

    public void Dispose() { _cts.Cancel(); _listener.Stop(); }
}

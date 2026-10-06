using System.Net.Sockets;

namespace DominoBridge.Core.Transport;

public sealed class EdcTcpClient : IEdcTransport
{
    private TcpClient? _client;
    private NetworkStream? _stream;

    public bool IsAlive
    {
        get
        {
            var c = _client;
            if (c == null || !c.Connected) return false;
            try
            {
                var s = c.Client;
                // Readable with nothing available = orderly close by the peer.
                return !(s.Poll(0, SelectMode.SelectRead) && s.Available == 0);
            }
            catch { return false; }
        }
    }

    public async Task ConnectAsync(string host, int port, int timeoutMs, CancellationToken ct)
    {
        Close();
        var client = new TcpClient { NoDelay = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException($"Connect to {host}:{port} timed out after {timeoutMs} ms.");
        }
        catch
        {
            client.Dispose();
            throw;
        }
        _client = client;
        _stream = client.GetStream();
    }

    public void Close()
    {
        try { _stream?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        _stream = null;
        _client = null;
    }

    public byte[] DrainReceived()
    {
        var c = _client;
        if (c == null || _stream == null) return Array.Empty<byte>();
        var all = new List<byte>();
        try
        {
            var buf = new byte[256];
            while (c.Available > 0)
            {
                int n = _stream.Read(buf, 0, Math.Min(buf.Length, c.Available));
                if (n <= 0) break;
                all.AddRange(buf.AsSpan(0, n).ToArray());
            }
        }
        catch { /* connection state is re-checked by caller */ }
        return all.ToArray();
    }

    public async Task SendAsync(byte[] data, CancellationToken ct)
    {
        var s = _stream ?? throw new InvalidOperationException("Not connected.");
        await s.WriteAsync(data, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    public async Task<byte[]> ReceiveAsync(int timeoutMs, CancellationToken ct)
    {
        var s = _stream ?? throw new InvalidOperationException("Not connected.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        var buf = new byte[256];
        try
        {
            int n = await s.ReadAsync(buf, cts.Token).ConfigureAwait(false);
            if (n == 0) throw new ConnectionClosedException("Printer closed the connection.");
            return buf.AsSpan(0, n).ToArray();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Array.Empty<byte>();
        }
    }

    public void Dispose() => Close();
}

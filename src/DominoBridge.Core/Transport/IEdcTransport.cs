namespace DominoBridge.Core.Transport;

public class ConnectionClosedException : IOException
{
    public ConnectionClosedException(string message) : base(message) { }
}

public interface IEdcTransport : IDisposable
{
    /// <summary>True only if the socket is connected and the peer has not closed it.</summary>
    bool IsAlive { get; }
    Task ConnectAsync(string host, int port, int timeoutMs, CancellationToken ct);
    void Close();
    /// <summary>Discards bytes already waiting in the receive buffer; returns them.</summary>
    byte[] DrainReceived();
    /// <summary>Writes the whole buffer in one call.</summary>
    Task SendAsync(byte[] data, CancellationToken ct);
    /// <summary>Waits for incoming bytes. Returns an empty array on timeout; throws ConnectionClosedException if the peer closed.</summary>
    Task<byte[]> ReceiveAsync(int timeoutMs, CancellationToken ct);
}

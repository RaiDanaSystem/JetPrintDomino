using DominoBridge.Core.Configuration;
using DominoBridge.Core.Logging;
using DominoBridge.Core.Protocol;

namespace DominoBridge.Core.Transport;

public enum ConnectionState { Disconnected, Connecting, Connected }

public enum DeliveryOutcome
{
    /// <summary>ACK received: accepted by the EDC protocol layer (NOT proof of physical printing).</summary>
    Accepted,
    /// <summary>NAK received.</summary>
    Rejected,
    /// <summary>Nothing was written to the socket; safe to try again.</summary>
    NotSent,
    /// <summary>Bytes may or may not have reached the printer (timeout, disconnect, odd reply).</summary>
    Unknown
}

public sealed record DeliveryResult(DeliveryOutcome Outcome, string Message, byte[] Response);

public sealed class PrinterConnectionManager : IDisposable
{
    private readonly AppSettings _settings;
    private readonly Func<IEdcTransport> _factory;
    private readonly IAppLogger _log;
    private readonly SemaphoreSlim _io = new(1, 1);
    private IEdcTransport? _transport;
    private CancellationTokenSource? _reconnectCts;
    private bool _wantConnected;

    public PrinterConnectionManager(AppSettings settings, Func<IEdcTransport>? factory = null, IAppLogger? log = null)
    {
        _settings = settings;
        _factory = factory ?? (() => new EdcTcpClient());
        _log = log ?? NullLogger.Instance;
    }

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string? LastError { get; private set; }
    public event Action<ConnectionState>? StateChanged;

    public bool IsConnected => State == ConnectionState.Connected && (_transport?.IsAlive ?? false);

    private void SetState(ConnectionState s)
    {
        if (State == s) return;
        State = s;
        StateChanged?.Invoke(s);
    }

    public Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        _wantConnected = true;
        StopReconnect();
        return ConnectCoreAsync(ct);
    }

    private async Task<bool> ConnectCoreAsync(CancellationToken ct)
    {
        var p = _settings.Printer;
        SetState(ConnectionState.Connecting);
        _transport?.Dispose();
        var t = _factory();
        try
        {
            await t.ConnectAsync(p.IpAddress, p.Port, p.ConnectTimeoutMs, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            t.Dispose();
            _transport = null;
            LastError = ex.Message;
            _log.Error($"Connection Failed: {p.IpAddress}:{p.Port} - {ex.Message}");
            SetState(ConnectionState.Disconnected);
            return false;
        }
        _transport = t;
        LastError = null;
        _log.Info($"Connection opened: {p.IpAddress}:{p.Port}");
        SetState(ConnectionState.Connected);
        return true;
    }

    public void Disconnect()
    {
        _wantConnected = false;
        StopReconnect();
        if (_transport != null)
        {
            _transport.Dispose();
            _transport = null;
            _log.Info("Connection closed");
        }
        SetState(ConnectionState.Disconnected);
    }

    /// <summary>Only checks that a TCP connection can be established. Sends nothing.</summary>
    public async Task<(bool Ok, string Message)> TestConnectionAsync(CancellationToken ct = default)
    {
        var p = _settings.Printer;
        if (IsConnected) return (true, $"Already connected to {p.IpAddress}:{p.Port}.");
        using var t = _factory();
        try
        {
            await t.ConnectAsync(p.IpAddress, p.Port, p.ConnectTimeoutMs, ct).ConfigureAwait(false);
            _log.Info($"Test connection OK: {p.IpAddress}:{p.Port} (nothing sent)");
            return (true, $"TCP connection to {p.IpAddress}:{p.Port} succeeded.");
        }
        catch (Exception ex)
        {
            _log.Error($"Connection Failed (test): {p.IpAddress}:{p.Port} - {ex.Message}");
            return (false, $"Connection Failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Sends one complete packet and waits for the ACK/NAK. Never retries; the caller decides.
    /// </summary>
    public async Task<DeliveryResult> SendPacketAsync(EdcProtocol protocol, byte[] packet, CancellationToken ct = default)
    {
        await _io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var t = _transport;
            if (t == null || !t.IsAlive)
            {
                OnConnectionLost("not connected before send");
                return new DeliveryResult(DeliveryOutcome.NotSent, "Not connected; nothing was sent.", Array.Empty<byte>());
            }

            var stale = t.DrainReceived();
            if (stale.Length > 0) _log.Warn($"Discarded unexpected bytes before send: {EdcProtocol.ToHex(stale)}");

            try
            {
                await t.SendAsync(packet, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                OnConnectionLost("send error: " + ex.Message);
                return new DeliveryResult(DeliveryOutcome.Unknown, $"Send error ({ex.Message}); delivery state unknown.", Array.Empty<byte>());
            }
            if (_settings.DebugHex) _log.Debug("TX: " + EdcProtocol.ToHex(packet));

            var parser = protocol.CreateAckParser();
            var deadline = DateTime.UtcNow.AddMilliseconds(_settings.Printer.AckTimeoutMs);
            var received = new List<byte>();
            while (true)
            {
                int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remaining <= 0)
                {
                    _log.Warn("Timeout waiting for ACK");
                    return new DeliveryResult(DeliveryOutcome.Unknown, $"No ACK within {_settings.Printer.AckTimeoutMs} ms; delivery state unknown.", received.ToArray());
                }
                byte[] chunk;
                try { chunk = await t.ReceiveAsync(remaining, ct).ConfigureAwait(false); }
                catch (ConnectionClosedException)
                {
                    OnConnectionLost("closed while waiting for ACK");
                    return new DeliveryResult(DeliveryOutcome.Unknown, "Connection lost before ACK; delivery state unknown.", received.ToArray());
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    OnConnectionLost("receive error: " + ex.Message);
                    return new DeliveryResult(DeliveryOutcome.Unknown, $"Receive error ({ex.Message}); delivery state unknown.", received.ToArray());
                }
                if (chunk.Length == 0) continue; // timeout slice; loop re-evaluates deadline
                received.AddRange(chunk);
                if (_settings.DebugHex) _log.Debug("RX: " + EdcProtocol.ToHex(chunk));

                var r = parser.Parse(received.ToArray());
                switch (r.Kind)
                {
                    case AckKind.Ack:
                        _log.Info("ACK received: " + EdcProtocol.ToHex(r.Raw));
                        return new DeliveryResult(DeliveryOutcome.Accepted, "ACK received: " + EdcProtocol.ToHex(r.Raw), r.Raw);
                    case AckKind.Nak:
                        _log.Warn("NAK received: " + EdcProtocol.ToHex(r.Raw));
                        return new DeliveryResult(DeliveryOutcome.Rejected, "NAK received: " + EdcProtocol.ToHex(r.Raw), r.Raw);
                    case AckKind.Unexpected:
                        _log.Warn("Unexpected response: " + EdcProtocol.ToHex(r.Raw));
                        return new DeliveryResult(DeliveryOutcome.Unknown, "Unexpected response " + EdcProtocol.ToHex(r.Raw) + "; delivery state unknown.", r.Raw);
                }
            }
        }
        finally { _io.Release(); }
    }

    private void OnConnectionLost(string reason)
    {
        _log.Warn("Disconnected: " + reason);
        _transport?.Dispose();
        _transport = null;
        SetState(ConnectionState.Disconnected);
        StartReconnect();
    }

    private void StartReconnect()
    {
        if (!_wantConnected || _settings.Queue.ReconnectIntervalMs <= 0) return;
        StopReconnect();
        var cts = _reconnectCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested && _wantConnected)
            {
                try { await Task.Delay(_settings.Queue.ReconnectIntervalMs, cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                _log.Info("Retry connection");
                if (await ConnectCoreAsync(cts.Token).ConfigureAwait(false)) return;
                if (cts.IsCancellationRequested) return;
            }
        });
    }

    private void StopReconnect()
    {
        var c = _reconnectCts;
        _reconnectCts = null;
        try { c?.Cancel(); } catch { }
    }

    public void Dispose()
    {
        Disconnect();
        _io.Dispose();
    }
}

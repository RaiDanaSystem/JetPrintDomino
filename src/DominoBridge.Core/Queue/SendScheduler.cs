using DominoBridge.Core.Configuration;
using DominoBridge.Core.Logging;
using DominoBridge.Core.Protocol;
using DominoBridge.Core.Transport;

namespace DominoBridge.Core.Queue;

public enum SchedulerState
{
    Idle,
    Running,
    Paused,
    /// <summary>Batch limit reached; the operator must confirm the printer buffer has drained.</summary>
    WaitingForOperator,
    /// <summary>Stopped because something needs an operator decision (NAK, unknown delivery, disconnect, invalid row).</summary>
    Halted,
    Completed,
    Stopped
}

/// <summary>
/// Feeds the printer's EDC FIFO buffer from the application queue, strictly in order, one row at a time:
/// send -> wait ACK/NAK -> next. It never blindly re-sends a row whose delivery state is unclear.
/// Pause/Stop only affect sending; they never clear the printer buffer.
/// </summary>
public sealed class SendScheduler
{
    private readonly PrintQueue _queue;
    private readonly PrinterConnectionManager _conn;
    private readonly AppSettings _settings;
    private readonly IAppLogger _log;
    private volatile bool _paused;
    private volatile bool _stop;
    private int _bufferEstimate;

    public SendScheduler(PrintQueue queue, PrinterConnectionManager conn, AppSettings settings, IAppLogger? log = null)
    {
        _queue = queue; _conn = conn; _settings = settings; _log = log ?? NullLogger.Instance;
    }

    public SchedulerState State { get; private set; } = SchedulerState.Idle;
    public string? StateMessage { get; private set; }
    public event Action<SchedulerState, string?>? StateChanged;

    /// <summary>
    /// ESTIMATE of items accepted into the printer buffer since the operator last confirmed it was empty.
    /// The printer's real fill level is only readable with the BufferDepth ack type (not implemented: UNKNOWN / REQUIRES DEVICE TEST).
    /// </summary>
    public int BufferEstimate => Volatile.Read(ref _bufferEstimate);

    public int BatchLimit => Math.Max(1, Math.Min(_settings.Queue.BatchSize, _settings.Queue.MaxQueueSize));

    public bool IsActive => State is SchedulerState.Running or SchedulerState.Paused;

    public void Pause() { if (State == SchedulerState.Running) _paused = true; }
    public void Resume() { _paused = false; }
    public void RequestStop() { _stop = true; _paused = false; }

    /// <summary>Operator confirms the printer's buffer is empty (or sets the known fill level).</summary>
    public void SetBufferEstimate(int value) => Interlocked.Exchange(ref _bufferEstimate, Math.Max(0, value));

    private void SetState(SchedulerState s, string? message = null)
    {
        State = s; StateMessage = message;
        StateChanged?.Invoke(s, message);
    }

    public async Task RunAsync(CancellationToken abort = default)
    {
        if (IsActive) return;
        _stop = false; _paused = false;

        var protocol = new EdcProtocol(_settings.Printer);
        var errors = protocol.ValidateConfiguration().Where(i => i.Severity == IssueSeverity.Error).ToList();
        if (errors.Count > 0) { Halt("Invalid configuration: " + errors[0].Message); return; }

        var blocking = _queue.Unresolved();
        if (blocking.Count > 0)
        {
            Halt($"{blocking.Count} row(s) need an operator decision first (first: Excel row {blocking[0].ExcelRowId}, {blocking[0].Status}). " +
                 "Sending is blocked so the print order is never broken.");
            return;
        }

        SetState(SchedulerState.Running);
        _log.Info($"Start sending (batch limit {BatchLimit}, buffer estimate {BufferEstimate})");

        PrintRow? row = null;
        int consecutiveNaks = 0;
        try
        {
            while (true)
            {
                if (_stop) { SetState(SchedulerState.Stopped, "Stopped by operator. Printer buffer was NOT cleared."); _log.Info("Stopped (printer buffer untouched)"); return; }
                abort.ThrowIfCancellationRequested();

                if (_paused)
                {
                    SetState(SchedulerState.Paused);
                    _log.Info("Paused (printer buffer untouched)");
                    while (_paused && !_stop) await Task.Delay(100, abort).ConfigureAwait(false);
                    if (_stop) continue;
                    SetState(SchedulerState.Running);
                    _log.Info("Resumed");
                }

                row = _queue.NextPending();
                if (row == null)
                {
                    SetState(SchedulerState.Completed, "All rows have been handled.");
                    _log.Info("No pending rows left");
                    return;
                }
                if (!_settings.Queue.AutoRefill && BufferEstimate >= BatchLimit)
                {
                    SetState(SchedulerState.WaitingForOperator,
                        $"{BufferEstimate} item(s) were accepted into the printer buffer (limit {BatchLimit}). Continue once the printer has consumed them.");
                    _log.Info("Batch limit reached; waiting for operator");
                    return;
                }

                if (!await EnsureConnectedAsync(abort).ConfigureAwait(false))
                {
                    Halt($"Printer not connected. Row {row.ExcelRowId} was not sent and stays Pending.");
                    return;
                }

                byte[] packet;
                try { packet = protocol.BuildPacket(row.Fields); }
                catch (EdcProtocolException ex)
                {
                    row.Set(RowStatus.Failed, "Not sent: " + ex.Message);
                    _log.Error($"Row {row.ExcelRowId} failed (not sent): {ex.Message}");
                    Halt($"Row {row.ExcelRowId} is invalid: {ex.Message}");
                    return;
                }

                row.MarkAttempt();
                row.Set(RowStatus.Sending);
                _log.Info($"Row {row.ExcelRowId}: data sent {row.DisplayPayload}");

                DeliveryResult result;
                try { result = await _conn.SendPacketAsync(protocol, packet, abort).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    row.Set(RowStatus.Unknown, "Aborted while waiting for ACK; delivery state unknown.");
                    throw;
                }

                switch (result.Outcome)
                {
                    case DeliveryOutcome.Accepted:
                        row.Set(RowStatus.Accepted, "Accepted by printer (ACK) - not proof of printing");
                        Interlocked.Increment(ref _bufferEstimate);
                        consecutiveNaks = 0;
                        _log.Info($"Row {row.ExcelRowId} accepted");
                        break;
                    case DeliveryOutcome.Rejected when _settings.Queue.AutoRefill
                                                      && ++consecutiveNaks <= _settings.Queue.AutoRefillMaxConsecutiveNaks:
                        // NAK = the printer did not take the row (typically buffer full). Safe to offer the same row again.
                        row.Set(RowStatus.Pending, $"NAK ({EdcProtocol.ToHex(result.Response)}) - printer buffer full? waiting to retry ({consecutiveNaks})");
                        SetBufferEstimate(0);
                        if (consecutiveNaks == 1) _log.Info($"Row {row.ExcelRowId}: NAK {EdcProtocol.ToHex(result.Response)}, waiting for printer buffer space (auto refill)");
                        SetState(SchedulerState.Running, "Waiting for space in the printer buffer...");
                        await Task.Delay(Math.Max(100, _settings.Queue.AutoRefillDelayMs), abort).ConfigureAwait(false);
                        SetState(SchedulerState.Running);
                        break;
                    case DeliveryOutcome.Rejected:
                        row.Set(RowStatus.Rejected, result.Message);
                        _log.Error($"Row {row.ExcelRowId} rejected: {result.Message} (printer buffer may be full)");
                        Halt($"Row {row.ExcelRowId} was rejected by the printer (NAK). Decide: Retry or Skip.");
                        return;
                    case DeliveryOutcome.NotSent:
                        row.Set(RowStatus.Pending, "Not sent: " + result.Message);
                        Halt($"Row {row.ExcelRowId} was not sent: {result.Message}");
                        return;
                    default:
                        row.Set(RowStatus.Unknown, result.Message);
                        _log.Error($"Row {row.ExcelRowId}: unknown delivery state - {result.Message}");
                        Halt($"Row {row.ExcelRowId}: delivery state UNKNOWN. Check the printer, then mark it Accepted or Not received. It is NOT re-sent automatically.");
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            SetState(SchedulerState.Stopped, "Aborted.");
        }
        catch (Exception ex)
        {
            if (row is { Status: RowStatus.Sending }) row.Set(RowStatus.Unknown, "Internal error while sending: " + ex.Message);
            _log.Error("Scheduler error: " + ex);
            Halt("Internal error: " + ex.Message);
        }
    }

    private void Halt(string reason)
    {
        _log.Error(reason);
        SetState(SchedulerState.Halted, reason);
    }

    /// <summary>
    /// Only reconnects while the current row is known untouched, so a retry can never cause a duplicate print.
    /// </summary>
    private async Task<bool> EnsureConnectedAsync(CancellationToken ct)
    {
        if (_conn.IsConnected) return true;
        for (int attempt = 0; attempt <= Math.Max(0, _settings.Queue.MaxRetry); attempt++)
        {
            _log.Info(attempt == 0 ? "Connection not alive; reconnecting" : $"Reconnect retry {attempt}");
            if (await _conn.ConnectAsync(ct).ConfigureAwait(false)) return true;
        }
        return false;
    }
}

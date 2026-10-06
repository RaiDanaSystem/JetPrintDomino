using DominoBridge.Core.Excel;
using DominoBridge.Core.Protocol;

namespace DominoBridge.Core.Queue;

public readonly record struct QueueCounts(
    int Total, int Pending, int Sending, int Accepted, int Rejected, int Failed, int Unknown, int Skipped)
{
    /// <summary>Rows that were written to the socket at least once.</summary>
    public int Sent => Accepted + Rejected + Unknown;
    /// <summary>Rows that block further sending until the operator decides.</summary>
    public int Unresolved => Rejected + Failed + Unknown;
}

/// <summary>Ordered application queue. Order always equals Excel order.</summary>
public sealed class PrintQueue
{
    private readonly object _gate = new();
    private int _scanFrom;

    public PrintQueue(IReadOnlyList<PrintRow> rows) { Rows = rows; }

    public IReadOnlyList<PrintRow> Rows { get; }

    /// <summary>Builds queue rows from a table. Invalid rows become Failed (never sent).</summary>
    public static PrintQueue FromExcel(ExcelTable table, IReadOnlyList<FieldMapping> mapping, EdcProtocol protocol)
    {
        var v = ColumnMapper.Validate(mapping, table.Headers.Length);
        if (!v.IsValid) throw new ArgumentException(string.Join(" ", v.Errors));

        var rows = new List<PrintRow>(table.Rows.Count);
        int seq = 0;
        foreach (var r in table.Rows)
        {
            var row = new PrintRow(++seq, r.RowNumber, ColumnMapper.MapRow(r, mapping));
            try
            {
                var data = protocol.BuildDataString(row.Fields);
                var packet = protocol.BuildDataPacket(data);
                row.DataString = data;
                row.DisplayPayload = protocol.ToDisplayString(data);
                row.HexPayload = EdcProtocol.ToHex(packet);
            }
            catch (EdcProtocolException ex)
            {
                row.Set(RowStatus.Failed, "Not sent: " + ex.Message);
            }
            rows.Add(row);
        }
        return new PrintQueue(rows);
    }

    public PrintRow? NextPending()
    {
        lock (_gate)
        {
            for (int i = _scanFrom; i < Rows.Count; i++)
            {
                var s = Rows[i].Status;
                if (s == RowStatus.Pending) { _scanFrom = i; return Rows[i]; }
            }
            _scanFrom = Rows.Count;
            return null;
        }
    }

    public QueueCounts Counts()
    {
        int p = 0, se = 0, a = 0, r = 0, f = 0, u = 0, sk = 0;
        lock (_gate)
        {
            foreach (var row in Rows)
            {
                switch (row.Status)
                {
                    case RowStatus.Pending: p++; break;
                    case RowStatus.Sending: se++; break;
                    case RowStatus.Accepted: case RowStatus.Printed: a++; break;
                    case RowStatus.Rejected: r++; break;
                    case RowStatus.Failed: f++; break;
                    case RowStatus.Unknown: u++; break;
                    case RowStatus.Skipped: sk++; break;
                }
            }
        }
        return new QueueCounts(Rows.Count, p, se, a, r, f, u, sk);
    }

    public IReadOnlyList<PrintRow> Unresolved() =>
        Rows.Where(r => r.Status is RowStatus.Rejected or RowStatus.Failed or RowStatus.Unknown or RowStatus.Sending).ToList();

    // ---- explicit operator decisions ----

    /// <summary>Rejected (NAK) row goes back to Pending so it is sent again, in its original position.</summary>
    public bool Retry(PrintRow row)
    {
        if (row.Status != RowStatus.Rejected || row.DataString == null) return false;
        row.Set(RowStatus.Pending, "Retry requested by operator");
        lock (_gate) _scanFrom = Math.Min(_scanFrom, row.Sequence - 1);
        return true;
    }

    /// <summary>Operator verified that an Unknown row was in fact received by the printer.</summary>
    public bool MarkAccepted(PrintRow row)
    {
        if (row.Status != RowStatus.Unknown) return false;
        row.Set(RowStatus.Accepted, "Marked accepted by operator");
        return true;
    }

    /// <summary>Operator verified that an Unknown row was NOT received; it will be sent again in its original position.</summary>
    public bool MarkNotReceived(PrintRow row)
    {
        if (row.Status != RowStatus.Unknown || row.DataString == null) return false;
        row.Set(RowStatus.Pending, "Marked not received by operator");
        lock (_gate) _scanFrom = Math.Min(_scanFrom, row.Sequence - 1);
        return true;
    }

    public bool Skip(PrintRow row)
    {
        if (row.Status is not (RowStatus.Rejected or RowStatus.Failed or RowStatus.Unknown or RowStatus.Pending)) return false;
        row.Set(RowStatus.Skipped, "Skipped by operator");
        return true;
    }
}

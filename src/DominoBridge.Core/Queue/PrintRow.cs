using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DominoBridge.Core.Queue;

public enum RowStatus
{
    Pending,
    Sending,
    /// <summary>ACK received: accepted by the printer's EDC layer. NOT proof of physical printing.</summary>
    Accepted,
    /// <summary>NAK received.</summary>
    Rejected,
    /// <summary>Known NOT to have been sent (invalid data, encoding error, ...).</summary>
    Failed,
    /// <summary>Sent but outcome not known (timeout / disconnect). Needs an operator decision.</summary>
    Unknown,
    /// <summary>Operator decided to leave this row out.</summary>
    Skipped,
    /// <summary>Reserved. Physical printing cannot be detected over EDC TCP; this app never sets it.</summary>
    Printed
}

public sealed class PrintRow : INotifyPropertyChanged
{
    private RowStatus _status = RowStatus.Pending;
    private string _message = "";
    private int _attempts;

    public PrintRow(int sequence, int excelRowId, string[] fields)
    {
        Sequence = sequence; ExcelRowId = excelRowId; Fields = fields;
    }

    /// <summary>1-based position in the send order (Excel order).</summary>
    public int Sequence { get; }
    /// <summary>Row number in the Excel sheet. Internal id for logs/state only; never added to the data string.</summary>
    public int ExcelRowId { get; }
    public string[] Fields { get; }
    /// <summary>Plain data string (without markers), or null if it could not be built.</summary>
    public string? DataString { get; set; }
    /// <summary>Display form, e.g. &lt;STX&gt;123,A001&lt;ETX&gt;.</summary>
    public string DisplayPayload { get; set; } = "";
    public string HexPayload { get; set; } = "";

    public RowStatus Status { get => _status; private set { if (_status != value) { _status = value; OnChanged(); } } }
    public string Message { get => _message; private set { if (_message != value) { _message = value; OnChanged(); } } }
    public int Attempts { get => _attempts; private set { _attempts = value; OnChanged(); } }

    public void Set(RowStatus status, string message = "")
    {
        Message = message;
        Status = status;
    }

    public void MarkAttempt() => Attempts++;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

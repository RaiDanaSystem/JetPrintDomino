using System.Net;
using System.Text;
using DominoBridge.Core.Configuration;

namespace DominoBridge.Core.Protocol;

/// <summary>
/// Domino EDC TCP framing: STX + DATA + ETX (real bytes, never the text "&lt;STX&gt;").
/// Independent of UI and sockets. Markers, encoding, delimiter and ACK bytes all come from configuration.
/// </summary>
public sealed class EdcProtocol
{
    private readonly PrinterSettings _s;

    public EdcProtocol(PrinterSettings settings) { _s = settings; }

    public IAckParser CreateAckParser() => new AckParser((byte)_s.AckByte, (byte)_s.NakByte);

    public AckResult ParseAck(ReadOnlySpan<byte> bytes) => CreateAckParser().Parse(bytes);

    private Encoding GetEncoding()
    {
        if (!EdcEncodings.TryParse(_s.Encoding, out var e))
            throw new EdcProtocolException($"Unknown encoding '{_s.Encoding}'.");
        return EdcEncodings.Create(e);
    }

    /// <summary>Joins fields with the configured delimiter. Fields containing the delimiter are rejected (they would shift EDC indexes).</summary>
    public string BuildDataString(IReadOnlyList<string> fields)
    {
        var d = _s.Delimiter;
        if (string.IsNullOrEmpty(d)) throw new EdcProtocolException("Delimiter is empty.");
        for (int i = 0; i < fields.Count; i++)
        {
            if (fields[i].Contains(d, StringComparison.Ordinal))
                throw new EdcProtocolException($"Field {i} contains the delimiter '{Printable(d)}' and would shift the EDC indexes.");
        }
        return string.Join(d, fields);
    }

    /// <summary>STX + encode(data) + ETX.</summary>
    public byte[] BuildDataPacket(string data)
    {
        var start = (char)_s.StartMarker;
        var end = (char)_s.EndMarker;
        if (data.Contains(start) || data.Contains(end))
            throw new EdcProtocolException("Data contains a start/end marker character and would break EDC framing.");

        byte[] payload;
        try { payload = GetEncoding().GetBytes(data); }
        catch (EncoderFallbackException ex)
        {
            throw new EdcProtocolException($"Data cannot be encoded as {_s.Encoding}: {ex.Message}");
        }

        var packet = new byte[payload.Length + 2];
        packet[0] = (byte)_s.StartMarker;
        Buffer.BlockCopy(payload, 0, packet, 1, payload.Length);
        packet[^1] = (byte)_s.EndMarker;
        return packet;
    }

    public byte[] BuildPacket(IReadOnlyList<string> fields) => BuildDataPacket(BuildDataString(fields));

    public IReadOnlyList<ConfigIssue> ValidateConfiguration()
    {
        var list = new List<ConfigIssue>();
        void Err(string m) => list.Add(new ConfigIssue(IssueSeverity.Error, m));
        void Warn(string m) => list.Add(new ConfigIssue(IssueSeverity.Warning, m));

        if (string.IsNullOrWhiteSpace(_s.IpAddress)) Err("Printer IP / host is empty.");
        else if (!IPAddress.TryParse(_s.IpAddress, out _) && Uri.CheckHostName(_s.IpAddress) == UriHostNameType.Unknown)
            Err($"'{_s.IpAddress}' is not a valid IP address or host name.");
        if (_s.Port is < 1 or > 65535) Err("Port must be 1..65535.");
        if (_s.Port == 7000) Warn("Port 7000 is not the EDC TCP default (16000).");
        if (!EdcEncodings.TryParse(_s.Encoding, out var enc)) Err($"Unknown encoding '{_s.Encoding}'.");
        else if (enc is EdcEncoding.UnicodeBigEndian or EdcEncoding.UnicodeLittleEndian)
            Warn("UNKNOWN / REQUIRES DEVICE TEST: with UTF-16 encodings it is not specified whether the markers are sent as single bytes or encoded characters. Markers are sent as single bytes.");
        if (_s.StartMarker is < 0 or > 255) Err("Start marker must be 0..255.");
        if (_s.EndMarker is < 0 or > 255) Err("End marker must be 0..255.");
        if (_s.StartMarker == _s.EndMarker) Err("Start and end marker must differ.");
        if (_s.StartMarker != 0x02 || _s.EndMarker != 0x03) Warn("Markers differ from the EDC default STX(0x02)/ETX(0x03); they must match the printer setting.");
        if (_s.AckByte is < 0 or > 255) Err("ACK byte must be 0..255.");
        if (_s.NakByte is < 0 or > 255) Err("NAK byte must be 0..255.");
        if (_s.AckByte == _s.NakByte) Err("ACK and NAK bytes must differ.");
        if (string.IsNullOrEmpty(_s.Delimiter) || _s.Delimiter.Length != 1) Err("Delimiter must be exactly one character.");
        if (_s.AckTimeoutMs <= 0) Err("ACK timeout must be > 0.");
        if (_s.ConnectTimeoutMs <= 0) Err("Connect timeout must be > 0.");
        return list;
    }

    // ---- display helpers (display only; never used for transmission) ----

    public static string ToHex(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length * 3);
        foreach (var b in bytes) { if (sb.Length > 0) sb.Append(' '); sb.Append(b.ToString("X2")); }
        return sb.ToString();
    }

    /// <summary>Human-readable form, e.g. &lt;STX&gt;12345,A001&lt;ETX&gt;. Display only.</summary>
    public string ToDisplayString(string data) => $"{MarkerName(_s.StartMarker, "STX")}{data}{MarkerName(_s.EndMarker, "ETX")}";

    private static string MarkerName(int marker, string defaultName) =>
        marker == (defaultName == "STX" ? 2 : 3) ? $"<{defaultName}>" : $"<0x{marker:X2}>";

    private static string Printable(string s) => s == "\t" ? "TAB" : s;
}

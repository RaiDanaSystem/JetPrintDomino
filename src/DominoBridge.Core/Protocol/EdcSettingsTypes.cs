using System.Text;
using DominoBridge.Core.Configuration;

namespace DominoBridge.Core.Protocol;

public enum EdcEncoding { Ascii, Utf8, UnicodeBigEndian, UnicodeLittleEndian }

public static class EdcEncodings
{
    public static readonly string[] Names = { "ASCII", "UTF-8", "UnicodeBigEndian", "UnicodeLittleEndian" };

    public static bool TryParse(string? name, out EdcEncoding value)
    {
        switch ((name ?? "").Trim().ToUpperInvariant().Replace("-", "").Replace("_", ""))
        {
            case "ASCII": value = EdcEncoding.Ascii; return true;
            case "UTF8": value = EdcEncoding.Utf8; return true;
            case "UNICODEBIGENDIAN": case "UTF16BE": value = EdcEncoding.UnicodeBigEndian; return true;
            case "UNICODELITTLEENDIAN": case "UTF16LE": case "UNICODE": value = EdcEncoding.UnicodeLittleEndian; return true;
            default: value = default; return false;
        }
    }

    /// <summary>Strict encoders: characters that cannot be represented throw instead of becoming '?'.</summary>
    public static Encoding Create(EdcEncoding e) => e switch
    {
        EdcEncoding.Ascii => Encoding.GetEncoding("us-ascii", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
        EdcEncoding.Utf8 => new UTF8Encoding(false, true),
        EdcEncoding.UnicodeBigEndian => new UnicodeEncoding(true, false, true),
        EdcEncoding.UnicodeLittleEndian => new UnicodeEncoding(false, false, true),
        _ => throw new ArgumentOutOfRangeException(nameof(e))
    };
}

public enum IssueSeverity { Warning, Error }

public sealed record ConfigIssue(IssueSeverity Severity, string Message);

public enum AckKind { Incomplete, Ack, Nak, Unexpected }

public sealed record AckResult(AckKind Kind, byte[] Raw);

public class EdcProtocolException : Exception
{
    public EdcProtocolException(string message) : base(message) { }
}

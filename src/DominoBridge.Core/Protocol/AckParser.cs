namespace DominoBridge.Core.Protocol;

/// <summary>Seam for alternative ack types (e.g. BufferDepth, UNKNOWN / not implemented in v1).</summary>
public interface IAckParser
{
    AckResult Parse(ReadOnlySpan<byte> received);
}

/// <summary>
/// Understands the replies documented in the Ax-Series EDC manual (EPT077984):
///  - ACK type "DefaultSpecialChars": single byte 0x06.
///  - ACK type "Default": the three letters "ACK" (41 43 4B).
///  - Fixed-length Codenet style ACK 06 30 30 30, and NAK = 0x15 optionally followed by a 3 character code (15 A B C).
///  - Letters "NAK".
/// The NAK layout for EDC TCP itself is not stated in the manual, so any reply that starts with the NAK byte
/// (and is at most 4 bytes long) is treated as a NAK. Everything else is Unexpected - never success.
/// </summary>
public sealed class AckParser : IAckParser
{
    private static readonly byte[] AckLetters = { 0x41, 0x43, 0x4B };
    private static readonly byte[] NakLetters = { 0x4E, 0x41, 0x4B };
    private static readonly byte[] AckFixed = { 0x06, 0x30, 0x30, 0x30 };

    private readonly byte _ack;
    private readonly byte _nak;

    public AckParser(byte ack, byte nak) { _ack = ack; _nak = nak; }

    public AckResult Parse(ReadOnlySpan<byte> received)
    {
        var raw = received.ToArray();
        if (raw.Length == 0) return new AckResult(AckKind.Incomplete, raw);
        if (raw.Length == 1 && raw[0] == _ack) return new AckResult(AckKind.Ack, raw);
        if (raw.AsSpan().SequenceEqual(AckLetters)) return new AckResult(AckKind.Ack, raw);
        if (_ack == 0x06 && raw.AsSpan().SequenceEqual(AckFixed)) return new AckResult(AckKind.Ack, raw);
        if (raw[0] == _nak && raw.Length <= 4) return new AckResult(AckKind.Nak, raw);
        if (raw.AsSpan().SequenceEqual(NakLetters)) return new AckResult(AckKind.Nak, raw);
        return new AckResult(AckKind.Unexpected, raw);
    }
}

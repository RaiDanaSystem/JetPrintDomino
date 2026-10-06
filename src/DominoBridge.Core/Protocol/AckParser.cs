namespace DominoBridge.Core.Protocol;

/// <summary>Seam for alternative ack types (e.g. BufferDepth, UNKNOWN / not implemented in v1).</summary>
public interface IAckParser
{
    AckResult Parse(ReadOnlySpan<byte> received);
}

/// <summary>
/// "DefaultSpecialChars" ack type: a single ACK byte (default 0x06) after parsing.
/// Anything that is not exactly one known byte is reported as Unexpected - never as success.
/// </summary>
public sealed class AckParser : IAckParser
{
    private readonly byte _ack;
    private readonly byte _nak;

    public AckParser(byte ack, byte nak) { _ack = ack; _nak = nak; }

    public AckResult Parse(ReadOnlySpan<byte> received)
    {
        var raw = received.ToArray();
        if (raw.Length == 0) return new AckResult(AckKind.Incomplete, raw);
        if (raw.Length == 1)
        {
            if (raw[0] == _ack) return new AckResult(AckKind.Ack, raw);
            if (raw[0] == _nak) return new AckResult(AckKind.Nak, raw);
        }
        return new AckResult(AckKind.Unexpected, raw);
    }
}

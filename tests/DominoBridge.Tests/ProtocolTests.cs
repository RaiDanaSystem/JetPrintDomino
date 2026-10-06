using DominoBridge.Core.Configuration;
using DominoBridge.Core.Protocol;
using Xunit;

namespace DominoBridge.Tests;

public class ProtocolTests
{
    private static EdcProtocol P(Action<PrinterSettings>? tweak = null)
    {
        var s = new PrinterSettings();
        tweak?.Invoke(s);
        return new EdcProtocol(s);
    }

    [Fact]
    public void Packet_matches_spec_example_TEST123()
    {
        var bytes = P().BuildDataPacket("TEST123");
        Assert.Equal("02 54 45 53 54 31 32 33 03", EdcProtocol.ToHex(bytes));
    }

    [Fact]
    public void Packet_matches_spec_example_row_with_three_fields()
    {
        var p = P();
        var bytes = p.BuildPacket(new[] { "12345", "A001", "1405/07/14" });
        Assert.Equal("02 31 32 33 34 35 2C 41 30 30 31 2C 31 34 30 35 2F 30 37 2F 31 34 03", EdcProtocol.ToHex(bytes));
    }

    [Fact]
    public void Markers_are_real_bytes_not_text()
    {
        var bytes = P().BuildDataPacket("X");
        Assert.Equal(0x02, bytes[0]);
        Assert.Equal(0x03, bytes[^1]);
        Assert.Equal(3, bytes.Length);
    }

    [Fact]
    public void Markers_and_delimiter_come_from_configuration()
    {
        var p = P(s => { s.StartMarker = 0x7B; s.EndMarker = 0x7D; s.Delimiter = "|"; });
        var bytes = p.BuildPacket(new[] { "a", "b" });
        Assert.Equal(new byte[] { 0x7B, (byte)'a', (byte)'|', (byte)'b', 0x7D }, bytes);
    }

    [Fact]
    public void Field_containing_delimiter_is_rejected()
    {
        Assert.Throws<EdcProtocolException>(() => P().BuildDataString(new[] { "a,b", "c" }));
    }

    [Fact]
    public void Data_containing_marker_is_rejected()
    {
        Assert.Throws<EdcProtocolException>(() => P().BuildDataPacket("ab\u0003cd"));
    }

    [Fact]
    public void Ascii_rejects_persian_instead_of_sending_question_marks()
    {
        Assert.Throws<EdcProtocolException>(() => P().BuildDataPacket("سلام"));
    }

    [Fact]
    public void Utf8_encodes_persian()
    {
        var bytes = P(s => s.Encoding = "UTF-8").BuildDataPacket("س");
        Assert.Equal(new byte[] { 0x02, 0xD8, 0xB3, 0x03 }, bytes);
    }

    [Fact]
    public void Utf16_encodings_are_supported_with_warning()
    {
        var p = P(s => s.Encoding = "UnicodeBigEndian");
        Assert.Equal(new byte[] { 0x02, 0x00, 0x41, 0x03 }, p.BuildDataPacket("A"));
        Assert.Contains(p.ValidateConfiguration(), i => i.Severity == IssueSeverity.Warning);
        var le = P(s => s.Encoding = "UnicodeLittleEndian").BuildDataPacket("A");
        Assert.Equal(new byte[] { 0x02, 0x41, 0x00, 0x03 }, le);
    }

    [Fact]
    public void Default_configuration_is_valid_without_warnings()
    {
        Assert.Empty(P().ValidateConfiguration());
    }

    [Theory]
    [InlineData("Port", 0)]
    [InlineData("Port", 70000)]
    [InlineData("StartMarker", 300)]
    [InlineData("AckTimeoutMs", 0)]
    public void Invalid_numbers_are_errors(string prop, int value)
    {
        var s = new PrinterSettings();
        typeof(PrinterSettings).GetProperty(prop)!.SetValue(s, value);
        Assert.Contains(new EdcProtocol(s).ValidateConfiguration(), i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void Same_markers_empty_delimiter_unknown_encoding_are_errors()
    {
        Assert.Contains(P(s => s.EndMarker = s.StartMarker).ValidateConfiguration(), i => i.Severity == IssueSeverity.Error);
        Assert.Contains(P(s => s.Delimiter = "").ValidateConfiguration(), i => i.Severity == IssueSeverity.Error);
        Assert.Contains(P(s => s.Encoding = "EBCDIC").ValidateConfiguration(), i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void Display_string_shows_marker_names()
    {
        Assert.Equal("<STX>12,3<ETX>", P().ToDisplayString("12,3"));
    }

    [Theory]
    [InlineData(new byte[] { }, AckKind.Incomplete)]
    [InlineData(new byte[] { 0x06 }, AckKind.Ack)]
    [InlineData(new byte[] { 0x15 }, AckKind.Nak)]
    [InlineData(new byte[] { 0x41 }, AckKind.Unexpected)]
    [InlineData(new byte[] { 0x06, 0x06 }, AckKind.Unexpected)]
    public void Ack_parsing(byte[] bytes, AckKind expected)
    {
        Assert.Equal(expected, P().ParseAck(bytes).Kind);
    }

    [Fact]
    public void Ack_byte_is_configurable()
    {
        var p = P(s => { s.AckByte = 0x41; s.NakByte = 0x42; });
        Assert.Equal(AckKind.Ack, p.ParseAck(new byte[] { 0x41 }).Kind);
        Assert.Equal(AckKind.Nak, p.ParseAck(new byte[] { 0x42 }).Kind);
        Assert.Equal(AckKind.Unexpected, p.ParseAck(new byte[] { 0x06 }).Kind);
    }
}

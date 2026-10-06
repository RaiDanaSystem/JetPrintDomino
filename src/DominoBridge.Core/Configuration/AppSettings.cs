using System.Text.Json;

namespace DominoBridge.Core.Configuration;

public sealed class PrinterSettings
{
    public string IpAddress { get; set; } = "192.168.1.55";
    public int Port { get; set; } = 16000;
    /// <summary>ASCII | UTF-8 | UnicodeBigEndian | UnicodeLittleEndian</summary>
    public string Encoding { get; set; } = "ASCII";
    public int StartMarker { get; set; } = 0x02;
    public int EndMarker { get; set; } = 0x03;
    public string Delimiter { get; set; } = ",";
    public int AckTimeoutMs { get; set; } = 3000;
    public int ConnectTimeoutMs { get; set; } = 5000;
    /// <summary>Default ACK for "DefaultSpecialChars" ack type.</summary>
    public int AckByte { get; set; } = 0x06;
    /// <summary>
    /// UNKNOWN / REQUIRES DEVICE TEST: the NAK byte is not stated in the specification.
    /// 0x15 (ASCII NAK) is only a placeholder and must be verified against the device / EDC manual.
    /// </summary>
    public int NakByte { get; set; } = 0x15;
}

public sealed class QueueSettings
{
    /// <summary>Max rows pushed into the printer buffer before waiting for the operator.</summary>
    public int BatchSize { get; set; } = 10;
    /// <summary>"Maximum Queue Size" configured on the printer (default 32).</summary>
    public int MaxQueueSize { get; set; } = 32;
    /// <summary>Automatic retries, only ever applied when the row is known NOT to have been sent.</summary>
    public int MaxRetry { get; set; } = 0;
    public int ReconnectIntervalMs { get; set; } = 5000;
    /// <summary>
    /// Keep feeding the printer buffer without operator clicks. A NAK is treated as "buffer full / not accepted"
    /// (the row was NOT taken, so re-sending it is duplicate-safe) and the same row is retried after AutoRefillDelayMs.
    /// </summary>
    public bool AutoRefill { get; set; } = true;
    public int AutoRefillDelayMs { get; set; } = 1000;
    /// <summary>Consecutive NAKs for the same row after which sending halts (guards against a real data error).</summary>
    public int AutoRefillMaxConsecutiveNaks { get; set; } = 600;
}

public sealed class MappingEntry
{
    public string Header { get; set; } = "";
    /// <summary>Null/empty = column not sent.</summary>
    public int? EdcIndex { get; set; }
}

public sealed class ExcelSettings
{
    public string LastFile { get; set; } = "";
    /// <summary>Headers the saved mapping belongs to; the mapping is only reused if they are unchanged.</summary>
    public List<string> Headers { get; set; } = new();
    public List<MappingEntry> Mapping { get; set; } = new();
}

public sealed class AppSettings
{
    public PrinterSettings Printer { get; set; } = new();
    public QueueSettings Queue { get; set; } = new();
    public ExcelSettings Excel { get; set; } = new();
    public bool DebugHex { get; set; } = true;
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DominoAx150iBridge");

    public static string DefaultPath => Path.Combine(DefaultDirectory, "settings.json");

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings();
        }
        catch (Exception)
        {
            try { File.Move(path, path + ".corrupt", true); } catch { /* ignore */ }
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
        File.Move(tmp, path, true);
    }
}

using System.Globalization;
using System.Windows.Data;

namespace DominoBridge.App.Converters;

/// <summary>int 2 &lt;-&gt; "0x02" (input is read as hex).</summary>
public sealed class HexByteConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int i ? $"0x{i:X2}" : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var t = (value as string ?? "").Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        return int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) && v is >= 0 and <= 255
            ? v : Binding.DoNothing;
    }
}

/// <summary>"\t" &lt;-&gt; "TAB".</summary>
public sealed class DelimiterConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value as string == "\t" ? "TAB" : value as string ?? "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var t = value as string ?? "";
        return t.Equals("TAB", StringComparison.OrdinalIgnoreCase) ? "\t" : t;
    }
}

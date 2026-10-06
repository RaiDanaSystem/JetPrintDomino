using System.Globalization;
using ClosedXML.Excel;

namespace DominoBridge.Core.Excel;

/// <summary>
/// Reads .xlsx without Microsoft Excel. The first used row is the header row.
/// Text cells are returned verbatim (leading zeros kept); numeric/date cells use the value as Excel displays it.
/// Row order is preserved; completely empty rows are skipped.
/// </summary>
public static class ExcelReader
{
    private static XLWorkbook Open(string path)
    {
        try
        {
            // FileShare.ReadWrite so a file that is still open in Excel can be read.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var ms = new MemoryStream();
            fs.CopyTo(ms);
            ms.Position = 0;
            return new XLWorkbook(ms);
        }
        catch (Exception ex) when (ex is not ExcelReadException)
        {
            throw new ExcelReadException($"Cannot read '{path}': {ex.Message}", ex);
        }
    }

    public static IReadOnlyList<string> GetSheetNames(string path)
    {
        using var wb = Open(path);
        return wb.Worksheets.Select(w => w.Name).ToList();
    }

    public static ExcelTable Load(string path, string? sheetName = null)
    {
        using var wb = Open(path);
        IXLWorksheet ws;
        try { ws = sheetName == null ? wb.Worksheet(1) : wb.Worksheet(sheetName); }
        catch (Exception ex) { throw new ExcelReadException("Worksheet not found: " + ex.Message, ex); }

        var used = ws.RangeUsed(XLCellsUsedOptions.Contents);
        if (used == null) throw new ExcelReadException("The worksheet is empty.");

        int firstRow = used.FirstRow().RowNumber();
        int lastRow = used.LastRow().RowNumber();
        int firstCol = used.FirstColumn().ColumnNumber();
        int lastCol = used.LastColumn().ColumnNumber();

        var headers = new string[lastCol - firstCol + 1];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int c = firstCol; c <= lastCol; c++)
        {
            var cell = ws.Cell(firstRow, c);
            var h = CellText(cell).Trim();
            if (h.Length == 0) h = "Column " + cell.Address.ColumnLetter;
            if (!seen.Add(h)) { h = $"{h} ({cell.Address.ColumnLetter})"; seen.Add(h); }
            headers[c - firstCol] = h;
        }

        var rows = new List<ExcelRowData>();
        for (int r = firstRow + 1; r <= lastRow; r++)
        {
            var cells = new string[headers.Length];
            bool any = false;
            for (int c = firstCol; c <= lastCol; c++)
            {
                var t = CellText(ws.Cell(r, c));
                cells[c - firstCol] = t;
                if (t.Length > 0) any = true;
            }
            if (any) rows.Add(new ExcelRowData(r, cells));
        }
        return new ExcelTable(ws.Name, headers, rows);
    }

    internal static string CellText(IXLCell c)
    {
        if (c.IsEmpty()) return "";
        switch (c.DataType)
        {
            case XLDataType.Text:
                return c.GetString();
            case XLDataType.Number:
                var d = c.GetDouble();
                if (c.Style.NumberFormat.NumberFormatId == 0 && c.Style.NumberFormat.Format is "" or "General")
                {
                    // General format: avoid Excel's scientific display for long integers.
                    if (d == Math.Floor(d) && Math.Abs(d) < 1e15) return ((long)d).ToString(CultureInfo.InvariantCulture);
                    return d.ToString("R", CultureInfo.InvariantCulture);
                }
                return c.GetFormattedString();
            default:
                return c.GetFormattedString();
        }
    }
}

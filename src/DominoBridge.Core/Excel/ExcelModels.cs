namespace DominoBridge.Core.Excel;

public sealed record ExcelRowData(int RowNumber, string[] Cells);

public sealed class ExcelTable
{
    public ExcelTable(string sheetName, string[] headers, List<ExcelRowData> rows)
    {
        SheetName = sheetName; Headers = headers; Rows = rows;
    }
    public string SheetName { get; }
    public string[] Headers { get; }
    public List<ExcelRowData> Rows { get; }
}

public sealed class ExcelReadException : Exception
{
    public ExcelReadException(string message, Exception? inner = null) : base(message, inner) { }
}

using ClosedXML.Excel;
using DominoBridge.Core.Configuration;
using DominoBridge.Core.Excel;
using DominoBridge.Core.Protocol;
using DominoBridge.Core.Queue;
using Xunit;

namespace DominoBridge.Tests;

public class ExcelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory().FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    public string MakeWorkbook(Action<IXLWorksheet> fill)
    {
        var path = Path.Combine(_dir, Guid.NewGuid() + ".xlsx");
        using var wb = new XLWorkbook();
        fill(wb.AddWorksheet("Sheet1"));
        wb.SaveAs(path);
        return path;
    }

    public string StandardFile() => MakeWorkbook(ws =>
    {
        ws.Cell(1, 1).Value = "ProductCode"; ws.Cell(1, 2).Value = "Serial"; ws.Cell(1, 3).Value = "Date";
        ws.Cell(2, 1).Value = "001245"; ws.Cell(2, 2).Value = "A001"; ws.Cell(2, 3).Value = "1405/07/14";
        ws.Cell(3, 1).Value = 12346;    ws.Cell(3, 2).Value = "A002"; ws.Cell(3, 3).Value = "1405/07/14";
        // row 4 intentionally empty
        ws.Cell(5, 1).Value = "12347";  ws.Cell(5, 2).Value = "A003"; ws.Cell(5, 3).Value = "1405/07/14";
    });

    [Fact]
    public void Reads_headers_rows_in_order_and_skips_empty_rows()
    {
        var t = ExcelReader.Load(StandardFile());
        Assert.Equal(new[] { "ProductCode", "Serial", "Date" }, t.Headers);
        Assert.Equal(new[] { 2, 3, 5 }, t.Rows.Select(r => r.RowNumber));
    }

    [Fact]
    public void Preserves_leading_zeros_and_text_numbers()
    {
        var t = ExcelReader.Load(StandardFile());
        Assert.Equal("001245", t.Rows[0].Cells[0]);
        Assert.Equal("12346", t.Rows[1].Cells[0]);
        Assert.Equal("12347", t.Rows[2].Cells[0]);
    }

    [Fact]
    public void Large_general_numbers_are_not_turned_into_scientific_notation()
    {
        var f = MakeWorkbook(ws => { ws.Cell(1, 1).Value = "Code"; ws.Cell(2, 1).Value = 6260123456789.0; });
        Assert.Equal("6260123456789", ExcelReader.Load(f).Rows[0].Cells[0]);
    }

    [Fact]
    public void Duplicate_and_blank_headers_are_made_unique()
    {
        var f = MakeWorkbook(ws => { ws.Cell(1, 1).Value = "A"; ws.Cell(1, 2).Value = "A"; ws.Cell(1, 3).Value = ""; ws.Cell(2, 1).Value = "1"; ws.Cell(2, 2).Value = "2"; ws.Cell(2, 3).Value = "3"; });
        var h = ExcelReader.Load(f).Headers;
        Assert.Equal(3, h.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Empty_sheet_throws_readable_error()
    {
        var f = MakeWorkbook(_ => { });
        Assert.Throws<ExcelReadException>(() => ExcelReader.Load(f));
    }

    [Fact]
    public void Missing_file_throws_readable_error()
    {
        Assert.Throws<ExcelReadException>(() => ExcelReader.Load(Path.Combine(_dir, "nope.xlsx")));
    }

    [Fact]
    public void Excel_to_edc_mapping_produces_spec_packets_in_order()
    {
        var t = ExcelReader.Load(StandardFile());
        var map = new[] { new FieldMapping(0, 0), new FieldMapping(1, 1), new FieldMapping(2, 2) };
        var q = PrintQueue.FromExcel(t, map, new EdcProtocol(new PrinterSettings()));
        Assert.Equal(new[] { "001245,A001,1405/07/14", "12346,A002,1405/07/14", "12347,A003,1405/07/14" }, q.Rows.Select(r => r.DataString));
        Assert.Equal("<STX>001245,A001,1405/07/14<ETX>", q.Rows[0].DisplayPayload);
        Assert.Equal(new[] { 2, 3, 5 }, q.Rows.Select(r => r.ExcelRowId));
    }

    [Fact]
    public void Mapping_can_reorder_and_skip_columns()
    {
        var t = ExcelReader.Load(StandardFile());
        var map = new[] { new FieldMapping(2, 0), new FieldMapping(0, 1) };
        var q = PrintQueue.FromExcel(t, map, new EdcProtocol(new PrinterSettings()));
        Assert.Equal("1405/07/14,001245", q.Rows[0].DataString);
    }

    [Fact]
    public void Mapping_validation()
    {
        Assert.False(ColumnMapper.Validate(Array.Empty<FieldMapping>(), 3).IsValid);
        Assert.False(ColumnMapper.Validate(new[] { new FieldMapping(0, 0), new FieldMapping(1, 0) }, 3).IsValid);
        Assert.False(ColumnMapper.Validate(new[] { new FieldMapping(5, 0) }, 3).IsValid);
        var gap = ColumnMapper.Validate(new[] { new FieldMapping(0, 0), new FieldMapping(1, 2) }, 3);
        Assert.True(gap.IsValid);
        Assert.Single(gap.Warnings);
        Assert.Equal(new[] { "a", "", "b" }, ColumnMapper.MapRow(new ExcelRowData(2, new[] { "a", "b" }), new[] { new FieldMapping(0, 0), new FieldMapping(1, 2) }));
    }

    [Fact]
    public void Invalid_rows_become_Failed_and_never_get_a_data_string()
    {
        var f = MakeWorkbook(ws =>
        {
            ws.Cell(1, 1).Value = "A"; ws.Cell(1, 2).Value = "B";
            ws.Cell(2, 1).Value = "ok"; ws.Cell(2, 2).Value = "fine";
            ws.Cell(3, 1).Value = "has,comma"; ws.Cell(3, 2).Value = "x";
            ws.Cell(4, 1).Value = "سلام"; ws.Cell(4, 2).Value = "x";
        });
        var q = PrintQueue.FromExcel(ExcelReader.Load(f), new[] { new FieldMapping(0, 0), new FieldMapping(1, 1) }, new EdcProtocol(new PrinterSettings()));
        Assert.Equal(new[] { RowStatus.Pending, RowStatus.Failed, RowStatus.Failed }, q.Rows.Select(r => r.Status));
        Assert.Null(q.Rows[1].DataString);
        Assert.Contains("Not sent", q.Rows[1].Message);
    }
}

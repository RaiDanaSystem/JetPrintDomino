namespace DominoBridge.Core.Excel;

/// <summary>Maps a 0-based table column to a 0-based EDC delimiter index.</summary>
public sealed record FieldMapping(int ColumnIndex, int EdcIndex);

public sealed record MappingValidation(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;
}

public static class ColumnMapper
{
    public static MappingValidation Validate(IReadOnlyList<FieldMapping> mapping, int columnCount)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        if (mapping.Count == 0) { errors.Add("No column is mapped."); return new(errors, warnings); }

        foreach (var m in mapping)
        {
            if (m.EdcIndex < 0) errors.Add($"Negative EDC index for column {m.ColumnIndex + 1}.");
            if (m.ColumnIndex < 0 || m.ColumnIndex >= columnCount) errors.Add($"Column {m.ColumnIndex + 1} does not exist.");
        }
        foreach (var g in mapping.GroupBy(m => m.EdcIndex).Where(g => g.Count() > 1))
            errors.Add($"EDC index {g.Key} is used by more than one column.");

        if (errors.Count == 0)
        {
            var used = mapping.Select(m => m.EdcIndex).ToHashSet();
            int max = used.Max();
            var gaps = Enumerable.Range(0, max + 1).Where(i => !used.Contains(i)).ToList();
            if (gaps.Count > 0)
                warnings.Add($"EDC index gap(s): {string.Join(", ", gaps)} will be sent as empty values.");
        }
        return new(errors, warnings);
    }

    /// <summary>Returns the ordered field values for one row (index = EDC index). Values are not modified.</summary>
    public static string[] MapRow(ExcelRowData row, IReadOnlyList<FieldMapping> mapping)
    {
        int size = mapping.Max(m => m.EdcIndex) + 1;
        var fields = new string[size];
        Array.Fill(fields, "");
        foreach (var m in mapping)
            fields[m.EdcIndex] = m.ColumnIndex < row.Cells.Length ? row.Cells[m.ColumnIndex] : "";
        return fields;
    }
}

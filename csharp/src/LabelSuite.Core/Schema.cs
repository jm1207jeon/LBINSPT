// 검사 목록 표준 스키마 — 컬럼 계약의 단일 원천 (파이썬 core/schema.py 포팅).
using System.Text.RegularExpressions;
using ClosedXML.Excel;

namespace LabelSuite.Core;

public class SchemaException(string message) : Exception(message);

public sealed record LabelRecord(
    string Lot, string Products, string Pn, string Ref,
    string MfgDate, string ExpDate, string Gtin, string? Standard = null)
{
    public string Field(string column) => column switch
    {
        "LOT" => Lot, "PRODUCTS" => Products, "PN" => Pn, "REF" => Ref,
        "MFG DATE" => MfgDate, "EXP DATE" => ExpDate, "GTIN" => Gtin,
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, null),
    };
}

public static class Schema
{
    public static readonly string[] CanonicalColumns =
        ["LOT", "PRODUCTS", "PN", "REF", "MFG DATE", "EXP DATE", "GTIN"];

    public const string StandardColumn = "STANDARD";
    public const string SheetName = "Label Inspection List";
    public const string DateFormatDefault = "yyyy-MM-dd";

    private static readonly Regex FloatArtifact = new(@"^\d+\.0+$", RegexOptions.Compiled);
    private static readonly Regex Scientific = new(@"^[\d.]+[eE]\+?\d+$", RegexOptions.Compiled);
    private static readonly Regex Gs1Paren = new(@"^\(01\)(\d{1,14})$", RegexOptions.Compiled);
    private static readonly Regex Gs1Bare = new(@"^01(\d{14})$", RegexOptions.Compiled);
    private static readonly Regex NonDigit = new(@"\D", RegexOptions.Compiled);

    /// <summary>GTIN 정규화의 유일한 규칙: 숫자만 남겨 14자리 zero-pad.</summary>
    public static string NormalizeGtin14(object? raw)
    {
        if (raw is null) return "";
        var text = Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture)?.Trim() ?? "";
        if (text.Length == 0 || text.Equals("nan", StringComparison.OrdinalIgnoreCase)
            || text.Equals("none", StringComparison.OrdinalIgnoreCase)) return "";
        if (FloatArtifact.IsMatch(text)) text = text.Split('.')[0];
        else if (Scientific.IsMatch(text))
            text = ((long)double.Parse(text, System.Globalization.CultureInfo.InvariantCulture)).ToString();
        var m = Gs1Paren.Match(text);
        if (!m.Success) m = Gs1Bare.Match(text);
        if (m.Success) text = m.Groups[1].Value;
        var digits = NonDigit.Replace(text, "");
        if (digits.Length == 0) return "";
        if (digits.Length > 14) throw new SchemaException($"GTIN이 14자리를 초과합니다: {raw}");
        return digits.PadLeft(14, '0');
    }

    /// <summary>규격별 날짜 포맷 폴백 규칙 (standards.json의 date_format이 우선).</summary>
    public static string DateFormatFor(string? standard) =>
        standard == "BSC" ? "yyyy.MM.dd" : DateFormatDefault;

    private static string CleanCell(object? value)
    {
        var text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)?.Trim() ?? "";
        return text.Equals("nan", StringComparison.OrdinalIgnoreCase)
            || text.Equals("none", StringComparison.OrdinalIgnoreCase) ? "" : text;
    }

    /// <summary>7컬럼(레거시) 또는 8컬럼 목록 파일 로드. GTIN은 GTIN-14로 정규화.</summary>
    public static (List<LabelRecord> Records, List<string> Warnings) LoadInspectionList(string path)
    {
        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheets.Contains(SheetName)
            ? workbook.Worksheet(SheetName) : workbook.Worksheets.First();
        var rows = sheet.RowsUsed().ToList();
        if (rows.Count == 0) throw new SchemaException("목록 파일이 비어 있습니다.");

        var header = rows[0].CellsUsed().Select(c => CleanCell(c.Value)).ToList();
        var missing = CanonicalColumns.Where(c => !header.Contains(c)).ToList();
        if (missing.Count > 0)
            throw new SchemaException(
                $"목록 파일의 컬럼이 올바르지 않습니다. 누락: {string.Join(", ", missing)}");
        var index = CanonicalColumns.ToDictionary(c => c, c => header.IndexOf(c) + 1);
        var hasStandard = header.Contains(StandardColumn);
        var standardIndex = hasStandard ? header.IndexOf(StandardColumn) + 1 : -1;

        var records = new List<LabelRecord>();
        var warnings = new List<string>();
        foreach (var row in rows.Skip(1))
        {
            string Cell(string name) => CleanCell(row.Cell(index[name]).GetFormattedString());
            var all = CanonicalColumns.Select(Cell).ToList();
            if (all.All(string.IsNullOrEmpty)) continue;
            string gtin;
            try { gtin = NormalizeGtin14(Cell("GTIN")); }
            catch (SchemaException ex)
            {
                warnings.Add($"{row.RowNumber()}행: {ex.Message} — 원본 값을 유지합니다.");
                gtin = Cell("GTIN");
            }
            var standard = hasStandard ? CleanCell(row.Cell(standardIndex).GetFormattedString()) : "";
            records.Add(new LabelRecord(
                Cell("LOT"), Cell("PRODUCTS"), Cell("PN"), Cell("REF"),
                Cell("MFG DATE"), Cell("EXP DATE"), gtin,
                standard.Length > 0 ? standard : null));
        }
        if (records.Count == 0) warnings.Add("목록에 데이터 행이 없습니다.");
        return (records, warnings);
    }

    /// <summary>목록 저장. GTIN 셀은 텍스트 서식으로 선행 0 보존.</summary>
    public static void SaveInspectionList(IEnumerable<LabelRecord> records, string path,
                                          bool includeStandard = true)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet(SheetName);
        var header = CanonicalColumns.ToList();
        if (includeStandard) header.Add(StandardColumn);
        for (var i = 0; i < header.Count; i++) sheet.Cell(1, i + 1).Value = header[i];

        var gtinColumn = Array.IndexOf(CanonicalColumns, "GTIN") + 1;
        var rowNumber = 2;
        foreach (var record in records)
        {
            var values = new List<string>
                { record.Lot, record.Products, record.Pn, record.Ref,
                  record.MfgDate, record.ExpDate, record.Gtin };
            if (includeStandard) values.Add(record.Standard ?? "");
            for (var i = 0; i < values.Count; i++)
            {
                var cell = sheet.Cell(rowNumber, i + 1);
                if (i + 1 == gtinColumn) cell.Style.NumberFormat.Format = "@";
                cell.SetValue(values[i]);
            }
            rowNumber++;
        }
        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents();
        workbook.SaveAs(path);
    }
}

// LOT별 검사 리포트 xlsx 내보내기 (파이썬 core/history/report.py 포팅).
using ClosedXML.Excel;

namespace LabelSuite.Core;

public static class Report
{
    public static int ExportLotReport(HistoryDb db, string lot, string outPath)
    {
        var rows = db.Query(lot: lot, limit: 10_000);
        using var workbook = new XLWorkbook();

        var summary = workbook.AddWorksheet("요약");
        var passedCount = rows.Count(r => r.Passed);
        var summaryRows = new (string, string)[]
        {
            ("LOT", lot),
            ("검사 건수", rows.Count.ToString()),
            ("합격", passedCount.ToString()),
            ("확인 필요", (rows.Count - passedCount).ToString()),
            ("합격률", rows.Count > 0 ? $"{passedCount * 100.0 / rows.Count:F1}%" : "-"),
        };
        for (var i = 0; i < summaryRows.Length; i++)
        {
            summary.Cell(i + 1, 1).SetValue(summaryRows[i].Item1).Style.Font.SetBold();
            summary.Cell(i + 1, 2).SetValue(summaryRows[i].Item2);
        }

        var detail = workbook.AddWorksheet("검사 상세");
        var header = new[] { "일시", "LOT", "REF", "PN", "규격", "소스", "페이지",
                             "판정", "이미지 경로", "필드별 검출/기준" };
        for (var i = 0; i < header.Length; i++)
            detail.Cell(1, i + 1).SetValue(header[i]).Style.Font.SetBold();
        var rowNumber = 2;
        foreach (var row in rows)
        {
            var fields = db.FieldsFor(row.Id);
            var fieldSummary = string.Join(", ", fields
                .Where(f => f.Expected is not null)
                .Select(f => $"{f.Field} {f.Found}/{f.Expected}"));
            var values = new[]
            {
                row.Ts, row.Lot, row.Ref, row.Pn, row.Standard, row.Source,
                row.Page is { } p ? (p + 1).ToString() : "",
                row.Passed ? "합격" : "확인 필요", row.ImagePath, fieldSummary,
            };
            for (var i = 0; i < values.Length; i++)
                detail.Cell(rowNumber, i + 1).SetValue(values[i]);
            detail.Cell(rowNumber, 8).Style.Fill.SetBackgroundColor(
                row.Passed ? XLColor.FromArgb(0x90, 0xEE, 0x90)
                           : XLColor.FromArgb(0xFF, 0xE4, 0xB5));
            rowNumber++;
        }
        summary.Columns().AdjustToContents();
        detail.Columns().AdjustToContents();
        workbook.SaveAs(outPath);
        return rows.Count;
    }
}

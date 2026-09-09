// 검사 결과 CSV — 품질 기록용 고정 열 구성. 모든 페이지(미검사 포함)를 한 행씩 내보내
// "보지 않은 페이지"가 산출물에서 드러나게 한다. AUTO 열은 프로그램의 자동 판정(참고값)이며
// 최종 판정은 검사자가 한다.
using System.Globalization;

namespace LabelSuite.Core;

public static class InspectionCsv
{
    public const string Header =
        "PAGE,LOT,PN,REF,GTIN,MFG_DATE,EXP_DATE,STANDARD,AUTO(자동판정),INSPECTOR(검사자처리),FINAL(최종)," +
        "FIELD_LOT,FIELD_PN,FIELD_REF,FIELD_MFG,FIELD_EXP,FIELD_GTIN,BARCODES," +
        "INSPECTOR_NOTE,INSPECTOR_BY,IMAGE_PATH,PDF_FILE,EXPORTED_AT,APP_VERSION";

    public static int ColumnCount => Header.Split(',').Length;

    public const string AutoMatch = "MATCH";
    public const string AutoCheck = "CHECK";
    public const string AutoUninspected = "UNINSPECTED";

    /// <summary>한 페이지 → CSV 한 행. page는 0-based(표시는 page+1). o가 null이면 미검사 행
    /// (값 빈칸, AUTO=UNINSPECTED). textProtect=true면 LOT/PN/REF/GTIN/IMAGE_PATH를 ="값"으로
    /// 감싸 엑셀의 숫자 변환(선행 0 소실·지수 표기)을 막는다(settings export.csv_text_protect).</summary>
    /// <summary>INSPECTOR 열: 검사자 처리 없음="" / PASS / FAIL. FINAL 열: 검사자 처리가 있으면 그것, 없으면 AUTO.</summary>
    public static string Row(int page, InspectionOutcome? o, string? imagePath, string pdfPath,
                             DateTime at, string appVersion, bool textProtect = true,
                             InspectorVerdict? inspector = null)
    {
        Func<string, string> text = textProtect ? CsvUtil.TextField : CsvUtil.Field;
        var cells = new List<string> { (page + 1).ToString(CultureInfo.InvariantCulture) };
        if (o is null)
        {
            cells.AddRange(Enumerable.Repeat("", 7));          // LOT..STANDARD
            cells.Add(AutoUninspected);                          // AUTO
            cells.Add("");                                       // INSPECTOR
            cells.Add(AutoUninspected);                          // FINAL
            cells.AddRange(Enumerable.Repeat("", 7));          // FIELD_* + BARCODES
            cells.Add(""); cells.Add("");                        // INSPECTOR_NOTE, INSPECTOR_BY
        }
        else
        {
            var r = o.Record;
            cells.Add(text(r.Lot));
            cells.Add(text(r.Pn));
            cells.Add(text(r.Ref));
            cells.Add(text(r.Gtin));
            cells.Add(CsvUtil.Field(r.MfgDate));
            cells.Add(CsvUtil.Field(r.ExpDate));
            cells.Add(CsvUtil.Field(o.Standard.DisplayName));
            var auto = o.Passed ? AutoMatch : AutoCheck;
            cells.Add(auto);
            cells.Add(inspector is null ? "" : inspector.Passed ? "PASS" : "FAIL");
            cells.Add(inspector is null ? auto : inspector.Passed ? AutoMatch : AutoCheck);
            foreach (var field in new[] { "LOT", "PN", "REF", "MFG DATE", "EXP DATE", "GTIN" })
                cells.Add(FieldCell(o, field));
            cells.Add(CsvUtil.Field(string.Join(";", o.BarcodeChecks
                .Select(c => $"{c.Field}:{(c.Matched ? "OK" : "NG")}"))));
            cells.Add(CsvUtil.Field(inspector?.Note ?? ""));
            cells.Add(CsvUtil.Field(inspector?.By ?? ""));
        }
        cells.Add(text(imagePath ?? ""));
        cells.Add(CsvUtil.Field(pdfPath));
        cells.Add(at.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        cells.Add(CsvUtil.Field(appVersion));
        if (cells.Count != ColumnCount)
            throw new InvalidOperationException($"CSV 열 수 불일치: {cells.Count} != {ColumnCount}");
        return string.Join(",", cells);
    }

    /// <summary>'검출/기대' — 기대 개수가 없는(검사 대상 아님) 필드는 '-'.</summary>
    private static string FieldCell(InspectionOutcome o, string field) =>
        o.Fields.TryGetValue(field, out var f) && f.Expected is not null
            ? $"{f.Found}/{f.ExpectedDisplay}" : "-";

    /// <summary>헤더 + 행들을 CRLF로 결합 (UTF-8 BOM은 호출 측이 new UTF8Encoding(true)로).</summary>
    public static string Build(IEnumerable<(int page, InspectionOutcome? o, string? img)> rows,
                               string pdfPath, string appVersion, bool textProtect = true,
                               DateTime? at = null) =>
        Build(rows.Select(r => (r.page, r.o, r.img, (InspectorVerdict?)null)), pdfPath, appVersion,
              textProtect, at);

    /// <summary>검사자 처리(합격 확정)까지 포함한 내보내기.</summary>
    public static string Build(IEnumerable<(int page, InspectionOutcome? o, string? img, InspectorVerdict? inspector)> rows,
                               string pdfPath, string appVersion, bool textProtect = true,
                               DateTime? at = null)
    {
        var stamp = at ?? DateTime.Now;
        var lines = new List<string> { Header };
        foreach (var (page, o, img, inspector) in rows)
            lines.Add(Row(page, o, img, pdfPath, stamp, appVersion, textProtect, inspector));
        return string.Join("\r\n", lines);
    }
}

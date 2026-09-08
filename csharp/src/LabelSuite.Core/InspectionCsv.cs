// 검사 결과 CSV — 품질 기록용 고정 열 구성. 모든 페이지(미검사 포함)를 한 행씩 내보내
// "보지 않은 페이지"가 산출물에서 드러나게 한다. AUTO 열은 프로그램의 자동 판정(참고값)이며
// 최종 판정은 검사자가 한다.
using System.Globalization;

namespace LabelSuite.Core;

public static class InspectionCsv
{
    public const string Header =
        "PAGE,LOT,PN,REF,GTIN,MFG_DATE,EXP_DATE,STANDARD,AUTO(자동판정)," +
        "FIELD_LOT,FIELD_PN,FIELD_REF,FIELD_MFG,FIELD_EXP,FIELD_GTIN,BARCODES," +
        "IMAGE_PATH,PDF_FILE,EXPORTED_AT,APP_VERSION";

    public static int ColumnCount => Header.Split(',').Length;

    public const string AutoMatch = "MATCH";
    public const string AutoCheck = "CHECK";
    public const string AutoUninspected = "UNINSPECTED";

    /// <summary>한 페이지 → CSV 한 행. page는 0-based(표시는 page+1). o가 null이면 미검사 행
    /// (값 빈칸, AUTO=UNINSPECTED). textProtect=true면 LOT/PN/REF/GTIN/IMAGE_PATH를 ="값"으로
    /// 감싸 엑셀의 숫자 변환(선행 0 소실·지수 표기)을 막는다(settings export.csv_text_protect).</summary>
    public static string Row(int page, InspectionOutcome? o, string? imagePath, string pdfPath,
                             DateTime at, string appVersion, bool textProtect = true)
    {
        Func<string, string> text = textProtect ? CsvUtil.TextField : CsvUtil.Field;
        var cells = new string[ColumnCount];
        var i = 0;
        cells[i++] = (page + 1).ToString(CultureInfo.InvariantCulture);
        if (o is null)
        {
            for (; i < 16; i++) cells[i] = "";
            cells[8] = AutoUninspected;
        }
        else
        {
            var r = o.Record;
            cells[i++] = text(r.Lot);
            cells[i++] = text(r.Pn);
            cells[i++] = text(r.Ref);
            cells[i++] = text(r.Gtin);
            cells[i++] = CsvUtil.Field(r.MfgDate);
            cells[i++] = CsvUtil.Field(r.ExpDate);
            cells[i++] = CsvUtil.Field(o.Standard.DisplayName);
            cells[i++] = o.Passed ? AutoMatch : AutoCheck;
            foreach (var field in new[] { "LOT", "PN", "REF", "MFG DATE", "EXP DATE", "GTIN" })
                cells[i++] = FieldCell(o, field);
            cells[i++] = CsvUtil.Field(string.Join(";", o.BarcodeChecks
                .Select(c => $"{c.Field}:{(c.Matched ? "OK" : "NG")}")));
        }
        cells[i++] = text(imagePath ?? "");
        cells[i++] = CsvUtil.Field(pdfPath);
        cells[i++] = at.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        cells[i++] = CsvUtil.Field(appVersion);
        return string.Join(",", cells);
    }

    /// <summary>'검출/기대' — 기대 개수가 없는(검사 대상 아님) 필드는 '-'.</summary>
    private static string FieldCell(InspectionOutcome o, string field) =>
        o.Fields.TryGetValue(field, out var f) && f.Expected is { } expected
            ? $"{f.Found}/{expected}" : "-";

    /// <summary>헤더 + 행들을 CRLF로 결합 (UTF-8 BOM은 호출 측이 new UTF8Encoding(true)로).</summary>
    public static string Build(IEnumerable<(int page, InspectionOutcome? o, string? img)> rows,
                               string pdfPath, string appVersion, bool textProtect = true,
                               DateTime? at = null)
    {
        var stamp = at ?? DateTime.Now;
        var lines = new List<string> { Header };
        foreach (var (page, o, img) in rows)
            lines.Add(Row(page, o, img, pdfPath, stamp, appVersion, textProtect));
        return string.Join("\r\n", lines);
    }
}

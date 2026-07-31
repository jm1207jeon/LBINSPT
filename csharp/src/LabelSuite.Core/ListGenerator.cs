// 검사 목록 생성 코어 — 파이썬 core/list_generator.py 포팅 (버그 수정 포함).
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClosedXML.Excel;

namespace LabelSuite.Core;

public sealed record SheetSpec(string Sheet, IReadOnlyDictionary<string, int> Columns)
{
    public int Col(string name) => Columns[name];
}

public sealed record ColumnMaps(SheetSpec Schedule, SheetSpec Product, SheetSpec Bsc)
{
    public static ColumnMaps FromConfig(JsonObject raw)
    {
        SheetSpec Spec(string key)
        {
            var entry = raw[key]!.AsObject();
            var columns = new Dictionary<string, int>();
            foreach (var (name, value) in entry["columns"]!.AsObject())
                columns[name] = value!.GetValue<int>();
            return new SheetSpec(entry["sheet"]!.GetValue<string>(), columns);
        }
        return new ColumnMaps(Spec("schedule"), Spec("product"), Spec("bsc"));
    }
}

public sealed record RowIssue(int RowIndex, string Lot, string Severity, string Message);

public sealed class GenerationResult
{
    public List<LabelRecord> Records { get; } = [];
    public List<RowIssue> Issues { get; } = [];
    public List<DateOnly> SelectedDates { get; init; } = [];
    public int WarningCount => Issues.Count(i => i.Severity == "warning");
    public int ErrorCount => Issues.Count(i => i.Severity == "error");
}

/// <summary>입력 엑셀 시트를 0-기반 인덱스 접근 가능한 행 배열로 로드.</summary>
public sealed class InputFrame
{
    public List<object?[]> Rows { get; } = [];   // 데이터 행 (헤더 제외)
    public List<int> RowNumbers { get; } = [];   // 엑셀 실제 행 번호

    public static InputFrame Load(string path, SheetSpec spec)
    {
        using var workbook = new XLWorkbook(path);
        if (!workbook.Worksheets.Contains(spec.Sheet))
            throw new SchemaException($"시트 '{spec.Sheet}'를 찾을 수 없습니다: {Path.GetFileName(path)}");
        var sheet = workbook.Worksheet(spec.Sheet);
        var frame = new InputFrame();
        var maxColumn = spec.Columns.Values.Max() + 1;
        foreach (var row in sheet.RowsUsed().Skip(1))   // 첫 행은 헤더 (레거시와 동일)
        {
            var cells = new object?[maxColumn];
            for (var i = 0; i < maxColumn; i++)
            {
                var cell = row.Cell(i + 1);
                cells[i] = cell.Value.Type switch
                {
                    XLDataType.Blank => null,
                    XLDataType.DateTime => cell.GetDateTime(),
                    XLDataType.Number => cell.GetDouble(),
                    _ => cell.GetString(),
                };
            }
            frame.Rows.Add(cells);
            frame.RowNumbers.Add(row.RowNumber());
        }
        return frame;
    }
}

public static class ListGenerator
{
    /// <summary>유효기한 = 제조일 + 유효기간(개월) - 1일. AddMonths가 2/29를 월말로 보정.</summary>
    public static DateOnly ComputeExpDate(DateOnly mfg, int shelfLifeMonths = 36) =>
        mfg.AddMonths(shelfLifeMonths).AddDays(-1);

    private static readonly Regex DateOnlyRe = new(
        @"^(?:\d{4}-\d{1,2}-\d{1,2}|\d{1,2}/\d{1,2}/\d{4}|\d{4}/\d{1,2}/\d{1,2}" +
        @"|\d{1,2}-\d{1,2}-\d{4}|\d{4}\.\d{1,2}\.\d{1,2}|\d{1,2}\.\d{1,2}\.\d{4})" +
        @"(?:[ T]\d{2}:\d{2}(?::\d{2})?)?$", RegexOptions.Compiled);

    /// <summary>REF 셀 정리 — 명백한 날짜만 비우고 경고를 남긴다 (레거시 과잉 휴리스틱 제거).</summary>
    public static (string Ref, string? Warning) CleanRef(object? raw)
    {
        switch (raw)
        {
            case null: return ("", null);
            case DateTime dt: return ("", $"REF 셀이 날짜 값({dt:yyyy-MM-dd})이라 비웠습니다.");
            case double d when d == Math.Floor(d) && d is >= 40000 and <= 60000:
                return ("", $"REF 셀이 엑셀 날짜 시리얼({(long)d})로 보여 비웠습니다.");
            case double d:
                return (d == Math.Floor(d)
                    ? ((long)d).ToString(CultureInfo.InvariantCulture)
                    : d.ToString(CultureInfo.InvariantCulture), null);
        }
        var text = Convert.ToString(raw, CultureInfo.InvariantCulture)?.Trim() ?? "";
        if (text.Length == 0 || text is "nan" or "NaT") return ("", null);
        if (DateOnlyRe.IsMatch(text)) return ("", $"REF 셀이 날짜 문자열('{text}')이라 비웠습니다.");
        return (text, null);
    }

    private static DateOnly? CoerceDate(object? value) => value switch
    {
        DateTime dt => DateOnly.FromDateTime(dt),
        double d and >= 20000 and <= 80000 => DateOnly.FromDateTime(DateTime.FromOADate(d)),
        string s when DateTime.TryParse(s, CultureInfo.InvariantCulture,
                                        DateTimeStyles.None, out var parsed)
            => DateOnly.FromDateTime(parsed),
        _ => null,
    };

    public static List<DateOnly> ExtractAvailableDates(InputFrame schedule, ColumnMaps maps)
    {
        var column = maps.Schedule.Col("mfg");
        return schedule.Rows
            .Select(r => CoerceDate(r[column]))
            .Where(d => d is not null)
            .Select(d => d!.Value)
            .Distinct().OrderBy(d => d).ToList();
    }

    private static string? StandardForCountry(string country,
                                              IReadOnlyDictionary<string, string> map)
    {
        if (map.TryGetValue(country, out var standard))
            return string.IsNullOrEmpty(standard) ? null : standard;
        return map.TryGetValue("*", out var fallback) && !string.IsNullOrEmpty(fallback)
            ? fallback : null;
    }

    private static string Text(object? value) =>
        Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? "";

    public static GenerationResult Generate(
        InputFrame schedule, InputFrame? product, InputFrame? bsc,
        ISet<DateOnly> selectedDates, ColumnMaps maps,
        IReadOnlyDictionary<string, string> countryStandardMap,
        int shelfLifeMonths = 36)
    {
        var result = new GenerationResult
        { SelectedDates = selectedDates.OrderBy(d => d).ToList() };
        if (selectedDates.Count == 0) return result;

        var sched = maps.Schedule.Columns;

        // 조회 테이블 사전 구축 — PN은 문자열 비교
        var productGtin = new Dictionary<string, object?>();
        if (product is not null)
        {
            var pcols = maps.Product.Columns;
            foreach (var row in product.Rows)
            {
                var pn = Text(row[pcols["pn"]]);
                if (pn.Length > 0 && !productGtin.ContainsKey(pn))
                    productGtin[pn] = row[pcols["gtin"]];
            }
        }
        var bscByPn = new Dictionary<string, (object? Ref, object? Gtin)>();
        if (bsc is not null)
        {
            var bcols = maps.Bsc.Columns;
            foreach (var row in bsc.Rows)
            {
                var pn = Text(row[bcols["pn"]]);
                if (pn.Length > 0 && !bscByPn.ContainsKey(pn))
                    bscByPn[pn] = (row[bcols["ref"]], row[bcols["gtin"]]);
            }
        }

        var grouped = selectedDates.OrderBy(d => d)
            .ToDictionary(d => d, _ => new List<LabelRecord>());

        for (var i = 0; i < schedule.Rows.Count; i++)
        {
            var row = schedule.Rows[i];
            var excelRow = schedule.RowNumbers[i];
            try
            {
                var mfg = CoerceDate(row[sched["mfg"]]);
                if (mfg is null || !selectedDates.Contains(mfg.Value)) continue;

                var lot = Text(row[sched["lot"]]);
                var products = Text(row[sched["products"]]);
                var pn = Text(row[sched["pn"]]);
                var country = Text(row[sched["country"]]);

                var (refBase, refWarning) = CleanRef(row[sched["ref"]]);
                if (refWarning is not null)
                    result.Issues.Add(new RowIssue(excelRow, lot, "warning", refWarning));

                var standard = StandardForCountry(country, countryStandardMap);
                var dateFormat = standard == "BSC" ? "yyyy.MM.dd" : "yyyy-MM-dd";
                var exp = ComputeExpDate(mfg.Value, shelfLifeMonths);

                var isJapan = country == "일본";
                bscByPn.TryGetValue(pn, out var bscHit);
                var hasBscHit = bscByPn.ContainsKey(pn);

                // REF: 일본이면 BSC 우선, 미매칭은 스케줄 값 + 경고
                var refValue = refBase;
                if (isJapan)
                {
                    if (hasBscHit && Text(bscHit.Ref).Length > 0)
                        refValue = Text(bscHit.Ref);
                    else
                        result.Issues.Add(new RowIssue(excelRow, lot, "warning",
                            $"일본 행의 REF를 BSC 리스트에서 찾지 못해 스케줄 값('{refBase}')을 사용합니다."));
                }

                // GTIN: 일본은 BSC, 그 외는 품목리스트. 일본 미매칭 시 품목리스트 폴백.
                object? gtinRaw = null;
                if (isJapan)
                {
                    if (hasBscHit && bscHit.Gtin is not null && Text(bscHit.Gtin).Length > 0)
                        gtinRaw = bscHit.Gtin;
                    else if (productGtin.TryGetValue(pn, out var fallback))
                    {
                        gtinRaw = fallback;
                        result.Issues.Add(new RowIssue(excelRow, lot, "warning",
                            "일본 행의 GTIN을 BSC 리스트에서 찾지 못해 품목리스트 값으로 대체했습니다."));
                    }
                }
                else if (productGtin.TryGetValue(pn, out var value))
                {
                    gtinRaw = value;
                }

                string gtin;
                try { gtin = Schema.NormalizeGtin14(gtinRaw); }
                catch (SchemaException ex)
                {
                    gtin = Text(gtinRaw);
                    result.Issues.Add(new RowIssue(excelRow, lot, "warning",
                        $"GTIN 정규화 실패: {ex.Message}"));
                }
                if (gtin.Length == 0)
                    result.Issues.Add(new RowIssue(excelRow, lot, "warning",
                        "GTIN을 찾지 못했습니다. 입력 파일(품목/BSC 리스트)을 확인하세요."));

                grouped[mfg.Value].Add(new LabelRecord(
                    lot, products, pn, refValue,
                    mfg.Value.ToString(dateFormat, CultureInfo.InvariantCulture),
                    exp.ToString(dateFormat, CultureInfo.InvariantCulture),
                    gtin, standard));
            }
            catch (Exception ex)   // 행 단위 실패는 기록하고 계속 — 무단 소멸 금지
            {
                result.Issues.Add(new RowIssue(excelRow, "", "error", $"행 처리 실패: {ex.Message}"));
            }
        }

        foreach (var date in grouped.Keys.OrderBy(d => d))
            result.Records.AddRange(grouped[date]);
        return result;
    }
}

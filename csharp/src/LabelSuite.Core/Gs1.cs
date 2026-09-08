// GS1 AI 파서 — 순차 상태머신 (파이썬 core/barcode/gs1.py 포팅).
using System.Text.RegularExpressions;

namespace LabelSuite.Core;

public sealed record AiSpec(string Ai, int? FixedLen, int MaxLen, string Name);

public sealed record Gs1Element(string Ai, string Name, string Value);

public sealed class Gs1Message
{
    public List<Gs1Element> Elements { get; } = [];
    /// <summary>관대 파싱에서 건너뛴 미등록 AI 목록 (예: "91", "8012").</summary>
    public List<string> UnknownAis { get; } = [];
    /// <summary>미등록 AI를 건너뛰어 일부만 해석했으면 true — 등록된 AI(01/10/17 등)는 정상 대조 가능.</summary>
    public bool Partial => UnknownAis.Count > 0;
    public string? Get(string ai) => Elements.FirstOrDefault(e => e.Ai == ai)?.Value;
}

public class Gs1ParseException(string message, int position)
    : Exception($"{message} (위치 {position})")
{
    public int Position { get; } = position;
}

public static class Gs1
{
    public const char GS = '\x1d';

    public static readonly IReadOnlyDictionary<string, AiSpec> AiTable =
        new Dictionary<string, AiSpec>
        {
            ["00"] = new("00", 18, 18, "SSCC"),
            ["01"] = new("01", 14, 14, "GTIN"),
            ["02"] = new("02", 14, 14, "CONTENT GTIN"),
            ["10"] = new("10", null, 20, "LOT"),
            ["11"] = new("11", 6, 6, "PROD DATE"),
            ["15"] = new("15", 6, 6, "BEST BEFORE"),
            ["17"] = new("17", 6, 6, "EXP DATE"),
            ["21"] = new("21", null, 20, "SERIAL"),
            ["240"] = new("240", null, 30, "ADDITIONAL ID"),
            ["30"] = new("30", null, 8, "VAR COUNT"),
            ["310"] = new("310", 7, 7, "NET WEIGHT KG"),
        };

    private static readonly Regex ParenAi = new(@"\(([0-9]{2,4})\)", RegexOptions.Compiled);

    private static string StripSymbologyPrefix(string payload) =>
        payload.StartsWith(']') && payload.Length >= 3 ? payload[3..] : payload;

    /// <summary>사람이 읽는 '(01)...(10)...' 표기를 FNC1 표기로 변환. 괄호로 명시된 AI는
    /// parenAis에 모아 관대 파싱에서 미등록 AI 길이를 판단하는 데 쓴다.</summary>
    private static string NormalizeParenthesized(string payload, HashSet<string> parenAis)
    {
        if (!payload.Contains('(')) return payload;
        var parts = ParenAi.Split(payload);
        var result = new System.Text.StringBuilder();
        for (var i = 1; i < parts.Length; i += 2)
        {
            var ai = parts[i];
            parenAis.Add(ai);
            var value = i + 1 < parts.Length ? parts[i + 1] : "";
            result.Append(ai).Append(value).Append(GS);
        }
        return result.ToString();
    }

    private static AiSpec? MatchAi(string payload, int pos)
    {
        foreach (var length in new[] { 4, 3, 2 })
        {
            if (pos + length > payload.Length) continue;
            var candidate = payload.Substring(pos, length);
            if (AiTable.TryGetValue(candidate, out var spec)) return spec;
            // 소수점 지시 자릿수를 가진 AI(310x 등)는 3자리 접두로 매칭
            if (length == 4 && AiTable.TryGetValue(candidate[..3], out var prefix)
                && prefix.FixedLen is not null)
                return new AiSpec(candidate, prefix.FixedLen, prefix.MaxLen, prefix.Name);
        }
        return null;
    }

    /// <summary>GS1 메시지 파싱. tolerant=true(기본)면 AI 표에 없는 AI(예: (91) 사내 코드, (8012) 버전)를
    /// 다음 GS 구분자까지 건너뛰고 UnknownAis에 기록해 계속 해석한다 — 등록된 AI(01/10/17)는 정상 대조되며
    /// 'GTIN은 바코드가 진실' 규칙이 미등록 AI 하나 때문에 OCR 폴백으로 무너지지 않는다.
    /// 숫자가 아닌 AI 위치·고정장 길이 위반 같은 구조 오류는 tolerant와 무관하게 예외.</summary>
    public static Gs1Message Parse(string payload, bool tolerant = true)
    {
        var parenAis = new HashSet<string>();
        payload = NormalizeParenthesized(StripSymbologyPrefix(payload.Trim()), parenAis);
        var message = new Gs1Message();
        var pos = 0;
        while (pos < payload.Length)
        {
            if (payload[pos] == GS) { pos++; continue; }
            var spec = MatchAi(payload, pos);
            if (spec is null)
            {
                var head = payload.Substring(pos, Math.Min(4, payload.Length - pos));
                var digits = new string(head.TakeWhile(char.IsDigit).ToArray());
                if (!tolerant || digits.Length < 2)
                    throw new Gs1ParseException($"알 수 없는 AI: '{head}'", pos);
                // 미등록 AI: 괄호 표기에 있던 정확한 AI가 있으면 그것, 아니면 2자리로 기록
                var unknown = new[] { 4, 3, 2 }.Select(n => digits.Length >= n ? digits[..n] : null)
                    .FirstOrDefault(c => c is not null && parenAis.Contains(c)) ?? digits[..2];
                message.UnknownAis.Add(unknown);
                var skipTo = payload.IndexOf(GS, pos);
                pos = skipTo < 0 ? payload.Length : skipTo;
                continue;
            }
            pos += spec.Ai.Length;
            string value;
            if (spec.FixedLen is { } fixedLen)
            {
                if (pos + fixedLen > payload.Length)
                    throw new Gs1ParseException(
                        $"AI({spec.Ai}) 값이 {fixedLen}자보다 짧습니다", pos);
                value = payload.Substring(pos, fixedLen);
                pos += fixedLen;
            }
            else
            {
                var end = payload.IndexOf(GS, pos);
                if (end < 0) end = payload.Length;
                value = payload[pos..end];
                if (value.Length > spec.MaxLen)
                    throw new Gs1ParseException(
                        $"AI({spec.Ai}) 값이 최대 {spec.MaxLen}자를 초과합니다", pos);
                pos = end;
            }
            message.Elements.Add(new Gs1Element(spec.Ai, spec.Name, value));
        }
        if (message.Elements.Count == 0)
            throw new Gs1ParseException(
                message.Partial ? $"등록된 AI가 없습니다 (미등록 AI: {string.Join(", ", message.UnknownAis)})"
                                : "GS1 데이터가 비어 있습니다", 0);
        return message;
    }

    /// <summary>GS1 날짜(YYMMDD). DD=00은 해당 월 말일. 파싱 불가 시 null.</summary>
    public static DateOnly? ParseDate(string? yymmdd)
    {
        if (yymmdd is null || !Regex.IsMatch(yymmdd, @"^\d{6}$")) return null;
        var yy = int.Parse(yymmdd[..2]);
        var mm = int.Parse(yymmdd[2..4]);
        var dd = int.Parse(yymmdd[4..6]);
        var year = yy <= 50 ? 2000 + yy : 1900 + yy;
        if (mm is < 1 or > 12) return null;
        if (dd == 0) dd = DateTime.DaysInMonth(year, mm);
        try { return new DateOnly(year, mm, dd); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}

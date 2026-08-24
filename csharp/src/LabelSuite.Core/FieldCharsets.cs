// 필드별 문자 제약 — "이 필드 위치에서는 이 문자가 나올 수 없다"를 미리 지정해
// (1) 혼동 문자를 허용 문자로 자동 복원하고 (2) 복원 불가능한 문자를 에러로 검출한다.
// 예: MFG DATE에 숫자만 허용 → OCR이 "2024-05-1O"로 읽어도 O→0으로 복원되어 매칭.
namespace LabelSuite.Core;

/// <summary>한 필드의 문자 제약. Allowed가 비어 있으면 Denied만 검사한다.
/// 공백은 항상 허용된다.</summary>
public sealed record FieldCharsetRule(string Field, string Allowed, string Denied)
{
    public bool HasConstraint => Allowed.Length > 0 || Denied.Length > 0;

    /// <summary>이 문자가 제약을 위반하는지 (필드에서 나올 수 없는 문자인지).</summary>
    public bool IsViolation(char c)
    {
        if (char.IsWhiteSpace(c)) return false;
        if (Denied.Contains(c)) return true;
        return Allowed.Length > 0 && !Allowed.Contains(c);
    }
}

/// <summary>필드 → 문자 제약 사전. OCR 혼동 그룹(O↔0 등)을 이용해
/// 위반 문자를 허용 문자로 유일하게 복원할 수 있으면 복원한다.</summary>
public sealed class FieldCharsets
{
    // OCR에서 서로 뒤바뀌는 문자 그룹 (양방향 복원 후보)
    private static readonly string[] ConfusableGroups =
    [
        "Oo0QD", "Il|1", "Z2", "S5", "B8", "G6", "T7", "g9", "A4", "E3",
    ];

    private readonly Dictionary<string, FieldCharsetRule> _rules;

    public FieldCharsets(IEnumerable<FieldCharsetRule>? rules = null)
    {
        _rules = (rules ?? [])
            .Where(r => r.Field.Trim().Length > 0)
            .GroupBy(r => r.Field.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(),
                          StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<FieldCharsetRule> Rules => _rules.Values;

    public FieldCharsetRule? RuleFor(string field) =>
        _rules.TryGetValue(field.Trim(), out var rule) && rule.HasConstraint
            ? rule : null;

    /// <summary>위반 문자를 혼동 그룹에서 허용되는 문자로 복원한 텍스트.
    /// 복원 후보가 유일할 때만 치환하고, 아니면 원문 유지.</summary>
    public string Repair(string field, string text)
    {
        if (RuleFor(field) is not { } rule) return text;
        Span<char> repaired = stackalloc char[text.Length];
        for (var i = 0; i < text.Length; i++)
            repaired[i] = RepairChar(rule, text[i]);
        return new string(repaired);
    }

    private static char RepairChar(FieldCharsetRule rule, char c)
    {
        if (!rule.IsViolation(c)) return c;
        char? candidate = null;
        foreach (var group in ConfusableGroups)
        {
            if (!group.Contains(c)) continue;
            foreach (var other in group)
            {
                if (other == c || rule.IsViolation(other)) continue;
                if (candidate is not null && candidate != other) return c;   // 모호 → 유지
                candidate = other;
            }
        }
        return candidate ?? c;
    }

    /// <summary>복원 시도 후에도 남는 위반 문자 목록 (에러 검출용).</summary>
    public List<(int Index, char Ch)> Violations(string field, string text)
    {
        var violations = new List<(int, char)>();
        if (RuleFor(field) is not { } rule) return violations;
        var repaired = Repair(field, text);
        for (var i = 0; i < repaired.Length; i++)
            if (rule.IsViolation(repaired[i])) violations.Add((i, text[i]));
        return violations;
    }
}

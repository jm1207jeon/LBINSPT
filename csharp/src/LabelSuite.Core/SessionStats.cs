// 검사 세션 집계 — 내보내기 전 확인 게이트와 '다음 확인 대상' 이동의 순수 계산.
namespace LabelSuite.Core;

/// <summary>페이지 단위 세션 현황. Total=PDF 페이지 수, Passed/Check=검사된 페이지 중 합격/확인 필요,
/// Uninspected=아직 결과가 없는 페이지, Unsaved=검사됐지만 결과 저장이 안 된 페이지.</summary>
public sealed record SessionStats(int Total, int Passed, int Check, int Uninspected, int Unsaved)
{
    public int Inspected => Passed + Check;

    /// <summary>내보내기 전 확인 대화상자가 필요한지 (하나라도 있으면).</summary>
    public bool NeedsConfirmation => Check + Uninspected + Unsaved > 0;

    /// <summary>passedByPage: 검사된 페이지(0-based) → 합격 여부, savedPages: 결과 저장된 페이지.
    /// 범위(0..pageCount-1) 밖 키는 무시한다.</summary>
    public static SessionStats Of(int pageCount, IReadOnlyDictionary<int, bool> passedByPage,
                                  IReadOnlySet<int> savedPages)
    {
        var total = Math.Max(0, pageCount);
        int passed = 0, check = 0, unsaved = 0;
        foreach (var (page, ok) in passedByPage)
        {
            if (page < 0 || page >= total) continue;
            if (ok) passed++; else check++;
            if (!savedPages.Contains(page)) unsaved++;
        }
        return new SessionStats(total, passed, check, total - passed - check, unsaved);
    }

    /// <summary>내보내기 전 확인 문구 (NeedsConfirmation일 때만 의미 있음).</summary>
    public string ConfirmMessage() =>
        $"확인 필요 {Check}건 · 미검사 {Uninspected}건 (PDF {Total}페이지 중) · 결과 미저장 {Unsaved}건이 포함됩니다.\n" +
        "프로그램 표시는 참고값이며 최종 판정은 검사자가 합니다.\n그래도 내보낼까요?";

    /// <summary>current 다음 페이지부터 순환하며 주의가 필요한 첫 페이지를 찾는다.
    /// state: null=미검사, false=확인 필요, true=합격. 다른 페이지가 모두 합격이면
    /// 마지막으로 current 자신을 검사하고, 그래도 없으면 null.</summary>
    public static int? NextAttention(int pageCount, int current, Func<int, bool?> state)
    {
        if (pageCount <= 0) return null;
        var start = ((current % pageCount) + pageCount) % pageCount;
        for (var step = 1; step <= pageCount; step++)
        {
            var page = (start + step) % pageCount;
            if (state(page) != true) return page;
        }
        return null;
    }
}

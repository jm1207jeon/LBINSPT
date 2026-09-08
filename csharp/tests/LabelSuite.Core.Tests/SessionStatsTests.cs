// 세션 집계(내보내기 전 확인 게이트)와 '다음 확인 대상' 순환 탐색.
using LabelSuite.Core;
using Xunit;

namespace LabelSuite.Core.Tests;

public class SessionStatsTests
{
    [Fact]
    public void Of_CountsUninspectedAndUnsaved()
    {
        var passed = new Dictionary<int, bool> { [0] = true, [1] = false, [3] = true, [9] = true };
        var saved = new HashSet<int> { 0, 9 };
        var stats = SessionStats.Of(5, passed, saved);
        Assert.Equal(new SessionStats(Total: 5, Passed: 2, Check: 1, Uninspected: 2, Unsaved: 2),
                     stats);   // 페이지 9는 범위 밖이라 무시
        Assert.Equal(3, stats.Inspected);
        Assert.True(stats.NeedsConfirmation);
        Assert.Contains("확인 필요 1건 · 미검사 2건 (PDF 5페이지 중) · 결과 미저장 2건",
                        stats.ConfirmMessage());
    }

    [Fact]
    public void Of_AllPassedAndSavedNeedsNoConfirmation()
    {
        var passed = new Dictionary<int, bool> { [0] = true, [1] = true };
        var stats = SessionStats.Of(2, passed, new HashSet<int> { 0, 1 });
        Assert.False(stats.NeedsConfirmation);
        Assert.Equal(0, stats.Uninspected);
    }

    private static Func<int, bool?> States(params bool?[] states) => p => states[p];

    [Fact]
    public void NextAttention_WrapsAround()
    {
        // current=4, 1페이지만 미검사 → 순환해서 1
        var state = States(true, null, true, true, true);
        Assert.Equal(1, SessionStats.NextAttention(5, 4, state));
    }

    [Fact]
    public void NextAttention_NoneWhenAllPassed()
    {
        Assert.Null(SessionStats.NextAttention(3, 0, States(true, true, true)));
        Assert.Null(SessionStats.NextAttention(0, 0, _ => null));
    }

    [Fact]
    public void NextAttention_SkipsCurrent()
    {
        // 현재(2)가 확인 필요여도 다음 페이지부터 탐색 → 4
        var state = States(true, true, false, true, false);
        Assert.Equal(4, SessionStats.NextAttention(5, 2, state));
        // 다른 페이지가 모두 합격이면 마지막으로 현재 페이지 자신
        Assert.Equal(2, SessionStats.NextAttention(5, 2, States(true, true, false, true, true)));
    }

    [Fact]
    public void NextAttention_PrefersFirstAfterCurrent()
    {
        var state = States(null, true, false, null, true);
        Assert.Equal(2, SessionStats.NextAttention(5, 1, state));
        Assert.Equal(3, SessionStats.NextAttention(5, 2, state));
        Assert.Equal(0, SessionStats.NextAttention(5, 4, state));
    }
}

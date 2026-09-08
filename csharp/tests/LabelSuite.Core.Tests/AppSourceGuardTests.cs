// App 소스 규약 감시 — WPF 프로젝트는 리눅스에서 빌드되지 않으므로, 컴파일은 되지만 실행 즉시
// 죽는 실수(Status() 헬퍼의 자기 호출 → StackOverflow)를 소스 텍스트 수준에서 잡는다.
using System.Text.RegularExpressions;
using Xunit;

namespace LabelSuite.Core.Tests;

/// <summary>테스트 어셈블리 위치에서 상위로 올라가며 csharp/src/LabelSuite.App를 찾는다.
/// 소스가 없는 환경(패키지된 테스트만 실행)에서는 false — 호출 측이 조용히 통과한다.</summary>
public static class AppSourceLocator
{
    public static bool TryFind(out string dir)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "csharp", "src", "LabelSuite.App");
            if (Directory.Exists(candidate))
            {
                dir = candidate;
                return true;
            }
            current = current.Parent;
        }
        dir = "";
        return false;
    }
}

public class AppSourceGuardTests
{
    private static readonly Regex SelfRecursiveStatus =
        new(@"void\s+Status\s*\([^)]*\)\s*=>\s*Status\s*\(", RegexOptions.Compiled);

    [Fact]
    public void NoSelfRecursiveStatusHelper()
    {
        if (!AppSourceLocator.TryFind(out var app)) return;
        var offenders = Directory.GetFiles(Path.Combine(app, "Views"), "*.xaml.cs")
            .Where(f => SelfRecursiveStatus.IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .ToList();
        Assert.True(offenders.Count == 0,
            "Status() 헬퍼가 자기 자신을 호출합니다 (StackOverflow): " + string.Join(", ", offenders));
    }

    [Theory]
    [InlineData("InspectorView.xaml.cs")]
    [InlineData("GeneratorView.xaml.cs")]
    public void StatusHelperForwardsToEvent(string file)
    {
        if (!AppSourceLocator.TryFind(out var app)) return;
        var path = Path.Combine(app, "Views", file);
        if (!File.Exists(path)) return;
        var source = File.ReadAllText(path);
        Assert.True(Regex.IsMatch(source, @"StatusMessage\?\.Invoke\(message,\s*level\)"),
                    $"{file}: Status() 헬퍼가 StatusMessage?.Invoke(message, level)로 전달해야 합니다.");
    }
}

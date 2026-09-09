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

    private static IEnumerable<string> AppSources(string app, string pattern) =>
        Directory.EnumerateFiles(app, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static string Rel(string app, string file) =>
        Path.GetRelativePath(app, file).Replace('\\', '/');

    /// <summary>대화상자 규범: MessageBox.Show는 Services/Dialogs.cs(Owner·기본 '아니오' 강제)와
    /// App.xaml.cs(부팅 실패 — 창이 없음)에서만. 나머지는 Dialogs.Confirm/Info/Warn/Error 또는 ChoiceDialog.</summary>
    [Fact]
    public void MessageBoxOnlyThroughDialogs()
    {
        if (!AppSourceLocator.TryFind(out var app)) return;
        string[] allowed = ["Services/Dialogs.cs", "App.xaml.cs"];
        var offenders = AppSources(app, "*.cs")
            .Where(f => !allowed.Contains(Rel(app, f)))
            .Where(f => File.ReadAllText(f).Contains("MessageBox.Show("))
            .Select(f => Rel(app, f))
            .ToList();
        Assert.True(offenders.Count == 0,
            "MessageBox.Show 직접 호출 — Dialogs.* 로 바꾸세요: " + string.Join(", ", offenders));
    }

    /// <summary>예/아니오 확인은 Dialogs.Confirm(기본 '아니오')만 — Enter 오조작으로 삭제·학습·과금 방지.</summary>
    [Fact]
    public void YesNoOnlyInDialogs()
    {
        if (!AppSourceLocator.TryFind(out var app)) return;
        var offenders = AppSources(app, "*.cs")
            .Where(f => Rel(app, f) != "Services/Dialogs.cs")
            .Where(f => File.ReadAllText(f).Contains("MessageBoxButton.YesNo"))
            .Select(f => Rel(app, f))
            .ToList();
        Assert.True(offenders.Count == 0,
            "MessageBoxButton.YesNo 직접 사용 — Dialogs.Confirm 으로 바꾸세요: " + string.Join(", ", offenders));
    }

    /// <summary>모든 모달 창은 Esc로 닫혀야 한다: 루트가 Window인 XAML마다 IsCancel="True" 버튼 ≥1
    /// (MainWindow.xaml은 주 창이라 제외).</summary>
    [Fact]
    public void EveryWindowXamlHasCancelButton()
    {
        if (!AppSourceLocator.TryFind(out var app)) return;
        var offenders = new List<string>();
        foreach (var file in AppSources(app, "*.xaml"))
        {
            if (Path.GetFileName(file) == "MainWindow.xaml") continue;
            var text = File.ReadAllText(file);
            var root = Regex.Match(text, @"<(?!\?|!--)([A-Za-z][\w:.]*)");
            if (!root.Success || root.Groups[1].Value != "Window") continue;
            if (!text.Contains("IsCancel=\"True\"")) offenders.Add(Rel(app, file));
        }
        Assert.True(offenders.Count == 0,
            "IsCancel=\"True\" 버튼이 없는 Window (Esc로 닫히지 않음): " + string.Join(", ", offenders));
    }

    /// <summary>SettingsWindow.xaml의 Tag(설정 경로)는 SettingRanges.All 또는 save_directory에 있어야 하고,
    /// 범위표의 단일 값 경로는 (UI에 노출하지 않는 예외 목록을 빼고) 모두 입력란을 가져야 한다.</summary>
    [Fact]
    public void SettingsXamlTagsMatchSettingRanges()
    {
        if (!AppSourceLocator.TryFind(out var app)) return;
        var path = Path.Combine(app, "SettingsWindow.xaml");
        if (!File.Exists(path)) return;
        var tags = Regex.Matches(File.ReadAllText(path), @"<TextBox[^>]*\bTag=""([^""]+)""")
            .Select(m => m.Groups[1].Value).ToHashSet();
        var known = SettingRanges.All.Select(d => d.Path).Append("save_directory").ToHashSet();
        var unknown = tags.Where(t => !known.Contains(t)).ToList();
        Assert.True(unknown.Count == 0,
            "SettingRanges에 없는 Tag (InputRules가 배선하지 못함): " + string.Join(", ", unknown));

        // UI 입력란이 없는 설정 (캐시 크기 등 — settings.json 직접 편집 항목)
        string[] notInUi = ["ocr_cache_max_entries", "page_image_cache_pages"];
        var missing = SettingRanges.All.Select(d => d.Path)
            .Where(p => !p.Contains("[]") && !notInUi.Contains(p) && !tags.Contains(p))
            .ToList();
        Assert.True(missing.Count == 0,
            "SettingsWindow.xaml에 Tag가 없는 범위표 경로: " + string.Join(", ", missing));
    }
}

/// <summary>XAML 컴파일(MC3072) 사전 차단 — Control 전용 속성(TabIndex·IsTabStop·Foreground·Font*)을
/// Panel/Border/도형/Image 요소에 직접 쓰면 Windows CI에서만 실패한다. 첨부 속성 표기
/// (KeyboardNavigation.TabIndex, TextElement.Foreground)는 허용.</summary>
public class XamlControlOnlyAttributeTests
{
    private static readonly string[] NonControlTags =
        ["StackPanel", "WrapPanel", "DockPanel", "Grid", "UniformGrid", "Canvas",
         "Border", "Rectangle", "Ellipse", "Path", "Line", "Image", "Viewbox"];

    // Control(또는 TextBlock)에만 있는 직접 속성 — 위 요소에 쓰면 MC3072
    private static readonly string[] ControlOnly =
        ["TabIndex", "IsTabStop", "HorizontalContentAlignment", "VerticalContentAlignment",
         "Foreground", "FontSize", "FontWeight", "FontFamily", "FontStyle"];

    [Fact]
    public void ControlOnlyAttributesNotUsedOnPanelsOrShapes()
    {
        if (!AppSourceLocator.TryFind(out var app)) return;
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(app, "*.xaml", SearchOption.AllDirectories))
        {
            var doc = System.Xml.Linq.XDocument.Load(file);
            foreach (var el in doc.Descendants().Where(e => NonControlTags.Contains(e.Name.LocalName)))
            {
                var bad = el.Attributes()
                    .Where(a => a.Name.Namespace == System.Xml.Linq.XNamespace.None)   // 첨부 속성(X.Y)은 LocalName에 '.'이 있어 제외됨
                    .Select(a => a.Name.LocalName)
                    .Where(ControlOnly.Contains)
                    .ToList();
                if (bad.Count > 0)
                    offenders.Add($"{Path.GetFileName(file)}: <{el.Name.LocalName}> {string.Join(",", bad)}");
            }
        }
        Assert.True(offenders.Count == 0,
            "Control 전용 속성이 비-Control 요소에 쓰였습니다 (MC3072). 첨부 속성(KeyboardNavigation.TabIndex 등)으로 바꾸세요:\n"
            + string.Join("\n", offenders));
    }
}

/// <summary>2026-09-09 요청 규칙의 소스 수준 회귀 가드 — 규격은 라벨 문서번호로만(목록 STANDARD 열 사용 금지),
/// DataMatrix는 검증 표에서 제외, 목록 생성기에 국가별 규격 매핑 없음.</summary>
public class RuleRegressionGuardTests
{
    [Fact]
    public void InspectorDoesNotSelectStandardFromListColumn()
    {
        if (!AppSourceLocator.TryFind(out var app)) return;
        var source = File.ReadAllText(Path.Combine(app, "Views", "InspectorView.xaml.cs"));
        Assert.DoesNotMatch(@"record\??\.Standard\b", source);
        Assert.DoesNotMatch(@"_records\[\w+\]\.Standard\b", source);
        Assert.Contains("StandardDetector.Detect(", source);
    }

    [Fact]
    public void BarcodeGridExcludesDataMatrix()
    {
        if (!AppSourceLocator.TryFind(out var app)) return;
        var source = File.ReadAllText(Path.Combine(app, "Views", "InspectorView.xaml.cs"));
        Assert.Contains(".Where(b => !b.IsDataMatrix)", source);
        Assert.Contains("BarcodeDetector.Summarize(", source);
    }

    [Fact]
    public void GeneratorHasNoCountryStandardMapping()
    {
        if (!AppSourceLocator.TryFind(out var app)) return;
        var core = Path.Combine(Path.GetDirectoryName(app)!, "LabelSuite.Core");
        Assert.DoesNotContain("StandardForCountry", File.ReadAllText(Path.Combine(core, "ListGenerator.cs")));
        Assert.DoesNotContain("country_standard_map",
                              File.ReadAllText(Path.Combine(core, "DefaultConfig", "settings.json")));
        Assert.DoesNotContain("CountryStandardMap",
                              File.ReadAllText(Path.Combine(app, "Views", "GeneratorView.xaml.cs")));
    }
}

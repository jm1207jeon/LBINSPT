// Theme.xaml 토큰 정합 감시 — WPF는 리눅스에서 빌드되지 않으므로, 존재하지 않는 리소스 키 참조
// (실행 시 XamlParseException으로 창이 뜨지 않음)와 인라인 색 잔존을 소스 텍스트 수준에서 잡는다.
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace LabelSuite.Core.Tests;

public class ThemeTokenTests(ITestOutputHelper output)
{
    private static readonly Regex KeyDef = new(@"x:Key=""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex MarkupRef =
        new(@"\{(?:StaticResource|DynamicResource)\s+([^}\s]+)\s*\}", RegexOptions.Compiled);
    private static readonly Regex CodeRef =
        new(@"(?:Try)?FindResource\(\s*""([^""]+)""\s*\)", RegexOptions.Compiled);
    private static readonly Regex MergedSource = new(@"Source=""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex InlineColor =
        new(@"=""(#[0-9A-Fa-f]{6,8}|Gray|Red)""", RegexOptions.Compiled);

    private static IEnumerable<string> SourceFiles(string app) =>
        Directory.EnumerateFiles(app, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".xaml") || f.EndsWith(".cs"))
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static HashSet<string> KeysIn(string file) =>
        KeyDef.Matches(File.ReadAllText(file)).Select(m => m.Groups[1].Value).ToHashSet();

    /// <summary>App.xaml MergedDictionaries에 등록된 전역 사전(Theme.xaml 등)의 x:Key 합집합.</summary>
    private static HashSet<string> GlobalKeys(string app)
    {
        var keys = new HashSet<string>();
        var appXaml = Path.Combine(app, "App.xaml");
        var sources = File.Exists(appXaml)
            ? MergedSource.Matches(File.ReadAllText(appXaml)).Select(m => m.Groups[1].Value).ToList()
            : new List<string>();
        if (!sources.Contains("Theme.xaml")) sources.Add("Theme.xaml");
        foreach (var source in sources)
        {
            var path = Path.Combine(app, source.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path)) keys.UnionWith(KeysIn(path));
        }
        return keys;
    }

    private static IEnumerable<string> RefsIn(string text) =>
        MarkupRef.Matches(text).Select(m => m.Groups[1].Value)
            .Concat(CodeRef.Matches(text).Select(m => m.Groups[1].Value))
            .Where(k => !k.StartsWith('{'));   // {StaticResource {x:Type Button}} 제외

    [Fact]
    public void EveryReferencedResourceKeyIsDefined()
    {
        if (!AppSourceLocator.TryFind(out var app)) return;
        var global = GlobalKeys(app);
        Assert.NotEmpty(global);
        var missing = new List<string>();
        foreach (var file in SourceFiles(app))
        {
            var text = File.ReadAllText(file);
            var local = file.EndsWith(".xaml") ? KeysIn(file) : new HashSet<string>();
            // 코드비하인드는 짝 XAML의 로컬 키도 볼 수 있다
            if (file.EndsWith(".xaml.cs") && File.Exists(file[..^3])) local.UnionWith(KeysIn(file[..^3]));
            foreach (var key in RefsIn(text).Distinct())
                if (!global.Contains(key) && !local.Contains(key))
                    missing.Add($"{Path.GetFileName(file)}: {key}");
        }
        Assert.True(missing.Count == 0,
            "정의되지 않은 리소스 키 참조 (실행 시 XamlParseException):\n  " + string.Join("\n  ", missing));
    }

    [Fact]
    public void ReportUnreferencedThemeKeys()
    {
        if (!AppSourceLocator.TryFind(out var app)) return;
        var theme = Path.Combine(app, "Theme.xaml");
        if (!File.Exists(theme)) return;
        var themeText = File.ReadAllText(theme);
        var defined = KeysIn(theme);
        var referenced = new HashSet<string>();
        foreach (var file in SourceFiles(app))
            if (Path.GetFullPath(file) != Path.GetFullPath(theme))
                referenced.UnionWith(RefsIn(File.ReadAllText(file)));
        // Theme.xaml 안에서 다른 토큰이 참조하는 키(스타일 Setter의 StaticResource)는 사용 중으로 본다
        referenced.UnionWith(RefsIn(themeText));
        var unused = defined.Where(k => !referenced.Contains(k)).OrderBy(k => k).ToList();
        output.WriteLine(unused.Count == 0
            ? "Theme.xaml: 미참조 토큰 없음"
            : $"Theme.xaml 미참조 토큰 {unused.Count}개 (실패 아님): {string.Join(", ", unused)}");
    }

    /// <summary>화면 범례·점선 색(Theme.xaml 오버레이 토큰)과 저장 이미지 색(Core Annotate 상수)이 같은 hex여야
    /// 검사자가 화면에서 본 색 그대로 저장본을 읽는다.</summary>
    [Theory]
    [InlineData("OverlayBarcodeBrush", 0, 128, 128)]
    [InlineData("OverlayFormBrush", 128, 0, 160)]
    [InlineData("OverlayLowConfBrush", 255, 140, 0)]
    public void OverlayColorsMatchCoreConstants(string key, int r, int g, int b)
    {
        var core = key switch
        {
            "OverlayBarcodeBrush" => Annotate.BarcodeBoxColor,
            "OverlayFormBrush" => Annotate.FormBoxColor,
            _ => Annotate.LowConfidenceColor,
        };
        // 기대값(InlineData)은 상수가 조용히 바뀌는 것도 잡는다
        Assert.Equal((r, g, b), ((int)core.Red, (int)core.Green, (int)core.Blue));
        if (!AppSourceLocator.TryFind(out var app)) return;
        var theme = File.ReadAllText(Path.Combine(app, "Theme.xaml"));
        var match = Regex.Match(theme, $@"x:Key=""{key}""\s+Color=""#([0-9A-Fa-f]{{6}})""");
        Assert.True(match.Success, $"Theme.xaml에 {key} 정의 없음");
        var expected = $"{core.Red:X2}{core.Green:X2}{core.Blue:X2}";
        Assert.Equal(expected, match.Groups[1].Value.ToUpperInvariant());
    }

    [Fact]
    public void NoInlineHexColorsInViewXaml()
    {
        if (!AppSourceLocator.TryFind(out var app)) return;
        var targets = Directory.GetFiles(Path.Combine(app, "Views"), "*.xaml")
            .Concat([Path.Combine(app, "SettingsWindow.xaml"), Path.Combine(app, "MainWindow.xaml")])
            .Where(File.Exists);
        var offenders = new List<string>();
        var warnings = new List<string>();
        foreach (var file in targets)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                foreach (Match m in InlineColor.Matches(lines[i]))
                {
                    var entry = $"{Path.GetFileName(file)}:{i + 1}: {m.Value}";
                    // InspectorView.xaml의 인라인 색 정리는 별도 항목(A-06 (2)) — 여기서는 경고만
                    if (Path.GetFileName(file) == "InspectorView.xaml") warnings.Add(entry);
                    else offenders.Add(entry);
                }
        }
        foreach (var w in warnings) output.WriteLine("경고(미차단): " + w);
        Assert.True(offenders.Count == 0,
            "인라인 색 잔존 — Theme.xaml 토큰({StaticResource …Brush})으로 바꾸세요:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>docs/family_design.md가 언급하는 스타일·브러시 토큰이 Theme.xaml에 실제로 존재한다 —
    /// 문서와 코드가 어긋나면(토큰 삭제·개명) 문서 갱신을 강제한다.</summary>
    [Fact]
    public void FamilyDesignDocTokensExist()
    {
        if (!AppSourceLocator.TryFind(out var app)) return;
        var repo = Directory.GetParent(Directory.GetParent(Directory.GetParent(app)!.FullName)!.FullName)!.FullName;
        var doc = Path.Combine(repo, "docs", "family_design.md");
        if (!File.Exists(doc)) return;
        var theme = File.ReadAllText(Path.Combine(app, "Theme.xaml"));
        var keys = System.Text.RegularExpressions.Regex.Matches(theme, "x:Key=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value).ToHashSet();
        var mentioned = System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(doc),
                "`([A-Z][A-Za-z]+(?:Brush|Color|Size|Button|Text|Panel|Chip|Banner|Card|Toggle|VSep))`")
            .Select(m => m.Groups[1].Value).Distinct().ToList();
        var missing = mentioned.Where(k => !keys.Contains(k)).ToList();
        Assert.True(missing.Count == 0, "family_design.md에 있으나 Theme.xaml에 없는 토큰: " + string.Join(", ", missing));
    }
}

// 설정 입력란 검증 가시화 (UDInspect SET-02/05 규범): 범위 밖·해석 불가 입력은 즉시 붉은 배경 +
// 범위 툴팁으로 알리고 invalid 집합에 넣어 [저장]이 차단되게 한다. 범위의 출처는 Core SettingRanges 하나뿐.
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LabelSuite.Core;

namespace LabelSuite.App;

public static class InputRules
{
    /// <summary>SaveDirBox 등 폴더 경로 입력란의 Tag 값.</summary>
    public const string DirectoryTag = "save_directory";

    /// <summary>숫자 설정 입력란을 SettingRanges 범위표에 연결한다. TextChanged마다 다시 검사하며
    /// 무효면 배경 InvalidInputBrush + 안내 툴팁 + invalid 집합 추가, 유효면 원복.
    /// path가 범위표에 없으면 AppLog.Warn 후 아무것도 하지 않는다.</summary>
    public static void Attach(TextBox box, string settingPath, ISet<TextBox> invalid)
    {
        var def = SettingRanges.Find(settingPath);
        if (def is null)
        {
            AppLog.Warn($"InputRules: 범위표에 없는 설정 경로 '{settingPath}' (TextBox {box.Name})");
            return;
        }
        var range = $"{Format(def, def.Min)}~{Format(def, def.Max)}{def.Unit}";
        var normalTip = $"{def.Label} · 범위 {range} · 기본 {Format(def, def.Default)}";
        var invalidTip = $"{def.Label}: {range} 사이 값이어야 합니다 (기본 {Format(def, def.Default)}) — 이 값은 저장되지 않습니다";

        void Validate()
        {
            var text = box.Text.Trim();
            var ok = def.Integer
                ? int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
                  && i >= def.IntMin && i <= def.IntMax
                : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                  && d >= def.Min && d <= def.Max;
            Mark(box, ok, invalid, ok ? normalTip : invalidTip);
        }

        box.TextChanged += (_, _) => Validate();
        Validate();
    }

    /// <summary>결과 저장 폴더 입력란 — PathRules.IsValidDirectory. 빈값은 '기본 폴더 사용'이라 유효.</summary>
    public static void AttachDirectory(TextBox box, ISet<TextBox> invalid)
    {
        const string normalTip = "결과 이미지·이력이 저장될 폴더 (비우면 내 문서\\LaVIS_결과)";
        const string invalidTip = "올바른 절대 경로가 아닙니다 (예: D:\\LaVIS_결과 또는 \\\\서버\\공유\\LaVIS) — 이 값은 저장되지 않습니다";

        void Validate()
        {
            var text = box.Text.Trim();
            var ok = text.Length == 0 || PathRules.IsValidDirectory(text);
            Mark(box, ok, invalid, ok ? normalTip : invalidTip);
        }

        box.TextChanged += (_, _) => Validate();
        Validate();
    }

    /// <summary>Tag(설정 경로)에 따라 Attach/AttachDirectory를 고른다. Tag가 문자열이 아니면 무시.</summary>
    public static void AttachByTag(TextBox box, ISet<TextBox> invalid)
    {
        if (box.Tag is not string path || path.Length == 0) return;
        if (path == DirectoryTag) AttachDirectory(box, invalid);
        else Attach(box, path, invalid);
    }

    /// <summary>오류 안내문에 쓸 입력란 이름 (범위표 Label, 경로면 '결과 저장 경로').</summary>
    public static string LabelOf(TextBox box) =>
        box.Tag is string path
            ? path == DirectoryTag ? "결과 저장 경로" : SettingRanges.Find(path)?.Label ?? path
            : box.Name;

    private static void Mark(TextBox box, bool ok, ISet<TextBox> invalid, string tip)
    {
        box.ToolTip = tip;
        if (ok)
        {
            box.ClearValue(Control.BackgroundProperty);
            invalid.Remove(box);
        }
        else
        {
            box.Background = box.TryFindResource("InvalidInputBrush") as Brush
                             ?? new SolidColorBrush(Color.FromRgb(0xFF, 0xE4, 0xE1));
            invalid.Add(box);
        }
    }

    private static string Format(RangeDef def, double value) =>
        def.Integer ? ((int)Math.Round(value)).ToString(CultureInfo.InvariantCulture)
                    : value.ToString("0.##", CultureInfo.InvariantCulture);
}

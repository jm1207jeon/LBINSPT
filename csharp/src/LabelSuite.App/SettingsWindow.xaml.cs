// 설정 — OCR/대상 필드/바운딩 박스/교정 사전/기준정보/저장까지 사용자 편집 가능.
// 고급 항목(컬럼 매핑 등)은 '설정 폴더 열기'로 JSON 직접 편집 (README 참고).
using System.Collections.ObjectModel;
using System.Data;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using LabelSuite.Core;
using Microsoft.Win32;

namespace LabelSuite.App;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private readonly OcrCorrections _corrections;
    private static readonly string[] CountFields =
        ["LOT", "PN", "REF", "MFG DATE", "EXP DATE", "GTIN", "CHINA"];
    private static readonly string[] DisableableFields =
        ["PRODUCTS", "PN", "REF", "MFG DATE", "EXP DATE", "GTIN", "CHINA"];

    public sealed class CustomFieldVm
    {
        public string Name { get; set; } = "";
        public string Pattern { get; set; } = "";
        public bool IsRegex { get; set; }
        public string Expected { get; set; } = "";
    }

    public sealed class ColorVm
    {
        public string Field { get; set; } = "";
        public string R { get; set; } = "0";
        public string G { get; set; } = "0";
        public string B { get; set; } = "0";
    }

    public sealed class CorrectionVm
    {
        public string Wrong { get; set; } = "";
        public string Right { get; set; } = "";
        public string Field { get; set; } = "";
        public string LearnedAt { get; set; } = "";
    }

    private readonly ObservableCollection<CustomFieldVm> _customFields = [];
    private readonly ObservableCollection<ColorVm> _fieldColors = [];
    private readonly ObservableCollection<CorrectionVm> _correctionRows = [];
    private readonly Dictionary<string, CheckBox> _disableChecks = [];

    public SettingsWindow(AppConfig config, OcrCorrections corrections)
    {
        InitializeComponent();
        _config = config;
        _corrections = corrections;
        LoadValues();
    }

    private void LoadValues()
    {
        // 일반
        SaveDirBox.Text = _config.GetString("save_directory");
        ShelfLifeBox.Text = _config.GetInt("shelf_life_months", 36).ToString();
        var policy = _config.Settings["prefetch_policy"];
        PrefetchCombo.SelectedIndex = policy?.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.Number => policy.GetValue<int>() switch
            { 1 => 1, 2 => 2, 5 => 3, 0 => 4, _ => 0 },
            _ => 0,
        };

        // OCR
        RenderZoomBox.Text = _config.GetDouble("pdf_render_zoom", 4.0).ToString("F1");
        OcrMaxDimBox.Text = _config.SectionInt("ocr", "max_dimension", 2000).ToString();
        OcrJpegQualityBox.Text = _config.SectionInt("ocr", "jpeg_quality", 85).ToString();
        OcrMinConfBox.Text = _config.SectionInt("ocr", "min_confidence", 0).ToString();
        ContrastStretchCheck.IsChecked = _config.SectionBool("ocr", "contrast_stretch", false);
        ConfusablesCheck.IsChecked = _config.SectionBool("ocr", "allow_confusables", true);

        // OCR 대상 필드
        var disabled = new HashSet<string>();
        if (_config.Section("fields")["disabled"] is JsonArray disabledArray)
            foreach (var node in disabledArray)
                if (node?.GetValue<string>() is { } name) disabled.Add(name);
        DisabledFieldsPanel.Children.Clear();
        foreach (var field in DisableableFields)
        {
            var check = new CheckBox
            { Content = field, IsChecked = disabled.Contains(field), Margin = new Thickness(0, 2, 12, 2) };
            _disableChecks[field] = check;
            DisabledFieldsPanel.Children.Add(check);
        }
        if (_config.Section("fields")["custom"] is JsonArray customArray)
            foreach (var node in customArray)
                if (node is JsonObject obj)
                    _customFields.Add(new CustomFieldVm
                    {
                        Name = obj["name"]?.GetValue<string>() ?? "",
                        Pattern = obj["pattern"]?.GetValue<string>() ?? "",
                        IsRegex = obj["is_regex"]?.GetValue<bool>() ?? false,
                        Expected = obj["expected"] is { } exp
                            && exp.AsValue().TryGetValue<int>(out var v) ? v.ToString() : "",
                    });
        CustomFieldsGrid.ItemsSource = _customFields;

        // 바운딩 박스
        OverlayThicknessBox.Text = _config.SectionInt("overlay", "thickness", 2).ToString();
        OverlayAlphaBox.Text = _config.SectionInt("overlay", "fill_alpha", 90).ToString();
        OverlayNumbersCheck.IsChecked = _config.SectionBool("overlay", "show_numbers", false);
        if (_config.StandardsRaw["field_colors"] is JsonObject colors)
            foreach (var (field, rgba) in colors)
            {
                var array = rgba!.AsArray();
                _fieldColors.Add(new ColorVm
                {
                    Field = field,
                    R = array[0]!.GetValue<int>().ToString(),
                    G = array[1]!.GetValue<int>().ToString(),
                    B = array[2]!.GetValue<int>().ToString(),
                });
            }
        FieldColorsGrid.ItemsSource = _fieldColors;

        // 교정 사전
        foreach (var entry in _corrections.Entries)
            _correctionRows.Add(new CorrectionVm
            {
                Wrong = entry.Wrong, Right = entry.Right,
                Field = entry.Field ?? "", LearnedAt = entry.LearnedAt ?? "",
            });
        CorrectionsGrid.ItemsSource = _correctionRows;

        // AWS
        var aws = _config.Settings["aws"]?.AsObject();
        AwsRegionBox.Text = aws?["region"]?.GetValue<string>() ?? "ap-northeast-2";
        AwsProfileBox.Text = aws?["profile"]?.GetValue<string>() ?? "";

        // 검사 기준
        var table = new DataTable();
        table.Columns.Add("규격");
        foreach (var field in CountFields) table.Columns.Add(field, typeof(int));
        if (_config.StandardsRaw["standards"] is JsonObject standards)
            foreach (var (name, node) in standards)
            {
                var row = table.NewRow();
                row["규격"] = name;
                var counts = node?["counts"]?.AsObject();
                foreach (var field in CountFields)
                    row[field] = counts?[field]?.GetValue<int>() ?? 0;
                table.Rows.Add(row);
            }
        CountsGrid.ItemsSource = table.DefaultView;

        // 저장
        SaveScaleBox.Text = _config.GetDouble("save_scale", 0.5).ToString("F2");
        SaveJpegQualityBox.Text = _config.GetInt("jpeg_quality", 90).ToString();
        AutoSaveDefaultCheck.IsChecked = _config.GetBool("auto_save_default", false);
    }

    private void OnBrowseSaveDir(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "결과 저장 폴더" };
        if (dialog.ShowDialog() == true) SaveDirBox.Text = dialog.FolderName;
    }

    private async void OnCheckAws(object sender, RoutedEventArgs e)
    {
        AwsCheckLabel.Text = "확인 중…";
        var client = new TextractClient(
            AwsRegionBox.Text.Trim(),
            AwsProfileBox.Text.Trim().Length > 0 ? AwsProfileBox.Text.Trim() : null);
        var status = await client.ValidateCredentialsAsync();
        AwsCheckLabel.Text = status.Ok ? $"확인됨: {status.IdentityArn}" : $"실패: {status.Error}";
        AwsCheckLabel.Foreground = status.Ok
            ? (System.Windows.Media.Brush)FindResource("SuccessBrush")
            : (System.Windows.Media.Brush)FindResource("DangerBrush");
    }

    private void OnOpenConfigFolder(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(_config.Directory) { UseShellExecute = true });

    private static int ParseInt(string text, int fallback, int min, int max) =>
        int.TryParse(text, out var v) ? Math.Clamp(v, min, max) : fallback;

    private void OnSave(object sender, RoutedEventArgs e)
    {
        foreach (var grid in new[] { CustomFieldsGrid, FieldColorsGrid, CorrectionsGrid,
                                     CountsGrid })
            grid.CommitEdit(DataGridEditingUnit.Row, true);

        var settings = _config.Settings;
        // 일반
        settings["save_directory"] = SaveDirBox.Text.Trim();
        if (int.TryParse(ShelfLifeBox.Text, out var shelfLife) && shelfLife > 0)
            settings["shelf_life_months"] = shelfLife;
        settings["prefetch_policy"] = PrefetchCombo.SelectedIndex switch
        {
            1 => JsonValue.Create(1), 2 => JsonValue.Create(2),
            3 => JsonValue.Create(5), 4 => JsonValue.Create(0),
            _ => JsonValue.Create("all"),
        };
        if (double.TryParse(RenderZoomBox.Text, out var zoom) && zoom is >= 1 and <= 8)
            settings["pdf_render_zoom"] = zoom;

        // OCR
        var ocr = _config.Section("ocr");
        ocr["max_dimension"] = ParseInt(OcrMaxDimBox.Text, 2000, 500, 4000);
        ocr["jpeg_quality"] = ParseInt(OcrJpegQualityBox.Text, 85, 30, 100);
        ocr["min_confidence"] = ParseInt(OcrMinConfBox.Text, 0, 0, 100);
        ocr["contrast_stretch"] = ContrastStretchCheck.IsChecked == true;
        ocr["allow_confusables"] = ConfusablesCheck.IsChecked == true;

        // 대상 필드
        var fields = _config.Section("fields");
        fields["disabled"] = new JsonArray(_disableChecks
            .Where(pair => pair.Value.IsChecked == true)
            .Select(pair => (JsonNode)JsonValue.Create(pair.Key)!).ToArray());
        fields["custom"] = new JsonArray(_customFields
            .Where(vm => vm.Name.Trim().Length > 0 && vm.Pattern.Trim().Length > 0)
            .Select(vm => (JsonNode)new JsonObject
            {
                ["name"] = vm.Name.Trim(),
                ["pattern"] = vm.Pattern.Trim(),
                ["is_regex"] = vm.IsRegex,
                ["expected"] = int.TryParse(vm.Expected, out var count)
                    ? JsonValue.Create(count) : null,
            }).ToArray());

        // 바운딩 박스
        var overlay = _config.Section("overlay");
        overlay["thickness"] = ParseInt(OverlayThicknessBox.Text, 2, 1, 12);
        overlay["fill_alpha"] = ParseInt(OverlayAlphaBox.Text, 90, 0, 255);
        overlay["show_numbers"] = OverlayNumbersCheck.IsChecked == true;
        if (_config.StandardsRaw["field_colors"] is JsonObject colorNode)
        {
            foreach (var vm in _fieldColors)
                if (colorNode[vm.Field] is JsonArray rgba)
                {
                    rgba[0] = ParseInt(vm.R, 128, 0, 255);
                    rgba[1] = ParseInt(vm.G, 128, 0, 255);
                    rgba[2] = ParseInt(vm.B, 128, 0, 255);
                }
        }

        // 교정 사전 (그리드 내용으로 전체 교체)
        _corrections.ReplaceAll(_correctionRows
            .Where(vm => vm.Wrong.Trim().Length > 0 && vm.Right.Trim().Length > 0)
            .Select(vm => new CorrectionEntry(vm.Wrong.Trim(), vm.Right.Trim(),
                vm.Field.Trim().Length > 0 ? vm.Field.Trim() : null,
                vm.LearnedAt.Length > 0 ? vm.LearnedAt : null)));

        // AWS
        settings["aws"] = new JsonObject
        {
            ["region"] = AwsRegionBox.Text.Trim().Length > 0
                ? AwsRegionBox.Text.Trim() : "ap-northeast-2",
            ["profile"] = AwsProfileBox.Text.Trim(),
        };

        // 검사 기준
        if (CountsGrid.ItemsSource is DataView view
            && _config.StandardsRaw["standards"] is JsonObject standards)
        {
            foreach (DataRowView rowView in view)
            {
                var name = rowView["규격"]?.ToString() ?? "";
                if (standards[name] is not JsonObject spec) continue;
                var counts = spec["counts"]?.AsObject() ?? new JsonObject();
                foreach (var field in CountFields)
                    counts[field] = rowView[field] is int value ? value
                        : int.TryParse(rowView[field]?.ToString(), out var parsed) ? parsed : 0;
                spec["counts"] = counts;
            }
        }

        // 저장 설정
        if (double.TryParse(SaveScaleBox.Text, out var scale) && scale is > 0 and <= 1)
            settings["save_scale"] = scale;
        settings["jpeg_quality"] = ParseInt(SaveJpegQualityBox.Text, 90, 30, 100);
        settings["auto_save_default"] = AutoSaveDefaultCheck.IsChecked == true;

        _config.SaveSettings();
        _config.SaveStandards();
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}

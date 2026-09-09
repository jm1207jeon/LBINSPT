// 설정 — OCR/대상 필드/바운딩 박스/교정 사전/기준정보/저장까지 사용자 편집 가능.
// 고급 항목(컬럼 매핑 등)은 '설정 폴더 열기'로 JSON 직접 편집 (README 참고).
using System.Collections.ObjectModel;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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
        public string Standard { get; set; } = "";
    }

    public sealed class ZoneVm
    {
        public string Field { get; set; } = "";
        public string Standard { get; set; } = "";
        public string X { get; set; } = "0";
        public string Y { get; set; } = "0";
        public string W { get; set; } = "20";
        public string H { get; set; } = "10";
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

    public sealed class CharsetVm
    {
        public string Field { get; set; } = "";
        public string Allowed { get; set; } = "";
        public string Denied { get; set; } = "";
    }

    public sealed class SameValueVm
    {
        public string Name { get; set; } = "";
        public string Pattern { get; set; } = "";
        public string Min { get; set; } = "2";
    }

    public sealed class FormRuleVm
    {
        public string Name { get; set; } = "";
        public string Standard { get; set; } = "";
        public string X { get; set; } = "0";
        public string Y { get; set; } = "0";
        public string W { get; set; } = "20";
        public string H { get; set; } = "10";
        public string Pattern { get; set; } = "";
        public bool UseImage { get; set; }
    }

    private readonly ObservableCollection<CustomFieldVm> _customFields = [];
    private readonly ObservableCollection<ColorVm> _fieldColors = [];
    private readonly ObservableCollection<CorrectionVm> _correctionRows = [];
    private readonly ObservableCollection<CharsetVm> _charsetRows = [];
    private readonly ObservableCollection<SameValueVm> _sameValueRows = [];
    private readonly ObservableCollection<FormRuleVm> _formRuleRows = [];
    private readonly ObservableCollection<ZoneVm> _zoneRows = [];
    private readonly Dictionary<string, CheckBox> _disableChecks = [];

    private readonly GlyphLibrary _glyphs;
    private readonly WordMergeRules _merges;

    public sealed class MergeVm
    {
        public string Pattern { get; set; } = "";
    }

    private readonly ObservableCollection<MergeVm> _mergeRows = [];

    /// <summary>현재 무효한 입력란 (InputRules가 유지) — 비어 있어야 저장할 수 있다.</summary>
    private readonly HashSet<TextBox> _invalid = [];
    private bool _rulesWired;
    private string _noteDefault = "";

    public SettingsWindow(AppConfig config, OcrCorrections corrections,
                          GlyphLibrary glyphs, WordMergeRules merges)
    {
        InitializeComponent();
        _config = config;
        _corrections = corrections;
        _glyphs = glyphs;
        _merges = merges;
        LoadValues();
        Loaded += (_, _) => WireInputRules();
    }

    /// <summary>Tag="설정경로"가 붙은 TextBox를 논리 트리에서 찾아 InputRules에 자동 배선한다
    /// (탭이 아직 표시되지 않아도 논리 자식은 존재하므로 전 탭 일괄).</summary>
    private void WireInputRules()
    {
        if (_rulesWired) return;
        _rulesWired = true;
        _noteDefault = SettingsNote.Text;
        foreach (var box in TaggedTextBoxes(this))
            InputRules.AttachByTag(box, _invalid);
    }

    private static IEnumerable<TextBox> TaggedTextBoxes(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject node) continue;
            if (node is TextBox { Tag: string tag } box && tag.Length > 0) yield return box;
            foreach (var nested in TaggedTextBoxes(node)) yield return nested;
        }
    }

    /// <summary>무효 입력이 있으면 안내문을 붉게 바꾸고 해당 탭으로 옮겨 포커스 — 저장 차단(true).</summary>
    private bool BlockSaveIfInvalid()
    {
        if (_invalid.Count == 0)
        {
            SettingsNote.Text = _noteDefault.Length > 0 ? _noteDefault : SettingsNote.Text;
            SettingsNote.ClearValue(TextBlock.ForegroundProperty);
            SettingsNote.ClearValue(TextBlock.FontWeightProperty);
            return false;
        }
        var labels = string.Join(", ", _invalid.Select(InputRules.LabelOf).Distinct());
        SettingsNote.Text = $"저장 안 됨 — 붉게 표시된 입력값을 고치세요: {labels}";
        SettingsNote.Foreground = (System.Windows.Media.Brush)FindResource("StatusErrorBrush");
        SettingsNote.FontWeight = FontWeights.Bold;
        var first = _invalid.First();
        for (DependencyObject? node = first; node is not null; node = LogicalTreeHelper.GetParent(node))
            if (node is TabItem tab) { SettingsTabs.SelectedItem = tab; break; }
        // 탭 전환 직후에는 내용이 아직 그려지지 않았을 수 있으므로 레이아웃 뒤에 포커스
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            first.Focus();
            first.SelectAll();
        });
        return true;
    }

    private void UpdateGlyphStatus() =>
        GlyphStatusText.Text =
            $"학습: {_glyphs.CharCount}종 글자 / 템플릿 {_glyphs.TemplateCount}개";

    private void OnClearGlyphs(object sender, RoutedEventArgs e)
    {
        // 파괴적 동작: 건수 표시 + 기본 버튼 '아니오' (Enter 오조작 방지)
        if (!Dialogs.Confirm(this,
                $"학습된 글자 패턴을 모두 삭제할까요?\n" +
                $"글자 {_glyphs.CharCount}종 / 템플릿 {_glyphs.TemplateCount}개가 삭제되며 되돌릴 수 없습니다.\n" +
                "(삭제 전 [학습 데이터 내보내기]로 보관할 수 있습니다)",
                "패턴 초기화")) return;
        _glyphs.Clear();
        UpdateGlyphStatus();
    }

    private void OnExportLearning(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "학습 데이터 내보내기",
            FileName = $"LaVIS-학습데이터-{DateTime.Now:yyyyMMdd}{LearningBundle.Extension}",
            Filter = $"LaVIS 학습 데이터 (*{LearningBundle.Extension})|*{LearningBundle.Extension}",
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var info = LearningBundle.Export(dialog.FileName, _glyphs, _corrections);
            Dialogs.Info(this, 
                $"내보내기 완료\n글자 패턴: {info.GlyphChars}종 / 템플릿 {info.GlyphTemplates}개\n" +
                $"교정 사전: {info.Corrections}건\n\n이 파일을 다른 PC의 LaVIS에서 " +
                "[학습 데이터 가져오기]로 불러오면 동일한 인식 성능을 사용할 수 있습니다.",
                "학습 데이터 내보내기");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Dialogs.Error(this, $"내보내기 실패: {ex.Message}", "학습 데이터 내보내기");
        }
    }

    // ---------------- 프리셋 (규칙 + 학습 데이터 통째로) ----------------

    private void OnExportPreset(object sender, RoutedEventArgs e)
    {
        var name = InputDialog.Ask(this, "프리셋 내보내기", "프리셋 이름 (예: A라인 스텐트 2026-09):",
                                   _config.GetString("last_preset_name", ""),
                                   "이름은 파일 안 manifest에 기록되어 가져올 때 표시됩니다.");
        if (name is null) return;
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var dialog = new SaveFileDialog
        {
            Title = "프리셋 내보내기",
            Filter = $"LaVIS 프리셋 (*{PresetBundle.Extension})|*{PresetBundle.Extension}",
            FileName = $"LaVIS-preset_{safe}_{DateTime.Now:yyyyMMdd}{PresetBundle.Extension}",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var manifest = PresetBundle.Export(dialog.FileName, _config, name);
            _config.Settings["last_preset_name"] = name;
            Dialogs.Info(this,
                $"프리셋 '{manifest.Name}' 내보내기 완료\n포함: {string.Join(", ", manifest.Contents)}\n\n" +
                "다른 PC의 LaVIS 설정 › 규격·양식 감지 › [프리셋 가져오기]로 적용할 수 있습니다.",
                "프리셋 내보내기");
        }
        catch (Exception ex)
        {
            AppLog.Error("프리셋 내보내기 실패", ex);
            Dialogs.Error(this, $"내보내기 실패: {ex.Message}", "프리셋 내보내기");
        }
    }

    private void OnImportPreset(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "프리셋 가져오기",
            Filter = $"LaVIS 프리셋 (*{PresetBundle.Extension})|*{PresetBundle.Extension}|모든 파일|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        PresetManifest manifest;
        try { manifest = PresetBundle.Inspect(dialog.FileName); }
        catch (Exception ex)
        {
            Dialogs.Warn(this, ex.Message, "프리셋 가져오기");
            return;
        }
        // 되돌리기 어려운 동작: 내용을 보여 주고 기본 버튼 '아니오'
        var proceed = Dialogs.Confirm(this,
            $"프리셋 '{manifest.Name}' ({manifest.ExportedAt})\n" +
            (manifest.Note.Length > 0 ? $"메모: {manifest.Note}\n" : "") +
            $"포함: {string.Join(", ", manifest.Contents)}\n\n" +
            "현재 규격·필드 규칙·영역·양식 감지·학습 데이터가 이 프리셋으로 교체됩니다.\n" +
            "(현재 상태는 데이터 폴더의 backup-preset-시각 폴더에 보관됩니다)\n" +
            "적용을 위해 LaVIS가 다시 시작됩니다. 계속할까요?",
            "프리셋 가져오기");
        if (!proceed) return;
        try
        {
            var applied = PresetBundle.Import(dialog.FileName, _config);
            AppLog.Info($"프리셋 적용: {applied.Name} (백업 {applied.BackupDirectory})");
            Dialogs.Info(this,
                $"프리셋 '{applied.Name}' 적용 완료. 이전 상태는\n{applied.BackupDirectory}\n에 보관되었습니다.\n\n확인을 누르면 LaVIS가 다시 시작됩니다.",
                "프리셋 가져오기");
            DialogResult = false;   // 설정 창의 미저장 편집은 버림 (재시작으로 새 상태 로드)
            App.Restart();
        }
        catch (Exception ex)
        {
            AppLog.Error("프리셋 가져오기 실패", ex);
            Dialogs.Error(this, $"가져오기 실패: {ex.Message}\n현재 설정은 변경되지 않았거나 backup-preset 폴더에서 복원할 수 있습니다.",
                "프리셋 가져오기");
        }
    }

    private void OnBackupDataDir(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "데이터 폴더 백업",
            Filter = "zip 파일 (*.zip)|*.zip",
            FileName = $"LaVIS-data-backup_{DateTime.Now:yyyyMMdd-HHmm}.zip",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (File.Exists(dialog.FileName)) File.Delete(dialog.FileName);
            // 이력 DB(WAL)와 캐시는 제외 — 열려 있는 DB 파일은 복사가 불완전할 수 있고 캐시는 재생성 가능
            using var zip = System.IO.Compression.ZipFile.Open(dialog.FileName,
                System.IO.Compression.ZipArchiveMode.Create);
            var count = 0;
            foreach (var file in Directory.GetFiles(_config.Directory))
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith("history.sqlite3", StringComparison.OrdinalIgnoreCase)) continue;
                zip.CreateEntryFromFile(file, name);
                count++;
            }
            Dialogs.Info(this, $"파일 {count}개를 백업했습니다.\n{dialog.FileName}\n(검사 이력 DB와 OCR 캐시는 제외)",
                "데이터 폴더 백업");
        }
        catch (Exception ex)
        {
            AppLog.Error("데이터 폴더 백업 실패", ex);
            Dialogs.Error(this, $"백업 실패: {ex.Message}", "데이터 폴더 백업");
        }
    }

    private void OnImportLearning(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "학습 데이터 가져오기",
            Filter = $"LaVIS 학습 데이터 (*{LearningBundle.Extension})|*{LearningBundle.Extension}|" +
                     "모든 파일 (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            // 가져오기 전 교정 키를 기억해 두고, 새로 생긴 행만 앰버로 번쩍인다 (UDInspect FlashRow 규범)
            var before = _correctionRows
                .Select(vm => (vm.Wrong, vm.Right, vm.Field)).ToHashSet();
            var info = LearningBundle.Import(dialog.FileName, _glyphs, _corrections);
            UpdateGlyphStatus();
            _correctionRows.Clear();
            foreach (var entry in _corrections.Entries)
                _correctionRows.Add(new CorrectionVm
                {
                    Wrong = entry.Wrong, Right = entry.Right,
                    Field = entry.Field ?? "", LearnedAt = entry.LearnedAt ?? "",
                });
            foreach (var vm in _correctionRows)
                if (!before.Contains((vm.Wrong, vm.Right, vm.Field)))
                    UiFx.FlashRow(CorrectionsGrid, vm);
            Dialogs.Info(this,
                $"가져오기 완료\n새 글자 템플릿 {info.GlyphTemplates}개 병합 " +
                $"(이미 있는 패턴은 유지)\n교정 사전 {info.Corrections}건 반영",
                "학습 데이터 가져오기");
        }
        catch (Exception ex) when (ex is System.IO.IOException
            or System.IO.InvalidDataException or UnauthorizedAccessException)
        {
            Dialogs.Error(this, $"가져오기 실패: {ex.Message}", "학습 데이터 가져오기");
        }
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
        OcrEngineCombo.SelectedIndex = _config.Section("ocr")["engine"]
            ?.GetValue<string>() switch
        { "pattern" => 1, "onnx" => 2, _ => 0 };
        UpdateGlyphStatus();
        RenderZoomBox.Text = _config.GetDouble("pdf_render_zoom", 4.0).ToString("F1");
        OcrMaxDimBox.Text = _config.SectionInt("ocr", "max_dimension", 2000).ToString();
        OcrJpegQualityBox.Text = _config.SectionInt("ocr", "jpeg_quality", 85).ToString();
        OcrMinConfBox.Text = _config.SectionInt("ocr", "min_confidence", 0).ToString();
        ContrastStretchCheck.IsChecked = _config.SectionBool("ocr", "contrast_stretch", false);
        ConfusablesCheck.IsChecked = _config.SectionBool("ocr", "allow_confusables", true);
        CropLabelCheck.IsChecked = _config.SectionBool("preprocess", "crop_label", true);
        QualityAlarmCheck.IsChecked = _config.SectionBool("ocr", "quality_alarm", true);
        LowWordConfBox.Text = _config.SectionInt("ocr", "low_word_confidence", 70).ToString();
        LowAvgConfBox.Text = _config.SectionInt("ocr", "low_avg_confidence", 80).ToString();

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
                        Standard = obj["standard"]?.GetValue<string>() ?? "",
                    });
        CustomFieldsGrid.ItemsSource = _customFields;
        if (_config.Section("fields")["zones"] is JsonArray zonesArray)
            foreach (var node in zonesArray)
                if (node is JsonObject obj)
                {
                    string At(int i) => obj["region"] is JsonArray r && r.Count == 4
                        && r[i]!.AsValue().TryGetValue<double>(out var zv)
                        ? zv.ToString("0.#") : "0";
                    _zoneRows.Add(new ZoneVm
                    {
                        Field = obj["field"]?.GetValue<string>() ?? "",
                        Standard = obj["standard"]?.GetValue<string>() ?? "",
                        X = At(0), Y = At(1), W = At(2), H = At(3),
                    });
                }
        ZonesGrid.ItemsSource = _zoneRows;
        if (_config.Section("fields")["charsets"] is JsonArray charsetArray)
            foreach (var node in charsetArray)
                if (node is JsonObject obj)
                    _charsetRows.Add(new CharsetVm
                    {
                        Field = obj["field"]?.GetValue<string>() ?? "",
                        Allowed = obj["allowed"]?.GetValue<string>() ?? "",
                        Denied = obj["denied"]?.GetValue<string>() ?? "",
                    });
        CharsetsGrid.ItemsSource = _charsetRows;
        if (_config.Section("fields")["same_value"] is JsonArray sameValueArray)
            foreach (var node in sameValueArray)
                if (node is JsonObject obj)
                    _sameValueRows.Add(new SameValueVm
                    {
                        Name = obj["name"]?.GetValue<string>() ?? "",
                        Pattern = obj["pattern"]?.GetValue<string>() ?? "",
                        Min = obj["min_instances"] is { } min
                            && min.AsValue().TryGetValue<int>(out var v) ? v.ToString() : "2",
                    });
        SameValueGrid.ItemsSource = _sameValueRows;
        // 자동 학습 모듈 (기본 꺼짐) — label_type은 구버전 type_learning.enabled를 이어받는다
        GlyphLearningCheck.IsChecked = _config.LearningEnabled("glyph_patterns");
        TypeLearningCheck.IsChecked = _config.LearningEnabled("label_type");
        SameValueLearningCheck.IsChecked = _config.LearningEnabled("same_value_layout");
        MasterLearningCheck.IsChecked = _config.LearningEnabled("master_db");
        TypeMinSamplesBox.Text = _config.SectionInt("type_learning", "min_samples", 5).ToString();

        // 라벨 양식 자동 감지
        if (_config.Section("label_forms")["rules"] is JsonArray formArray)
            foreach (var node in formArray)
                if (node is JsonObject obj)
                {
                    string At(int i) => obj["region"] is JsonArray r && r.Count == 4
                        && r[i]!.AsValue().TryGetValue<double>(out var v)
                        ? v.ToString("0.#") : "0";
                    _formRuleRows.Add(new FormRuleVm
                    {
                        Name = obj["name"]?.GetValue<string>() ?? "",
                        Standard = obj["standard"]?.GetValue<string>() ?? "",
                        X = At(0), Y = At(1), W = At(2), H = At(3),
                        Pattern = obj["text_pattern"]?.GetValue<string>() ?? "",
                        UseImage = obj["use_image"]?.GetValue<bool>() ?? false,
                    });
                }
        FormRulesGrid.ItemsSource = _formRuleRows;

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
        foreach (var rule in _merges.Rules)
            _mergeRows.Add(new MergeVm { Pattern = string.Join(" ", rule) });
        MergesGrid.ItemsSource = _mergeRows;

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
        CsvTextProtectCheck.IsChecked = _config.SectionBool("export", "csv_text_protect", true);
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

    /// <summary>범위표(Core SettingRanges)의 단일 출처로 정수 해석 — 범위 밖은 clamp, 해석 불가는 기본값.
    /// 무효 입력은 BlockSaveIfInvalid가 먼저 막으므로 여기에는 정상 값만 도달한다.</summary>
    private static int RangeInt(string path, string text) =>
        SettingRanges.Find(path)!.ParseInt(text);

    private static double RangeDouble(string path, string text) =>
        SettingRanges.Find(path)!.ParseDouble(text);

    private void OnSave(object sender, RoutedEventArgs e)
    {
        WireInputRules();   // Loaded 전에 저장을 누른 경우에도 검증이 동작하도록
        if (BlockSaveIfInvalid()) return;   // 붉은 입력란이 있으면 창을 닫지 않고 안내

        foreach (var grid in new[] { CustomFieldsGrid, FieldColorsGrid, CorrectionsGrid,
                                     CountsGrid, CharsetsGrid, SameValueGrid,
                                     FormRulesGrid, ZonesGrid, MergesGrid })
            grid.CommitEdit(DataGridEditingUnit.Row, true);

        var settings = _config.Settings;
        // 일반
        settings["save_directory"] = SaveDirBox.Text.Trim();
        settings["shelf_life_months"] = RangeInt("shelf_life_months", ShelfLifeBox.Text);
        settings["prefetch_policy"] = PrefetchCombo.SelectedIndex switch
        {
            1 => JsonValue.Create(1), 2 => JsonValue.Create(2),
            3 => JsonValue.Create(5), 4 => JsonValue.Create(0),
            _ => JsonValue.Create("all"),
        };
        settings["pdf_render_zoom"] = RangeDouble("pdf_render_zoom", RenderZoomBox.Text);

        // OCR
        var ocr = _config.Section("ocr");
        ocr["engine"] = OcrEngineCombo.SelectedIndex switch
        { 1 => "pattern", 2 => "onnx", _ => "aws" };
        ocr["max_dimension"] = RangeInt("ocr.max_dimension", OcrMaxDimBox.Text);
        ocr["jpeg_quality"] = RangeInt("ocr.jpeg_quality", OcrJpegQualityBox.Text);
        ocr["min_confidence"] = RangeInt("ocr.min_confidence", OcrMinConfBox.Text);
        ocr["contrast_stretch"] = ContrastStretchCheck.IsChecked == true;
        ocr["allow_confusables"] = ConfusablesCheck.IsChecked == true;
        ocr["quality_alarm"] = QualityAlarmCheck.IsChecked == true;
        ocr["low_word_confidence"] = RangeInt("ocr.low_word_confidence", LowWordConfBox.Text);
        ocr["low_avg_confidence"] = RangeInt("ocr.low_avg_confidence", LowAvgConfBox.Text);

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
                ["standard"] = vm.Standard.Trim(),
            }).ToArray());
        static double Pct(string text) =>
            double.TryParse(text, out var v) ? Math.Clamp(v, 0, 100) : 0;
        fields["zones"] = new JsonArray(_zoneRows
            .Where(vm => vm.Field.Trim().Length > 0)
            .Select(vm => (JsonNode)new JsonObject
            {
                ["field"] = vm.Field.Trim(),
                ["standard"] = vm.Standard.Trim(),
                ["region"] = new JsonArray(
                    JsonValue.Create(Pct(vm.X)), JsonValue.Create(Pct(vm.Y)),
                    JsonValue.Create(Math.Max(1, Pct(vm.W))),
                    JsonValue.Create(Math.Max(1, Pct(vm.H)))),
            }).ToArray());
        _config.Section("preprocess")["crop_label"] = CropLabelCheck.IsChecked == true;
        fields["charsets"] = new JsonArray(_charsetRows
            .Where(vm => vm.Field.Trim().Length > 0)
            .Select(vm => (JsonNode)new JsonObject
            {
                ["field"] = vm.Field.Trim(),
                ["allowed"] = vm.Allowed.Trim(),
                ["denied"] = vm.Denied.Trim(),
            }).ToArray());
        fields["same_value"] = new JsonArray(_sameValueRows
            .Where(vm => vm.Name.Trim().Length > 0 && vm.Pattern.Trim().Length > 0)
            .Select(vm => (JsonNode)new JsonObject
            {
                ["name"] = vm.Name.Trim(),
                ["pattern"] = vm.Pattern.Trim(),
                ["min_instances"] = RangeInt("fields.same_value[].min_instances", vm.Min),
            }).ToArray());
        var typeLearning = _config.Section("type_learning");
        typeLearning.Remove("enabled");   // learning.label_type로 이전
        typeLearning["min_samples"] = RangeInt("type_learning.min_samples", TypeMinSamplesBox.Text);
        var learning = _config.Section("learning");
        learning["glyph_patterns"] = GlyphLearningCheck.IsChecked == true;
        learning["label_type"] = TypeLearningCheck.IsChecked == true;
        learning["same_value_layout"] = SameValueLearningCheck.IsChecked == true;
        learning["master_db"] = MasterLearningCheck.IsChecked == true;

        // 라벨 양식 자동 감지 규칙
        static double ParsePercent(string text) =>
            double.TryParse(text, out var v) ? Math.Clamp(v, 0, 100) : 0;
        _config.Section("label_forms")["rules"] = new JsonArray(_formRuleRows
            .Where(vm => vm.Name.Trim().Length > 0
                && (vm.Pattern.Trim().Length > 0 || vm.UseImage))
            .Select(vm => (JsonNode)new JsonObject
            {
                ["name"] = vm.Name.Trim(),
                ["standard"] = vm.Standard.Trim(),
                ["region"] = new JsonArray(
                    JsonValue.Create(ParsePercent(vm.X)),
                    JsonValue.Create(ParsePercent(vm.Y)),
                    JsonValue.Create(Math.Max(1, ParsePercent(vm.W))),
                    JsonValue.Create(Math.Max(1, ParsePercent(vm.H)))),
                ["text_pattern"] = vm.Pattern.Trim(),
                ["use_image"] = vm.UseImage,
            }).ToArray());

        // 바운딩 박스
        var overlay = _config.Section("overlay");
        overlay["thickness"] = RangeInt("overlay.thickness", OverlayThicknessBox.Text);
        overlay["fill_alpha"] = RangeInt("overlay.fill_alpha", OverlayAlphaBox.Text);
        overlay["show_numbers"] = OverlayNumbersCheck.IsChecked == true;
        if (_config.StandardsRaw["field_colors"] is JsonObject colorNode)
        {
            const string rgbaPath = "overlay.colors[].rgba";
            foreach (var vm in _fieldColors)
                if (colorNode[vm.Field] is JsonArray rgba)
                {
                    rgba[0] = RangeInt(rgbaPath, vm.R);
                    rgba[1] = RangeInt(rgbaPath, vm.G);
                    rgba[2] = RangeInt(rgbaPath, vm.B);
                }
        }

        // 단어 병합 패턴 (그리드 내용으로 전체 교체)
        _merges.ReplaceAll(_mergeRows
            .Select(vm => (IEnumerable<string>)vm.Pattern.Split(' ',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));

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
        settings["save_scale"] = RangeDouble("save_scale", SaveScaleBox.Text);
        settings["jpeg_quality"] = RangeInt("jpeg_quality", SaveJpegQualityBox.Text);
        settings["auto_save_default"] = AutoSaveDefaultCheck.IsChecked == true;
        _config.Section("export")["csv_text_protect"] = CsvTextProtectCheck.IsChecked == true;

        _config.SaveSettings();
        _config.SaveStandards();
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}

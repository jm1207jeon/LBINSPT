// 라벨 검사 탭 — 파이썬 inspector_page.py 포팅.
// PDF 로드 시 전 페이지 백그라운드 선행 OCR(프리페치) + 캐시로 페이지 이동 무지연 표시.
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using LabelSuite.App.Services;
using LabelSuite.Core;
using Microsoft.Win32;
using SkiaSharp;

namespace LabelSuite.App.Views;

public partial class InspectorView : UserControl
{
    // 전처리 서명 — 라벨 정렬 옵션이 바뀌면 OCR 캐시를 분리한다
    // 주의: 정렬(크롭/기울기) 알고리즘이 바뀌면 버전을 올려야 한다 —
    // 이전 알고리즘의 크롭 기준으로 저장된 OCR 좌표가 재사용되면 박스가 어긋난다.
    private string PreprocessSig =>
        _config?.SectionBool("preprocess", "crop_label", true) == true
            ? "crop-v2" : "none-v1";

    private AppConfig _config = null!;
    private HistoryDb? _history;
    private StandardsBundle _standards = null!;
    private InspectionEngine _engine = null!;
    private TextractClient _textract = null!;
    private OcrCache _cache = null!;
    private PrefetchWorker _worker = null!;
    private readonly PdfDoc _pdf = new();

    private List<LabelRecord> _records = [];
    private int _currentPage;
    private readonly Dictionary<int, PageAnalysis> _analyses = [];
    private readonly Dictionary<int, InspectionOutcome> _outcomes = [];
    /// <summary>결과 이미지가 저장된 페이지 — '● 결과 미저장 / ✓ 저장됨' 표시와 종료 확인용.</summary>
    private readonly HashSet<int> _savedPages = [];
    /// <summary>페이지 → 마지막으로 저장된 판정 서명(InspectionOutcome.Signature). 같은 판정을
    /// 다시 방문·재검사해도 자동 저장이 중복 파일·이력을 만들지 않게 한다 (A-05).</summary>
    private readonly Dictionary<int, string> _savedSignature = [];
    /// <summary>페이지 → 마지막으로 저장된 결과 파일 경로 (재저장 확인 문구·CSV IMAGE_PATH).</summary>
    private readonly Dictionary<int, string> _savedFile = [];
    /// <summary>라벨에서 목록 LOT을 읽지 못한 페이지 — 슬롯 '?' 표시.</summary>
    private readonly HashSet<int> _lotUnmatchedPages = [];
    private readonly HashSet<int> _manualLotPages = [];   // 사용자가 직접 LOT 고른 페이지
    private readonly Dictionary<int, int> _pageLotChoice = [];  // 페이지 → 수동 선택 인덱스
    private readonly Dictionary<int, string> _manualStandardPages = [];  // 페이지 → 수동 규격

    // 라벨 정렬(이형지 크롭·기울기 보정) 결과 캐시 — 반환 비트맵은 캐시 소유
    private readonly object _procLock = new();
    private readonly Dictionary<int, (SKBitmap Image, double Angle, bool Cropped,
                                      bool Owned)> _processed = [];
    private readonly LinkedList<int> _procOrder = [];
    private string? _selectedStandard;
    private bool _suppressEvents;
    private double _zoom = 1.0;
    private SKBitmap? _displayed;
    private Point _panStart;
    private bool _panning;

    /// <summary>상태바 메시지 (정보/경고/오류 3단계 — MainWindow가 색·고정 시간을 처리).</summary>
    public event Action<string, StatusLevel>? StatusMessage;
    public event Action<bool, string>? AwsStatusChanged;
    /// <summary>OCR 진행률 (완료 페이지, 전체 페이지) — 상태바 진행 표시용.</summary>
    public event Action<int, int>? ProgressChanged;

    // StatusMessage 이벤트로 전달 — 자기 호출 금지 (치환 스크립트 사고 방지용 주석)
    private void Status(string message, StatusLevel level = StatusLevel.Info) =>
        StatusMessage?.Invoke(message, level);

    private OcrCorrections _corrections = null!;
    private WordMergeRules _merges = null!;
    private GlyphOcrEngine _glyphEngine = null!;
    private static readonly Lazy<OnnxOcrEngine> OnnxEngine = new(() => new OnnxOcrEngine());

    private SameValueChecker _sameValue = null!;
    private List<SameValueRule> _sameValueRules = [];
    private LabelFormDetector _formDetector = null!;
    private List<LabelFormRule> _formRules = [];
    private LabelTypeProfiler _profiler = null!;
    private readonly HashSet<int> _typeLearnedPages = [];   // 페이지당 1회만 표본 축적
    private readonly HashSet<int> _typeAlarmPages = [];     // 페이지당 1회만 알람

    public InspectorView() => InitializeComponent();

    public void Initialize(AppConfig config, HistoryDb history,
                           OcrCorrections corrections, GlyphLibrary glyphs,
                           WordMergeRules merges)
    {
        _config = config;
        _history = history;
        _corrections = corrections;
        _merges = merges;
        _glyphEngine = new GlyphOcrEngine(glyphs);
        _standards = StandardsBundle.Load(config);
        _engine = BuildEngine();
        _textract = MakeTextract();
        _sameValue = new SameValueChecker(
            Path.Combine(AppConfig.DataDir(), "same_value_layouts.json"), corrections);
        _sameValueRules = LoadSameValueRules();
        _formDetector = new LabelFormDetector(
            Path.Combine(AppConfig.DataDir(), "form_templates.json"));
        _formRules = LoadFormRules();
        UpdateFormRowVisibility();
        _profiler = new LabelTypeProfiler(
            Path.Combine(AppConfig.DataDir(), "label_profiles.json"))
        { MinSamples = _config.SectionInt("type_learning", "min_samples", 5) };
        _cache = new OcrCache(Path.Combine(AppConfig.DataDir(), "ocr_cache"),
                              _config.GetInt("ocr_cache_max_entries", 500));
        _pdf.RenderZoom = _config.GetDouble("pdf_render_zoom", 4.0);
        _pdf.CachePages = _config.GetInt("page_image_cache_pages", 6);
        _loadingConfig = true;
        AutoSaveCheck.IsChecked = _config.GetBool("auto_save_default", false);
        ShowZonesCheck.IsChecked = _config.SectionBool("overlay", "show_zones", true);
        _loadingConfig = false;
        UpdateLegend();

        _worker = new PrefetchWorker(async image =>
        {
            // 이미지 보정 옵션 (저대비 인쇄 대응) — OCR/바코드 분석에만 적용, 표시는 원본
            SKBitmap analysisImage = image;
            var stretched = false;
            if (_config.SectionBool("ocr", "contrast_stretch", false))
            {
                analysisImage = ImagePreprocess.StretchContrast(image);
                stretched = true;
            }
            try
            {
                var engine = CurrentOcrEngine();
                var words = await engine.DetectWordsAsync(analysisImage);
                // AWS 결과는 신뢰 학습원 — 패턴 라이브러리 자동 축적
                if (engine.Id == "aws" && words.Count > 0)
                {
                    try { _glyphEngine.LearnFrom(analysisImage, words); }
                    catch (Exception) { /* 학습 실패는 검사에 영향 없음 */ }
                }
                var barcodes = BarcodeDetector.Detect(analysisImage)
                    .Select(hit => hit with
                    { Grade = BarcodeGrader.Grade(image, hit.Bbox).Display })
                    .ToList();
                return new PageAnalysis { Words = words, Barcodes = barcodes };
            }
            finally
            {
                if (stretched) analysisImage.Dispose();
            }
        });
        _worker.PageDone += (gen, page, key, analysis) =>
            Dispatcher.Invoke(() => OnPageDone(gen, page, key, analysis));
        _worker.PageFailed += (gen, page, message) =>
            Dispatcher.Invoke(() => OnPageFailed(gen, page, message));

        PopulateStandardButtons();
        _analysisSig = CurrentAnalysisSig();
        _ = CheckAwsAsync();
        // 저장 폴더 접근 확인(비차단) — 네트워크 드라이브가 끊겨 있으면 자동 저장을 미리 해제해 기록 유실을 막는다
        if (AutoSaveCheck.IsChecked == true) _ = VerifySaveDirAsync(disableAutoSaveOnFailure: true);
    }

    /// <summary>범례 '합격 필드' 견본을 설정의 필드 색(LOT)으로 채운다 (Initialize/ApplyConfig).</summary>
    private void UpdateLegend()
    {
        if (!_standards.FieldColors.TryGetValue("LOT", out var c))
        {
            if (_standards.FieldColors.Count == 0) return;
            c = _standards.FieldColors.Values.First();
        }
        // 설정 데이터(필드 색)에서 오는 색 — 토큰이 아니라 사용자 설정값이라 코드에서 만든다
        var brush = new SolidColorBrush(Color.FromArgb(255, c.R, c.G, c.B));
        LegendFieldSwatch.Fill = brush;
        LegendFieldSwatch.Stroke = brush;
    }

    private TextractClient MakeTextract()
    {
        var aws = _config.Settings["aws"]?.AsObject();
        return new TextractClient(
            aws?["region"]?.GetValue<string>() ?? "ap-northeast-2",
            aws?["profile"]?.GetValue<string>() is { Length: > 0 } profile ? profile : null)
        {
            MaxDimension = _config.SectionInt("ocr", "max_dimension", 2000),
            JpegQuality = _config.SectionInt("ocr", "jpeg_quality", 85),
            MinConfidence = _config.SectionInt("ocr", "min_confidence", 0),
        };
    }

    /// <summary>설정(제외 필드/커스텀 필드/혼동 매칭/교정 사전)을 반영한 엔진 생성.</summary>
    private InspectionEngine BuildEngine()
    {
        var fields = _config.Section("fields");
        var disabled = new HashSet<string>();
        if (fields["disabled"] is System.Text.Json.Nodes.JsonArray disabledArray)
            foreach (var node in disabledArray)
                if (node?.GetValue<string>() is { Length: > 0 } name) disabled.Add(name);
        var custom = new List<CustomFieldDef>();
        if (fields["custom"] is System.Text.Json.Nodes.JsonArray customArray)
            foreach (var node in customArray)
            {
                if (node is not System.Text.Json.Nodes.JsonObject obj) continue;
                custom.Add(new CustomFieldDef(
                    obj["name"]?.GetValue<string>() ?? "",
                    obj["pattern"]?.GetValue<string>() ?? "",
                    obj["is_regex"]?.GetValue<bool>() ?? false,
                    obj["expected"] is { } exp
                        && exp.AsValue().TryGetValue<int>(out var count) ? count : null,
                    obj["standard"]?.GetValue<string>()));
            }
        var zones = new List<FieldZone>();
        if (fields["zones"] is System.Text.Json.Nodes.JsonArray zoneArray)
            foreach (var node in zoneArray)
            {
                if (node is not System.Text.Json.Nodes.JsonObject obj) continue;
                double At(int i) => obj["region"] is System.Text.Json.Nodes.JsonArray r
                    && r.Count == 4 && r[i]!.AsValue().TryGetValue<double>(out var v)
                    ? Math.Clamp(v / 100.0, 0, 1) : 0;
                zones.Add(new FieldZone(
                    obj["field"]?.GetValue<string>() ?? "",
                    obj["standard"]?.GetValue<string>() ?? "",
                    (At(0), At(1), Math.Max(0.01, At(2)), Math.Max(0.01, At(3)))));
            }
        var charsetRules = new List<FieldCharsetRule>();
        if (fields["charsets"] is System.Text.Json.Nodes.JsonArray charsetArray)
            foreach (var node in charsetArray)
                if (node is System.Text.Json.Nodes.JsonObject obj)
                    charsetRules.Add(new FieldCharsetRule(
                        obj["field"]?.GetValue<string>() ?? "",
                        obj["allowed"]?.GetValue<string>() ?? "",
                        obj["denied"]?.GetValue<string>() ?? ""));
        return new InspectionEngine(_standards, new InspectionOptions
        {
            DisabledFields = disabled,
            CustomFields = custom,
            AllowConfusables = _config.SectionBool("ocr", "allow_confusables", true),
            Corrections = _corrections,
            Charsets = new FieldCharsets(charsetRules),
            Zones = zones,
            Merges = _merges,
        });
    }

    /// <summary>설정의 라벨 양식 감지 규칙 (label_forms.rules — 영역은 %).</summary>
    private List<LabelFormRule> LoadFormRules()
    {
        var rules = new List<LabelFormRule>();
        if (_config.Section("label_forms")["rules"] is System.Text.Json.Nodes.JsonArray array)
            foreach (var node in array)
            {
                if (node is not System.Text.Json.Nodes.JsonObject obj) continue;
                double At(int i) => obj["region"] is System.Text.Json.Nodes.JsonArray r
                    && r.Count == 4 && r[i]!.AsValue().TryGetValue<double>(out var v)
                    ? Math.Clamp(v / 100.0, 0, 1) : 0;
                rules.Add(new LabelFormRule(
                    obj["name"]?.GetValue<string>() ?? "",
                    obj["standard"]?.GetValue<string>() ?? "",
                    (At(0), At(1), Math.Max(0.01, At(2)), Math.Max(0.01, At(3))),
                    obj["text_pattern"]?.GetValue<string>() ?? "",
                    obj["use_image"]?.GetValue<bool>() ?? false));
            }
        return rules;
    }

    /// <summary>현재 페이지의 규칙 영역을 양식 이미지 템플릿으로 학습.</summary>
    private void OnLearnForm(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!_pdf.IsOpen)
        {
            Dialogs.Info(this, "PDF를 먼저 여세요.", "양식 학습");
            return;
        }
        var imageRules = _formRules
            .Where(r => r.UseImage && r.Name.Trim().Length > 0).ToList();
        if (imageRules.Count == 0)
        {
            Dialogs.Info(this,
                "'이미지 사용'이 켜진 양식 규칙이 없습니다.\n" +
                "설정 → 라벨 양식 자동 감지에서 규칙을 추가하세요.", "양식 학습");
            return;
        }
        var combo = new ComboBox
        {
            ItemsSource = imageRules.Select(r => r.Name).ToList(),
            SelectedIndex = 0, MinWidth = 240, Margin = new Thickness(0, 8, 0, 8),
        };
        var okButton = new Button
        {
            Content = "이 양식으로 학습", MinWidth = 110, IsDefault = true,
            Style = (Style)FindResource("PrimaryButton"),
            ToolTip = "현재 페이지의 규칙 영역 이미지를 이 양식의 기준 템플릿으로 저장합니다 (기존 템플릿은 교체)",
        };
        var cancelButton = new Button { Content = "취소", IsCancel = true, MinWidth = 70 };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock
        { Text = "현재 페이지를 어느 양식의 기준 이미지로 학습할까요?" });
        combo.ToolTip = "설정 › 규격·양식 감지 › 라벨 양식 자동 감지에서 '이미지' 사용이 켜진 규칙";
        panel.Children.Add(combo);
        panel.Children.Add(buttons);
        var dialog = new Window
        {
            Title = "양식 이미지 학습", Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            Owner = Window.GetWindow(this),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
        };
        okButton.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() != true || combo.SelectedIndex < 0) return;

        var rule = imageRules[combo.SelectedIndex];
        var image = RenderProcessed(_currentPage);   // 캐시 소유 — 해제 금지
        if (_formDetector.LearnTemplate(rule, image))
        {
            Status(
                $"양식 '{rule.Name}' 기준 이미지 학습 완료 " +
                $"(영역 {rule.Region.X * 100:F0}%,{rule.Region.Y * 100:F0}% " +
                $"{rule.Region.W * 100:F0}x{rule.Region.H * 100:F0}%)");
            ReinspectCurrent();
        }
        else
            Dialogs.Warn(this, "규칙 영역이 너무 작습니다. 설정에서 영역(%)을 확인하세요.",
                         "양식 학습");
    }

    /// <summary>설정의 동일값 패턴 규칙 (fields.same_value).</summary>
    private List<SameValueRule> LoadSameValueRules()
    {
        var rules = new List<SameValueRule>();
        if (_config.Section("fields")["same_value"] is System.Text.Json.Nodes.JsonArray array)
            foreach (var node in array)
                if (node is System.Text.Json.Nodes.JsonObject obj)
                    rules.Add(new SameValueRule(
                        obj["name"]?.GetValue<string>() ?? "",
                        obj["pattern"]?.GetValue<string>() ?? "",
                        obj["min_instances"] is { } min
                            && min.AsValue().TryGetValue<int>(out var v) ? v : 2));
        return rules;
    }

    private OverlayStyle CurrentOverlayStyle() => new(
        Thickness: _config.SectionInt("overlay", "thickness", 2),
        FillAlpha: (byte)Math.Clamp(_config.SectionInt("overlay", "fill_alpha", 90), 0, 255),
        ShowNumbers: _config.SectionBool("overlay", "show_numbers", false));

    private string? _analysisSig;

    /// <summary>분석 결과의 유효성을 좌우하는 설정 서명 — 바뀌면 렌더/분석 무효화.</summary>
    private string CurrentAnalysisSig() =>
        $"{PreprocessSig}|eng={CurrentOcrEngine().Id}" +
        $"|cs={_config.SectionBool("ocr", "contrast_stretch", false)}" +
        $"|zoom={_config.GetDouble("pdf_render_zoom", 4.0)}";

    private void UpdateFormRowVisibility() =>
        FormRow.Visibility = _formRules.Count > 0
            || _engine.Options.CustomFields.Any(c => c.Standard is { Length: > 0 })
            ? Visibility.Visible : Visibility.Collapsed;

    public void ApplyConfig()
    {
        _standards = StandardsBundle.Load(_config);
        _engine = BuildEngine();
        _textract = MakeTextract();
        _sameValueRules = LoadSameValueRules();
        _formRules = LoadFormRules();
        UpdateFormRowVisibility();
        _profiler.MinSamples = _config.SectionInt("type_learning", "min_samples", 5);
        _pdf.RenderZoom = _config.GetDouble("pdf_render_zoom", 4.0);
        PopulateStandardButtons();
        UpdateLegend();
        _loadingConfig = true;
        ShowZonesCheck.IsChecked = _config.SectionBool("overlay", "show_zones", true);
        _loadingConfig = false;
        _ = CheckAwsAsync();

        var signature = CurrentAnalysisSig();
        if (_analysisSig != signature)
        {
            // 렌더 배율/대비 보정/엔진/정렬이 바뀜 → 이전 배율의 비트맵·분석을
            // 재사용하면 좌표가 어긋나고 메모리가 폭주한다. 전부 무효화 후 재분석.
            _analysisSig = signature;
            _pdf.ClearRenderCache();
            ClearProcessed();
            _analyses.Clear();
            _outcomes.Clear();
            _savedPages.Clear();
            _savedSignature.Clear();
            _savedFile.Clear();
            _lotUnmatchedPages.Clear();
            if (_pdf.IsOpen)
            {
                _worker.NewGeneration();
                ShowPage(_currentPage);
                SubmitPrefetchJobs();
            }
            UpdateDashboard();
            UpdatePageSlots();
        }
        else ReinspectCurrent();
        DrawZones();
    }

    /// <summary>교정 사전이 바뀐 뒤 재검사 (설정 창/교정 등록에서 호출).</summary>
    public void ReloadEngineAndReinspect()
    {
        _engine = BuildEngine();
        ReinspectCurrent();
        DrawZones();
    }

    public void Shutdown()
    {
        _worker.Dispose();
        ClearProcessed();
        _pdf.Dispose();
    }

    /// <summary>설정된 OCR 엔진 인스턴스.</summary>
    private IOcrEngine CurrentOcrEngine() =>
        _config.Section("ocr")["engine"]?.GetValue<string>() switch
        {
            "pattern" => _glyphEngine,
            "onnx" => OnnxEngine.Value,
            _ => _textract,
        };

    private async Task CheckAwsAsync()
    {
        var engine = CurrentOcrEngine();
        if (engine.Id != "aws")
        {
            // 로컬 엔진 — AWS 인증 불필요
            AwsStatusChanged?.Invoke(true, $"OCR: {engine.DisplayName}");
            return;
        }
        var status = await _textract.ValidateCredentialsAsync();
        AwsStatusChanged?.Invoke(status.Ok,
            status.Ok ? "AWS 인증 확인됨" : $"AWS 인증 실패: {status.Error}");
        if (!status.Ok)
            Status("AWS 인증 실패 — OCR 실행 전에 설정에서 자격증명을 확인하세요.", StatusLevel.Error);
    }

    private void PopulateStandardButtons()
    {
        _suppressEvents = true;
        StandardPanel.Children.Clear();
        foreach (var spec in _standards.Standards.Values)
        {
            var button = new System.Windows.Controls.RadioButton
            {
                Content = spec.DisplayName,
                Tag = spec.Name,
                GroupName = "standard",
                Style = (Style)FindResource("StandardToggle"),
                ToolTip = spec.Name == spec.DisplayName ? null : $"내부 코드: {spec.Name}",
            };
            var key = spec.Name;
            button.Checked += (_, _) =>
            {
                if (_suppressEvents) return;
                _selectedStandard = key;
                // 수동 선택은 이 페이지에서 자동 매칭(레코드/양식/커스텀)보다 우선
                if (_pdf.IsOpen) _manualStandardPages[_currentPage] = key;
                ReinspectCurrent();
            };
            StandardPanel.Children.Add(button);
        }
        if (_selectedStandard is null || !_standards.Standards.ContainsKey(_selectedStandard))
            _selectedStandard = _standards.Standards.Keys.FirstOrDefault();
        CheckStandardButton(_selectedStandard);
        _suppressEvents = false;
    }

    private void CheckStandardButton(string? key)
    {
        foreach (var child in StandardPanel.Children
                     .OfType<System.Windows.Controls.RadioButton>())
            child.IsChecked = (string?)child.Tag == key;
    }

    /// <summary>규격 자동 선택 (이벤트 억제 상태로 버튼 체크만 갱신).</summary>
    private void SelectStandard(string key)
    {
        if (!_standards.Standards.ContainsKey(key)) return;
        _selectedStandard = key;
        var previous = _suppressEvents;
        _suppressEvents = true;
        CheckStandardButton(key);
        _suppressEvents = previous;
    }

    // ---------------- 목록 ----------------

    public void LoadRecords(List<LabelRecord> records)
    {
        _records = records;
        _manualLotPages.Clear();
        _pageLotChoice.Clear();
        _savedSignature.Clear();   // 목록이 바뀌면 같은 페이지라도 다른 판정 — 자동 저장 스킵 기준 초기화
        _suppressEvents = true;
        var items = new List<string> { "LOT 선택…" };
        items.AddRange(records.Select(r => r.Lot));
        LotCombo.ItemsSource = items;
        LotCombo.SelectedIndex = 0;
        _suppressEvents = false;
        ListStatus.Text = $"{records.Count}건 로드됨";
        ListStatus.Foreground = (Brush)FindResource("SuccessBrush");
        // 기준정보(마스터 DB) 자동 축적 — 사전 등록값은 보존된다
        if (_history is not null)
            foreach (var record in records) _history.UpsertMasterFromRecord(record);
        ReinspectCurrent();
    }

    private void OnOpenList(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        { Title = "검사 목록 열기", Filter = "Excel 파일|*.xlsx" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var (records, warnings) = Schema.LoadInspectionList(dialog.FileName);
            _config.Settings["last_list_path"] = dialog.FileName;
            _config.SaveSettings();
            LoadRecords(records);
            if (warnings.Count > 0)
                Dialogs.Warn(this, string.Join("\n", warnings.Take(20)), "목록 경고");
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, ex.Message, "목록 오류");
        }
    }

    private LabelRecord? CurrentRecord()
    {
        var index = LotCombo.SelectedIndex - 1;
        return index >= 0 && index < _records.Count ? _records[index] : null;
    }

    private void OnLotChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        // 사용자가 직접 고른 페이지 — 선택을 기억해 두고 자동 매칭이 덮어쓰지 않는다
        _manualLotPages.Add(_currentPage);
        _pageLotChoice[_currentPage] = LotCombo.SelectedIndex;
        LotMatchLabel.Text = "수동";
        var record = CurrentRecord();
        if (record?.Standard is { } standard) SelectStandard(standard);
        ReinspectCurrent();
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ReinspectCurrent();
    }

    // ---------------- PDF ----------------

    private void OnOpenPdf(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "라벨 PDF 열기", Filter = "PDF 파일|*.pdf" };
        if (dialog.ShowDialog() == true) LoadPdf(dialog.FileName);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            foreach (var path in paths)
                if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    LoadPdf(path);
                    return;
                }
    }

    private void LoadPdf(string path)
    {
        try { _pdf.Open(path); }
        catch (Exception ex)
        {
            Dialogs.Error(this, ex.Message, "PDF 오류");
            return;
        }
        _currentPage = 0;
        _analyses.Clear();
        _outcomes.Clear();
        _savedPages.Clear();
        _savedSignature.Clear();
        _savedFile.Clear();
        _lotUnmatchedPages.Clear();
        _manualLotPages.Clear();
        _pageLotChoice.Clear();
        _manualStandardPages.Clear();
        _typeLearnedPages.Clear();
        _typeAlarmPages.Clear();
        ClearProcessed();
        _worker.NewGeneration();
        foreach (var button in new[] { FirstButton, PrevButton, NextButton, LastButton,
                                       NextAttentionButton })
            button.IsEnabled = true;
        PdfNameLabel.Text = $"{Path.GetFileName(path)} · {_pdf.PageCount}페이지";
        Status($"PDF 로드: {Path.GetFileName(path)} ({_pdf.PageCount}페이지)");
        UpdateDashboard();
        UpdatePageSlots();
        ShowPage(0, fit: true);
        SubmitPrefetchJobs();
    }

    private string CacheKeyForPage(int page) =>
        OcrCache.PageKey(_pdf.Path ?? "", _pdf.Mtime, page, _pdf.RenderZoom,
            // 엔진·보정 옵션이 다르면 다른 캐시 (엔진 전환 시 재분석)
            $"{PreprocessSig}|eng={CurrentOcrEngine().Id}" +
            $"|cs={_config.SectionBool("ocr", "contrast_stretch", false)}");

    private void SubmitPrefetchJobs()
    {
        var policyNode = _config.Settings["prefetch_policy"];
        IEnumerable<int> pages;
        if (policyNode?.GetValueKind() == System.Text.Json.JsonValueKind.Number)
        {
            var ahead = Math.Max(0, policyNode.GetValue<int>());
            pages = Enumerable.Range(_currentPage,
                Math.Min(_pdf.PageCount - _currentPage, ahead + 1));
        }
        else
        {
            pages = Enumerable.Range(0, _pdf.PageCount);   // 기본 'all'
        }
        foreach (var page in pages)
        {
            var key = CacheKeyForPage(page);
            var cached = _cache.Get(key);
            if (cached is not null) { _analyses[page] = cached; continue; }
            var pageCopy = page;
            _worker.Submit(page, key, () => RenderProcessedForAnalysis(pageCopy),
                           priority: page == _currentPage ? 0 : 1);
        }
        UpdatePrefetchLabel();
    }

    private void Navigate(int target)
    {
        if (!_pdf.IsOpen) return;
        target = Math.Clamp(target, 0, _pdf.PageCount - 1);
        if (target == _currentPage) return;
        _currentPage = target;
        ShowPage(target);
        SubmitPrefetchJobs();   // 이동할 때마다 앞 N페이지 버퍼 유지 (캐시는 재과금 없음)
    }

    private void OnFirstPage(object s, RoutedEventArgs e) => Navigate(0);
    private void OnPrevPage(object s, RoutedEventArgs e) => Navigate(_currentPage - 1);
    private void OnNextPage(object s, RoutedEventArgs e) => Navigate(_currentPage + 1);
    private void OnLastPage(object s, RoutedEventArgs e) => Navigate(_pdf.PageCount - 1);

    private void OnNextAttention(object sender, RoutedEventArgs e) => GoToNextAttention();

    /// <summary>현재 다음 페이지부터 순환하며 미검사(OCR 대기·LOT 미매칭 포함) 또는 확인 필요 페이지로 이동 (N).</summary>
    private void GoToNextAttention()
    {
        if (!_pdf.IsOpen) return;
        var target = SessionStats.NextAttention(_pdf.PageCount, _currentPage, PageState);
        if (target is null)
        {
            Status("다음 확인 대상 없음 — 모든 페이지 검사·합격");
            return;
        }
        if (target == _currentPage)
        {
            Status($"p{_currentPage + 1}만 확인 대상입니다 — 다른 페이지는 모두 검사·합격");
            return;
        }
        Navigate(target.Value);
    }

    /// <summary>페이지 상태: null=미검사(OCR 대기·LOT 미매칭), false=확인 필요, true=합격.</summary>
    private bool? PageState(int page) =>
        _lotUnmatchedPages.Contains(page) ? null
        : _outcomes.TryGetValue(page, out var outcome) ? outcome.Passed : null;

    private void OnKeyDown(object sender, KeyEventArgs e) => HandleGlobalKey(e);

    /// <summary>전역 키 입력 (MainWindow가 검사 탭 활성 시 라우팅) —
    /// Ctrl+O 열기 · Esc 등록 모드 해제 · Ctrl+S 저장 · F5 재검사 · N 다음 확인 대상 ·
    /// Ctrl+L LOT · Ctrl+F 검색 · Ctrl+Shift+L OCR 로그 · ←/→ 페이지 · WASD 이동 · Q/E 줌.</summary>
    public void HandleGlobalKey(KeyEventArgs e)
    {
        // 텍스트 입력 중에는 개입하지 않는다 (검색창에서 N/F5는 문자 입력)
        if (e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase
            or PasswordBox or ComboBox or ComboBoxItem) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (ctrl && !shift && e.Key == Key.O)
        {
            OnOpenPdf(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && ZoneModeButton.IsChecked == true)
        {
            ZoneModeButton.IsChecked = false;
            e.Handled = true;
            return;
        }
        if (!_pdf.IsOpen) return;
        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.S when !shift:
                    OnSaveCurrent(this, new RoutedEventArgs()); e.Handled = true; break;
                case Key.L when shift:
                    OpenOcrLog("", ""); e.Handled = true; break;
                case Key.L:
                    LotCombo.Focus();
                    LotCombo.IsDropDownOpen = true;
                    e.Handled = true; break;
                case Key.F when !shift:
                    SearchBox.Focus();
                    SearchBox.SelectAll();
                    e.Handled = true; break;
            }
            return;   // Ctrl 조합은 아래 단일 키(S=이동 등)와 겹치지 않게 여기서 끝
        }
        const double PanStep = 90;
        switch (e.Key)
        {
            case Key.F5:
                ReinspectCurrent();
                Status($"p{_currentPage + 1} 재검사");
                e.Handled = true; break;
            case Key.N: GoToNextAttention(); e.Handled = true; break;
            case Key.Left: Navigate(_currentPage - 1); e.Handled = true; break;
            case Key.Right: Navigate(_currentPage + 1); e.Handled = true; break;
            case Key.Home: Navigate(0); e.Handled = true; break;
            case Key.End: Navigate(_pdf.PageCount - 1); e.Handled = true; break;
            case Key.W:
                ViewerScroll.ScrollToVerticalOffset(
                    ViewerScroll.VerticalOffset - PanStep);
                e.Handled = true; break;
            case Key.S:
                ViewerScroll.ScrollToVerticalOffset(
                    ViewerScroll.VerticalOffset + PanStep);
                e.Handled = true; break;
            case Key.A:
                ViewerScroll.ScrollToHorizontalOffset(
                    ViewerScroll.HorizontalOffset - PanStep);
                e.Handled = true; break;
            case Key.D:
                ViewerScroll.ScrollToHorizontalOffset(
                    ViewerScroll.HorizontalOffset + PanStep);
                e.Handled = true; break;
            case Key.Q: ZoomBy(ZoomStep); e.Handled = true; break;
            case Key.E: ZoomBy(1 / ZoomStep); e.Handled = true; break;
        }
    }

    private void ShowPage(int page, bool fit = false)
    {
        PageLabel.Text = $"{page + 1} / {_pdf.PageCount}";
        var image = RenderProcessed(page);
        UpdateAlignLabel(page);

        if (!_analyses.TryGetValue(page, out var analysis))
        {
            analysis = _cache.Get(CacheKeyForPage(page));
            if (analysis is not null) _analyses[page] = analysis;
        }

        if (analysis is not null)
        {
            RunInspection(page, analysis, image, fit);   // 캐시 히트 → 무지연 표시
        }
        else
        {
            SetViewerImage(image, fit);
            ClearResultPanel("OCR 진행 중…");
            var pageCopy = page;
            _worker.Submit(page, CacheKeyForPage(page),
                           () => RenderProcessedForAnalysis(pageCopy), priority: 0);
            _worker.Prioritize(page);
        }
        UpdatePrefetchLabel();
        UpdatePageSlots();
        DrawZones();
    }

    private void OnPageDone(int generation, int page, string key, PageAnalysis analysis)
    {
        _cache.Put(key, analysis);   // 과금된 결과는 세대와 무관하게 저장
        if (generation != _worker.Generation) return;
        _analyses[page] = analysis;
        UpdatePrefetchLabel();
        if (page == _currentPage && _pdf.IsOpen)
            RunInspection(page, analysis, RenderProcessed(page));
    }

    private string? _lastFailureMessage;
    private DateTime _lastFailureShownAt;

    private void OnPageFailed(int generation, int page, string message)
    {
        if (generation != _worker.Generation) return;
        Status($"{page + 1}페이지 OCR 실패: {message}", StatusLevel.Error);
        if (page == _currentPage)
            ClearResultPanel($"OCR 실패 — {ShortMessage(message)}", StatusLevel.Warn);
        // 같은 원인(예: 자격증명 오류)으로 페이지마다 팝업이 연쇄되는 것을 막는다:
        // 동일 메시지는 60초에 한 번만 알리고, 남은 프리페치는 중단한다.
        var sameAsLast = message == _lastFailureMessage
                         && DateTime.Now - _lastFailureShownAt < TimeSpan.FromSeconds(60);
        if (sameAsLast) return;
        _lastFailureMessage = message;
        _lastFailureShownAt = DateTime.Now;
        _worker.NewGeneration();   // 대기 중인 프리페치 잡 폐기 (재과금·연쇄 실패 방지)
        Dialogs.Warn(this,
            $"{message}\n\n남은 페이지의 자동 OCR을 중단했습니다. 설정(OCR 엔진·AWS " +
            "자격증명)을 확인한 뒤 페이지를 이동하면 다시 시도합니다.",
            "OCR 실패");
    }

    private static string ShortMessage(string message)
    {
        var first = message.Split('\n')[0].Trim();
        return first.Length > 60 ? first[..60] + "…" : first;
    }

    private void UpdatePrefetchLabel()
    {
        if (!_pdf.IsOpen) { PrefetchLabel.Text = ""; ProgressChanged?.Invoke(0, 0); return; }
        var done = _analyses.Count;
        var total = _pdf.PageCount;
        ProgressChanged?.Invoke(done, total);
        PrefetchLabel.Text = done >= total
            ? $"OCR 완료 {done}/{total} ✓" : $"OCR 진행 {done}/{total}…";
        PrefetchLabel.Foreground = done >= total
            ? (Brush)FindResource("SuccessBrush") : (Brush)FindResource("MutedBrush");
    }

    // ---------------- 검사 ----------------

    private sealed record FieldRowVm(string Field, string Term, string Count, string State)
    {
        /// <summary>색 없이도 읽히는 상태 기호 — ✓ 일치 / ✗ 불일치 / – 참고(기대 없음).</summary>
        public string Glyph => State switch { "pass" => "✓", "fail" => "✗", _ => "–" };
    }
    private sealed record BarcodeRowVm(string Order, string Symbology, string Grade,
                                       string Value, string State, string? Tip = null);
    private sealed record PageSlotVm(int PageIndex, string Number, string Tip,
                                     Brush Fill, Brush Stroke, Thickness StrokeThickness,
                                     Brush TextBrush, bool Saved, FontWeight Weight);

    private void RunInspection(int page, PageAnalysis analysis, SKBitmap image,
                               bool fit = false)
    {
        // 이 페이지에서 사용자가 직접 고른 규격 — 모든 자동 선택보다 우선
        var manualStandard = _manualStandardPages.TryGetValue(page, out var chosenStd)
            && _standards.Standards.ContainsKey(chosenStd) ? chosenStd : null;

        var lotUnmatched = false;
        // 페이지별 LOT 매칭 — 수동 선택 페이지는 그때의 선택을 복원하고,
        // 그 외 페이지는 매번 라벨의 LOT을 다시 읽어 자동 선택한다.
        if (_manualLotPages.Contains(page))
        {
            if (_pageLotChoice.TryGetValue(page, out var stored)
                && LotCombo.SelectedIndex != stored)
            {
                _suppressEvents = true;
                LotCombo.SelectedIndex = stored;
                _suppressEvents = false;
            }
            LotMatchLabel.Text = "수동";
            LotMatchLabel.Foreground = (Brush)FindResource("SuccessBrush");
            LotMatchLabel.ToolTip = "이 페이지는 사용자가 직접 고른 LOT으로 검사합니다";
        }
        else if (_records.Count > 0)
        {
            var match = _engine.MatchLot(analysis.Words, _records);
            lotUnmatched = match is null;
            if (match is not null)
            {
                var index = _records.FindIndex(r => r.Lot == match.Lot);
                if (index >= 0)
                {
                    if (LotCombo.SelectedIndex != index + 1)
                    {
                        _suppressEvents = true;
                        LotCombo.SelectedIndex = index + 1;
                        _suppressEvents = false;
                    }
                    if (manualStandard is null && _records[index].Standard is { } std)
                        SelectStandard(std);
                }
                LotMatchLabel.Text = match.MatchType switch
                {
                    "exact" => "자동(정확)", "suffix_unique" => "자동(끝4자리)",
                    _ => "자동(유사)",
                };
                LotMatchLabel.ToolTip = $"라벨에서 읽은 '{match.Candidate}' ↔ 목록 LOT {match.Lot} ({match.MatchType}, 신뢰도 {match.Confidence}%)";
                LotMatchLabel.Foreground = (Brush)FindResource("SuccessBrush");
            }
            else
            {
                // 라벨에서 LOT 후보를 못 찾음 — 이전 선택을 그대로 검사하면 다른 LOT 라벨이
                // '합격'으로 보일 수 있다(거짓 합격). 명시 경고 + 아래에서 '확인 필요' 강제.
                LotMatchLabel.Text = "⚠ 미매칭";
                LotMatchLabel.Foreground = (Brush)FindResource("WarnBrush");
                LotMatchLabel.ToolTip = "이 페이지에서 목록의 LOT을 읽지 못해 이전 선택 LOT으로 검사합니다. LOT 위치를 확인하거나 수동으로 LOT을 고르세요.";
                Status($"p{page + 1}: 라벨에서 LOT을 찾지 못했습니다 — 이전 선택({CurrentRecord()?.Lot})으로 검사하며 판정은 '확인 필요'로 표시됩니다.",
                       StatusLevel.Warn);
            }
        }
        if (lotUnmatched) _lotUnmatchedPages.Add(page); else _lotUnmatchedPages.Remove(page);

        // 커스텀 필드 값 → 규격 자동 매칭 (예: 라벨에서 Rev.A00 검출 → 규격 A00)
        if (manualStandard is null)
            foreach (var def in _engine.Options.CustomFields)
            {
                if (def.Standard is not { Length: > 0 } mapped
                    || !_standards.Standards.ContainsKey(mapped)) continue;
                if (_engine.CountCustomField(def, analysis.Words).Count == 0) continue;
                SelectStandard(mapped);
                if (FormRow.Visibility == Visibility.Visible)
                {
                    FormMatchLabel.Text =
                        $"{def.Name} → {_standards.Spec(mapped).DisplayName}";
                    FormMatchLabel.Foreground = (Brush)FindResource("SuccessBrush");
                }
                break;
            }

        // 라벨 양식 자동 감지 (설정된 위치의 텍스트/이미지 패턴) → 규격 자동 선택
        FormDetection? detectedForm = null;
        if (_formRules.Count > 0)
        {
            var form = _formDetector.Detect(_formRules, analysis.Words, image);
            detectedForm = form;
            if (form is not null)
            {
                FormMatchLabel.Text =
                    $"{form.Name} ({form.Method} {form.Score * 100:F0}%)";
                FormMatchLabel.Foreground = (Brush)FindResource("SuccessBrush");
                if (manualStandard is null && form.Standard.Length > 0
                    && _standards.Standards.ContainsKey(form.Standard))
                    SelectStandard(form.Standard);
            }
            else
            {
                FormMatchLabel.Text = "미감지";
                FormMatchLabel.Foreground = (Brush)FindResource("MutedBrush");
            }
        }

        if (manualStandard is not null) SelectStandard(manualStandard);

        var record = CurrentRecord();
        if (record is null)
        {
            SetViewerImage(image, fit);
            if (_records.Count > 0) ClearResultPanel("LOT을 선택하면 검사를 시작합니다");
            else ClearResultPanel("검사 목록을 먼저 로드하세요", StatusLevel.Warn);
            return;
        }

        if (_standards.Standards.Count == 0)
        {
            SetViewerImage(image, fit);
            ClearResultPanel("규격 정의가 없습니다 — 설정 폴더의 standards.json을 확인하세요",
                             StatusLevel.Warn);
            return;
        }
        var standardName = _selectedStandard ?? _standards.Standards.Keys.First();
        var barcodeChecks = BarcodeDetector.CrossCheckHits(analysis.Barcodes, record);
        if (lotUnmatched)
            barcodeChecks.Add(new CrossCheckResult("LOT 매칭", "LOT", "(라벨에서 LOT 미검출)",
                                                   record.Lot, Matched: false));
        // 사전 등록 기준정보(마스터 DB) 대조
        if (_history?.GetMaster(record.Pn) is { } master)
            barcodeChecks.AddRange(MasterCheck.Check(master, record, analysis.Barcodes));
        // 동일값 패턴 검사 — 위치 이동을 보정해 객체별 값 상호 비교
        var formatKey = $"{record.Pn}|{standardName}";
        if (_sameValueRules.Count > 0)
        {
            var sameValueResults = _sameValue.Check(formatKey, _sameValueRules,
                _corrections.Apply(analysis.Words), (image.Width, image.Height));
            barcodeChecks.AddRange(SameValueChecker.ToCrossChecks(sameValueResults));
        }
        var outcome = _engine.Inspect(record, standardName, analysis.Words,
                                      barcodeChecks, SearchBox.Text,
                                      (image.Width, image.Height),
                                      analysis.Barcodes);
        _outcomes[page] = outcome;
        CheckLabelType(page, formatKey, analysis, record, outcome);
        ShowOutcome(outcome, analysis);
        using var annotated = Annotate.RenderOverlays(
            image, outcome.AllMatches, _standards.FieldColors, CurrentOverlayStyle());
        // 검출된 바코드(DataMatrix·GS1-128 등)에도 박스 표시
        Annotate.DrawBarcodeBoxes(annotated, analysis.Barcodes, CurrentOverlayStyle());
        // 규격 자동 판별에 쓰인 양식명 OCR 영역도 박스 표시 (보라색)
        if (detectedForm is { TextBbox: { } formBbox } detected)
            Annotate.DrawTaggedBox(annotated, formBbox, $"양식: {detected.Name}",
                                   Annotate.FormBoxColor, CurrentOverlayStyle());
        // 저신뢰 OCR 알람 — 유의미하게 낮으면 해당 단어를 주황 파선으로 하이라이트
        if (_config.SectionBool("ocr", "quality_alarm", true))
        {
            var lowThreshold = _config.SectionInt("ocr", "low_word_confidence", 70);
            var quality = OcrQuality.Assess(analysis.Words,
                _config.SectionInt("ocr", "low_avg_confidence", 80), lowThreshold);
            if (quality.IsPoor)
            {
                Annotate.HighlightLowConfidence(annotated,
                    OcrQuality.LowConfidenceWords(analysis.Words, lowThreshold),
                    CurrentOverlayStyle());
                BadgeReasonText.Text += $" · ⚠ OCR 신뢰도 {quality.Average:F0}%";
                Status($"⚠ p{page + 1}: {quality.Summary}", StatusLevel.Warn);
            }
        }
        SetViewerImage(annotated, fit);
        UpdateDashboard();
        UpdatePageSlots();
        // 자동 저장: 같은 판정(서명 동일)이 이미 저장돼 있으면 건너뛴다 — 재방문·재검사마다
        // 새 번호 파일과 이력 행이 쌓이지 않게 (페이지당 판정 1건 원칙)
        if (AutoSaveCheck.IsChecked == true
            && (!_savedSignature.TryGetValue(page, out var savedSig)
                || savedSig != outcome.Signature()))
            SaveOutcome(page, outcome, image, notify: false);
    }

    /// <summary>라벨 유형 학습·이상 알람 — 전체 OCR 토큰을 유형별로 축적하다가
    /// 기존과 다른 유형(고정 문구 누락·처음 보는 문구 다수)이 나오면 알린다.</summary>
    private void CheckLabelType(int page, string formatKey, PageAnalysis analysis,
                                LabelRecord record, InspectionOutcome outcome)
    {
        if (!_config.SectionBool("type_learning", "enabled", true)) return;
        var report = _profiler.Check(formatKey, analysis.Words, record);
        if (report.IsAnomaly)
        {
            Status($"⚠ 유형 이상 (p{page + 1}): {report.Summary}", StatusLevel.Warn);
            if (!_typeAlarmPages.Add(page)) return;   // 같은 페이지 중복 알람 방지
            var detail =
                (report.MissingTokens.Count > 0
                    ? $"\n누락된 고정 문구: {string.Join(", ", report.MissingTokens.Take(6))}"
                      + (report.MissingTokens.Count > 6 ? " …" : "")
                    : "") +
                (report.NewTokens.Count > 0
                    ? $"\n처음 보는 문구: {string.Join(", ", report.NewTokens.Take(6))}"
                      + (report.NewTokens.Count > 6 ? " …" : "")
                    : "");
            // 버튼 라벨이 동작을 말하도록 (Enter = 학습 안 함 — 오조작으로 이상 라벨이 학습되지 않게)
            var choice = ChoiceDialog.Show(Window.GetWindow(this), "라벨 유형 이상 감지",
                $"이 라벨이 지금까지 학습된 유형과 다릅니다.\n\n{report.Summary}{detail}\n\n" +
                "인쇄 오류가 의심되면 라벨을 확인하세요. 라벨 개정 등 정상적인 변경이면 " +
                "새 유형으로 학습할 수 있습니다.",
                ("라벨을 확인하겠습니다 (학습 안 함)", ChoiceStyle.Default, true),
                ("정상 개정 — 새 유형으로 학습", ChoiceStyle.Caution, false));
            if (choice == 1 && _typeLearnedPages.Add(page))
                _profiler.Learn(formatKey, analysis.Words, record);
        }
        else if (outcome.Passed && _typeLearnedPages.Add(page))
        {
            _profiler.Learn(formatKey, analysis.Words, record);
            if (report.SampleCount < _profiler.MinSamples)
                Status(
                    $"라벨 유형 학습 중 ({report.SampleCount + 1}/{_profiler.MinSamples})");
        }
    }

    /// <summary>판정 배지를 앰버로 번쩍인 뒤 목표색으로 페이드 (650ms) — "방금 판정이 바뀌었다"는
    /// 피드백. UDInspect의 행 하이라이트 페이드와 같은 규범.</summary>
    private void FlashBadge(SolidColorBrush target)
    {
        var amber = (Color)FindResource("FlashAmberColor");
        var brush = new SolidColorBrush(Color.FromArgb(200, amber.R, amber.G, amber.B));
        StatusBadge.Background = brush;
        brush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(target.Color, TimeSpan.FromMilliseconds(650))
            { FillBehavior = FillBehavior.HoldEnd });
    }

    private void ShowOutcome(InspectionOutcome outcome, PageAnalysis? analysis = null)
    {
        _lastAnalysisShown = analysis;
        if (outcome.Passed)
        {
            StatusBadgeText.Text = $"✓ 합격 (PASSED) · 규격 {outcome.Standard.DisplayName}";
            StatusBadgeText.Foreground = (Brush)FindResource("SuccessBrush");
            StatusBadge.BorderBrush = (Brush)FindResource("SuccessBrush");
            FlashBadge((SolidColorBrush)FindResource("SuccessBgBrush"));
        }
        else
        {
            StatusBadgeText.Text = $"⚠ 확인 필요 (CHECK) · 규격 {outcome.Standard.DisplayName}";
            StatusBadgeText.Foreground = (Brush)FindResource("WarnBrush");
            StatusBadge.BorderBrush = (Brush)FindResource("WarnBrush");
            FlashBadge((SolidColorBrush)FindResource("WarnBgBrush"));
        }
        // 사유줄: 저장 이미지 요약 박스 2행과 같은 문구 (Core InspectionSummary)
        BadgeReasonText.Text = InspectionSummary.Describe(outcome);
        BadgeReasonText.Foreground = (Brush)FindResource(outcome.Passed ? "SuccessBrush" : "WarnBrush");
        FieldGrid.ItemsSource = outcome.Fields.Values
            .Select(f => new FieldRowVm(
                f.Field, f.Term.Length > 0 ? f.Term : "-",
                f.Expected is { } expected ? $"{f.Found}/{expected}" : f.Found.ToString(),
                f.Expected is null ? "info" : f.Passed ? "pass" : "fail")).ToList();
        // 라벨에 등장하는 모든 바코드를 위→아래 순으로 나열 (종류/등급/값/일치)
        var barcodeRows = new List<BarcodeRowVm>();
        var hits = (_lastAnalysisShown?.Barcodes ?? [])
            .OrderBy(b => b.Bbox.Y).ThenBy(b => b.Bbox.X).ToList();
        for (var i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            string value;
            string state;
            string? tip = null;
            if (hit.IsGs1 || BarcodeDetector.LooksGs1(hit.Text))
            {
                try
                {
                    var message = Gs1.Parse(hit.Text);
                    value = string.Concat(message.Elements
                        .Select(el => $"({el.Ai}){el.Value}"));
                    var checks = BarcodeCrossCheck.Check(message, outcome.Record,
                                                         hit.Symbology);
                    if (checks.Count == 0) state = "-";
                    else if (checks.All(c => c.Matched)) state = "일치";
                    else
                    {
                        state = "불일치";
                        value += "  ⚠ " + string.Join(", ", checks
                            .Where(c => !c.Matched)
                            .Select(c => $"{c.Field} 기대 {c.ExpectedValue}"));
                    }
                    if (message.Partial)
                    {
                        value += $" (미등록 AI: {string.Join(", ", message.UnknownAis)})";
                        tip = "GS1 표준 표에 없는 AI가 있어 그 구간은 대조하지 않았습니다 — 등록된 AI(01/10/17 등)만 대조";
                    }
                }
                catch (Gs1ParseException)
                {
                    // 바코드는 읽혔지만 GS1 구조 해석 실패 — OCR 텍스트로 대체하지 않고(GTIN은 바코드가 진실)
                    // 붉은 행으로 육안 확인을 요구한다
                    var raw = hit.Text.Replace('\x1d', '|');
                    value = raw.Length > 40 ? raw[..40] + "…" : raw;
                    state = "해석 불가";
                    tip = "바코드는 읽혔으나 GS1 구조를 해석하지 못했습니다 — 판독값을 육안 확인하세요";
                }
            }
            else
            {
                value = hit.Text;
                state = "-";
            }
            barcodeRows.Add(new BarcodeRowVm($"#{i + 1}", hit.Symbology,
                                             hit.Grade ?? "", value, state, tip));
        }
        // 부가 검증 행 (동일값 패턴 · 기준정보 DB)
        foreach (var check in outcome.BarcodeChecks
                     .Where(c => c.Source is "동일값" or "기준DB"))
            barcodeRows.Add(new BarcodeRowVm("", $"{check.Source}·{check.Field}", "",
                check.Matched ? check.BarcodeValue
                              : $"{check.BarcodeValue} (기대: {check.ExpectedValue})",
                check.Matched ? "일치" : "불일치"));
        if (barcodeRows.Count == 0)
            barcodeRows.Add(new BarcodeRowVm("", "", "", "검출된 바코드 없음", ""));
        BarcodeGrid.ItemsSource = barcodeRows;
    }

    private PageAnalysis? _lastAnalysisShown;

    /// <summary>판정 없음 상태의 배지 — level=Warn(OCR 실패·목록 없음)이면 주황 테두리로 주의를 끈다.</summary>
    private void ClearResultPanel(string message, StatusLevel level = StatusLevel.Info)
    {
        StatusBadgeText.Text = message;
        StatusBadgeText.Foreground = (Brush)FindResource(level == StatusLevel.Info ? "MutedBrush" : "WarnBrush");
        StatusBadge.Background = (Brush)FindResource("ReadoutBrush");
        StatusBadge.BorderBrush = (Brush)FindResource(level == StatusLevel.Info ? "ReadoutBorderBrush" : "WarnBrush");
        BadgeReasonText.Text = "";
        FieldGrid.ItemsSource = null;
        BarcodeGrid.ItemsSource = null;
    }

    private void ReinspectCurrent()
    {
        if (_pdf.IsOpen && _analyses.TryGetValue(_currentPage, out var analysis))
            RunInspection(_currentPage, analysis, RenderProcessed(_currentPage));
    }

    // ---------------- 렌더 + 라벨 정렬 ----------------

    /// <summary>페이지 렌더 후 라벨 정렬(하늘색 이형지 기준 크롭·기울기 보정) 적용.
    /// 반환 비트맵은 내부 캐시 소유 — 호출자는 해제하지 않는다.</summary>
    private SKBitmap RenderProcessed(int page)
    {
        if (!_config.SectionBool("preprocess", "crop_label", true))
            return _pdf.RenderPage(page);
        lock (_procLock)
            if (_processed.TryGetValue(page, out var hit))
            {
                _procOrder.Remove(page);
                _procOrder.AddLast(page);
                return hit.Image;
            }
        var raw = _pdf.RenderPage(page);
        var aligned = ImagePreprocess.DeskewAndCropLiner(raw);
        lock (_procLock)
        {
            if (_processed.TryGetValue(page, out var raced))
            {
                if (!ReferenceEquals(aligned.Image, raw)) aligned.Image.Dispose();
                return raced.Image;
            }
            _processed[page] = (aligned.Image, aligned.AngleDegrees, aligned.Cropped,
                                !ReferenceEquals(aligned.Image, raw));
            _procOrder.AddLast(page);
            while (_processed.Count > Math.Max(6, _pdf.CachePages)
                   && _procOrder.First is { } oldest)
            {
                _procOrder.RemoveFirst();
                var entry = _processed[oldest.Value];
                if (entry.Owned) entry.Image.Dispose();
                _processed.Remove(oldest.Value);
            }
            return aligned.Image;
        }
    }

    /// <summary>백그라운드 분석용 소유 렌더 — 공유 캐시를 쓰지 않는다.
    /// 캐시 비트맵은 LRU 축출로 분석 도중 해제될 수 있어(박스 좌표 오염·크래시)
    /// 워커에는 항상 소유본을 주고 워커가 분석 후 해제한다.</summary>
    private SKBitmap RenderProcessedForAnalysis(int page)
    {
        var raw = _pdf.RenderPageOwned(page);
        if (!_config.SectionBool("preprocess", "crop_label", true)) return raw;
        var aligned = ImagePreprocess.DeskewAndCropLiner(raw);
        if (!ReferenceEquals(aligned.Image, raw)) raw.Dispose();
        return aligned.Image;
    }

    private (double Angle, bool Cropped)? ProcessedInfo(int page)
    {
        lock (_procLock)
            return _processed.TryGetValue(page, out var entry)
                ? (entry.Angle, entry.Cropped) : null;
    }

    /// <summary>기울기 보정/크롭 상태 표시 (요청: 보정 완료된 페이지 표시).</summary>
    private void UpdateAlignLabel(int page)
    {
        var info = ProcessedInfo(page);
        AlignLabel.Text = info is { Cropped: true } aligned
            ? Math.Abs(aligned.Angle) >= 0.3
                ? $"정렬됨: 크롭 + {aligned.Angle:+0.0;-0.0}°"
                : "정렬됨: 크롭"
            : _config.SectionBool("preprocess", "crop_label", true)
                ? "정렬: 이형지 미감지" : "";
    }

    private void ClearProcessed()
    {
        lock (_procLock)
        {
            foreach (var entry in _processed.Values)
                if (entry.Owned) entry.Image.Dispose();
            _processed.Clear();
            _procOrder.Clear();
        }
    }

    // ---------------- 뷰어 (줌/팬) ----------------

    private void SetViewerImage(SKBitmap image, bool fit)
    {
        _displayed?.Dispose();
        _displayed = image.Copy();
        ViewerPlaceholder.Visibility = Visibility.Collapsed;
        if (fit)
        {
            var viewport = ViewerScroll.ViewportWidth > 0
                ? (ViewerScroll.ViewportWidth, ViewerScroll.ViewportHeight)
                : (ViewerScroll.ActualWidth, ViewerScroll.ActualHeight);
            if (viewport.Item1 > 0 && image.Width > 0)
                _zoom = Math.Clamp(Math.Min(viewport.Item1 / image.Width,
                                            viewport.Item2 / image.Height), 0.1, 1.0);
        }
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        if (_displayed is null) return;
        ViewerImage.Source = SkiaWpf.ToBitmapSource(_displayed);
        ViewerImage.Width = _displayed.Width * _zoom;
        ViewerImage.Height = _displayed.Height * _zoom;
        ViewerImage.Stretch = System.Windows.Media.Stretch.Fill;
        _suppressEvents = true;
        ZoomSlider.Value = _zoom * 100;
        _suppressEvents = false;
        ZoomLabel.Text = $"{(int)(_zoom * 100)}%";
        DrawZones();
    }

    // ---------------- 등록 영역 표시 (점선 + 칩) ----------------

    /// <summary>등록된 필드 영역(현재 규격 + 전체 규격)과 양식 규칙 영역을 뷰어 위에 점선·칩으로 그린다.
    /// 좌표계는 RubberBand와 같은 ViewerImage 표시 좌표(영역% × 표시 크기 × 배율).</summary>
    private void DrawZones()
    {
        ZoneLayer.Children.Clear();
        if (_displayed is null || _engine is null || ShowZonesCheck.IsChecked != true) return;
        var width = _displayed.Width * _zoom;
        var height = _displayed.Height * _zoom;
        var zoneStroke = (Brush)FindResource("PrimaryBrush");
        var zoneChipBg = (Brush)FindResource("PrimarySoftBrush");
        var zoneChipFg = (Brush)FindResource("AccentBrush");
        var formStroke = (Brush)FindResource("OverlayFormBrush");
        var chipFg = (Brush)FindResource("PanelBrush");
        foreach (var zone in _engine.Options.Zones)
        {
            if (zone.Standard.Length > 0 && zone.Standard != _selectedStandard) continue;
            var label = zone.Standard.Length > 0 ? $"{zone.Field} · {zone.Standard}" : zone.Field;
            AddZoneShape(zone.Region, width, height, zoneStroke, zoneChipBg, zoneChipFg, label,
                         ZoneTag(zone.Field, zone.Standard, zone.Region));
        }
        foreach (var rule in _formRules)
            AddZoneShape(rule.Region, width, height, formStroke, formStroke, chipFg,
                         $"양식: {rule.Name}", null);
    }

    private static string ZoneTag(string field, string standard,
                                  (double X, double Y, double W, double H) region) =>
        $"{field.ToUpperInvariant()}|{standard}|{region.X:F3}|{region.Y:F3}|{region.W:F3}|{region.H:F3}";

    private void AddZoneShape((double X, double Y, double W, double H) region,
                              double width, double height, Brush stroke, Brush chipBg,
                              Brush chipFg, string label, string? tag)
    {
        var x = region.X * width;
        var y = region.Y * height;
        var rect = new System.Windows.Shapes.Rectangle
        {
            Width = Math.Max(1, region.W * width), Height = Math.Max(1, region.H * height),
            Stroke = stroke, StrokeThickness = 1.5, StrokeDashArray = [6, 3],
            Fill = Brushes.Transparent, Tag = tag,
        };
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        ZoneLayer.Children.Add(rect);
        var chip = new Border
        {
            Background = chipBg, CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 0, 4, 1),
            Child = new TextBlock
            {
                Text = label, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = chipFg,
            },
        };
        Canvas.SetLeft(chip, x);
        Canvas.SetTop(chip, Math.Max(0, y - 16));
        ZoneLayer.Children.Add(chip);
    }

    /// <summary>방금 등록한 영역의 점선 사각형을 앰버로 번쩍인다 (DrawZones 뒤).</summary>
    private void FlashZone(string field, string standard,
                           (double X, double Y, double W, double H) region)
    {
        var tag = ZoneTag(field, standard, region);
        foreach (var child in ZoneLayer.Children.OfType<System.Windows.Shapes.Rectangle>())
            if (child.Tag is string t && t == tag) UiFx.FlashShape(child);
    }

    /// <summary>'영역 표시' 토글은 바꾸는 즉시 저장 (overlay.show_zones).</summary>
    private void OnShowZonesToggled(object sender, RoutedEventArgs e)
    {
        if (_loadingConfig || _config is null) return;
        try
        {
            _config.Section("overlay")["show_zones"] =
                System.Text.Json.Nodes.JsonValue.Create(ShowZonesCheck.IsChecked == true);
            _config.SaveSettings();
        }
        catch (Exception ex)
        {
            Status($"설정 저장 실패 (재실행 시 이전 설정으로 돌아갈 수 있음): {ShortMessage(ex.Message)}",
                   StatusLevel.Error);
        }
        DrawZones();
    }

    private void OnZoomSliderChanged(object sender,
                                     RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents || _displayed is null) return;
        _zoom = Math.Clamp(e.NewValue / 100.0, 0.1, 5.0);
        ApplyZoom();
    }

    /// <summary>줌 단계 (세밀한 조절 — 휠 1노치 = 8%).</summary>
    private const double ZoomStep = 1.08;

    private void ZoomTo(double newZoom, Point anchor)
    {
        if (_displayed is null) return;
        var oldZoom = _zoom;
        _zoom = Math.Clamp(newZoom, 0.05, 6.0);
        if (Math.Abs(_zoom - oldZoom) < 1e-6) return;
        var ratio = _zoom / oldZoom;
        ApplyZoom();
        ViewerScroll.ScrollToHorizontalOffset(
            (ViewerScroll.HorizontalOffset + anchor.X) * ratio - anchor.X);
        ViewerScroll.ScrollToVerticalOffset(
            (ViewerScroll.VerticalOffset + anchor.Y) * ratio - anchor.Y);
    }

    private void ZoomBy(double factor) => ZoomTo(_zoom * factor,
        new Point(ViewerScroll.ViewportWidth / 2, ViewerScroll.ViewportHeight / 2));

    private void OnViewerWheel(object sender, MouseWheelEventArgs e)
    {
        if (_displayed is null) return;
        ZoomTo(_zoom * (e.Delta > 0 ? ZoomStep : 1 / ZoomStep),
               e.GetPosition(ViewerScroll));
        e.Handled = true;
    }

    private bool _zoneDragging;
    private Point _zoneStart;   // ViewerImage 표시 좌표

    private void OnZoneModeChanged(object sender, RoutedEventArgs e)
    {
        var on = ZoneModeButton.IsChecked == true;
        ViewerScroll.Cursor = on ? Cursors.Cross : Cursors.Arrow;
        ZoneModeBanner.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on && _zoneDragging)
        {
            _zoneDragging = false;
            RubberBand.Visibility = Visibility.Collapsed;
        }
        Status(on
            ? "필드 영역 등록 모드 — 라벨 위를 드래그(영역) 또는 클릭(객체)하세요. Esc 또는 버튼으로 해제"
            : "필드 영역 등록 모드 해제");
        DrawZones();
    }

    private void OnViewerMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _displayed is null) return;
        if (ZoneModeButton.IsChecked == true)
        {
            // 필드 영역 등록 모드: 드래그 사각형 시작
            _zoneDragging = true;
            _zoneStart = e.GetPosition(ViewerImage);
            Canvas.SetLeft(RubberBand, _zoneStart.X);
            Canvas.SetTop(RubberBand, _zoneStart.Y);
            RubberBand.Width = 0;
            RubberBand.Height = 0;
            RubberBand.Visibility = Visibility.Visible;
            e.Handled = true;
            return;
        }
        _panning = true;
        _panStart = e.GetPosition(ViewerScroll);
        ViewerScroll.Cursor = Cursors.Hand;
    }

    private void OnViewerMouseMove(object sender, MouseEventArgs e)
    {
        if (_zoneDragging)
        {
            var p = e.GetPosition(ViewerImage);
            Canvas.SetLeft(RubberBand, Math.Min(p.X, _zoneStart.X));
            Canvas.SetTop(RubberBand, Math.Min(p.Y, _zoneStart.Y));
            RubberBand.Width = Math.Abs(p.X - _zoneStart.X);
            RubberBand.Height = Math.Abs(p.Y - _zoneStart.Y);
            e.Handled = true;
            return;
        }
        if (!_panning || e.LeftButton != MouseButtonState.Pressed) return;
        var position = e.GetPosition(ViewerScroll);
        ViewerScroll.ScrollToHorizontalOffset(
            ViewerScroll.HorizontalOffset - (position.X - _panStart.X));
        ViewerScroll.ScrollToVerticalOffset(
            ViewerScroll.VerticalOffset - (position.Y - _panStart.Y));
        _panStart = position;
    }

    private void OnViewerMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_zoneDragging)
        {
            _zoneDragging = false;
            RubberBand.Visibility = Visibility.Collapsed;
            FinishZoneRegistration(_zoneStart, e.GetPosition(ViewerImage));
            e.Handled = true;
            return;
        }
        _panning = false;
        ViewerScroll.Cursor = ZoneModeButton.IsChecked == true ? Cursors.Cross : Cursors.Arrow;
    }

    // ---------------- 필드 영역 등록 (드래그/객체 클릭) ----------------

    private static readonly string[] BuiltinZoneFields =
        ["LOT", "PN", "REF", "MFG DATE", "EXP DATE", "GTIN", "CHINA"];

    /// <summary>드래그(영역) 또는 클릭(객체 스냅)으로 필드 검출 영역을 등록한다.</summary>
    private void FinishZoneRegistration(Point start, Point end)
    {
        if (_displayed is null) return;
        var zoom = Math.Max(0.01, _zoom);
        var x0 = Math.Min(start.X, end.X) / zoom;
        var y0 = Math.Min(start.Y, end.Y) / zoom;
        var x1 = Math.Max(start.X, end.X) / zoom;
        var y1 = Math.Max(start.Y, end.Y) / zoom;
        string? clickedText = null;

        if (x1 - x0 < 8 && y1 - y0 < 8)
        {
            // 클릭 = 객체 선택: 커서 아래 OCR 단어의 상자를 영역으로 사용
            if (!_analyses.TryGetValue(_currentPage, out var analysis))
            {
                Status("OCR이 끝난 뒤 객체를 선택할 수 있습니다.");
                return;
            }
            var word = analysis.Words.FirstOrDefault(w =>
                x0 >= w.Bbox.X - 4 && x0 <= w.Bbox.X + w.Bbox.W + 4
                && y0 >= w.Bbox.Y - 4 && y0 <= w.Bbox.Y + w.Bbox.H + 4);
            if (word is null)
            {
                Dialogs.Info(this, "클릭 위치에서 인식된 객체가 없습니다.\n" +
                                "드래그로 영역을 직접 지정할 수도 있습니다.", "필드 영역 등록");
                return;
            }
            clickedText = word.Text.Trim();
            x0 = word.Bbox.X - word.Bbox.W * 0.2;
            y0 = word.Bbox.Y - word.Bbox.H * 0.5;
            x1 = word.Bbox.X + word.Bbox.W * 1.2;
            y1 = word.Bbox.Y + word.Bbox.H * 1.5;
        }

        var region = (
            X: Math.Clamp(x0 / _displayed.Width, 0, 1) * 100,
            Y: Math.Clamp(y0 / _displayed.Height, 0, 1) * 100,
            W: Math.Clamp((x1 - x0) / _displayed.Width, 0.01, 1) * 100,
            H: Math.Clamp((y1 - y0) / _displayed.Height, 0.01, 1) * 100);
        ShowZoneDialog(clickedText, region);
    }

    private void ShowZoneDialog(string? clickedText,
                                (double X, double Y, double W, double H) regionPercent)
    {
        const string NewCustom = "(새 커스텀 필드 만들기)";
        var fields = _config.Section("fields");
        if (fields["zones"] is not System.Text.Json.Nodes.JsonArray zoneArray)
        {
            zoneArray = new System.Text.Json.Nodes.JsonArray();
            fields["zones"] = zoneArray;
        }
        var fieldNames = BuiltinZoneFields
            .Concat(_engine.Options.CustomFields.Select(c => c.Name))
            .Distinct().Append(NewCustom).ToList();
        var fieldCombo = new ComboBox
        { ItemsSource = fieldNames, SelectedIndex = 0, MinWidth = 220 };
        var standardCombo = new ComboBox
        {
            ItemsSource = new List<string> { "전체" }
                .Concat(_standards.Standards.Keys).ToList(),
            MinWidth = 220,
        };
        standardCombo.SelectedItem = _selectedStandard ?? "전체";
        var nameBox = new TextBox { MinWidth = 220, IsEnabled = false };
        var patternBox = new TextBox
        { MinWidth = 220, Text = clickedText ?? "", IsEnabled = false };
        var header = new TextBlock
        {
            Text = clickedText is null
                ? $"선택 영역: X {regionPercent.X:F0}%, Y {regionPercent.Y:F0}%, " +
                  $"{regionPercent.W:F0}×{regionPercent.H:F0}%"
                : $"선택 객체: '{clickedText}'",
            FontWeight = FontWeights.Bold,
        };
        // '(이미 n개 등록됨)' — 고른 필드·규격에 등록된 영역 수를 즉시 보여 준다
        var existingText = new TextBlock
        { Style = (Style)FindResource("HintText"), Margin = new Thickness(0, 2, 0, 0) };
        string SelectedStandard() =>
            (string?)standardCombo.SelectedItem is { } sel && sel != "전체" ? sel : "";
        string SelectedField() =>
            (string?)fieldCombo.SelectedItem == NewCustom ? nameBox.Text.Trim()
                                                          : (string?)fieldCombo.SelectedItem ?? "";
        void RefreshExisting()
        {
            var count = SelectedField().Length > 0
                ? FieldZoneStore.CountFor(zoneArray, SelectedField(), SelectedStandard()) : 0;
            existingText.Text = count > 0
                ? $"(이미 {count}개 등록됨 — 등록 시 교체/추가를 묻습니다)" : "(이 필드·규격에 등록된 영역 없음)";
        }
        fieldCombo.SelectionChanged += (_, _) =>
        {
            var isNew = (string?)fieldCombo.SelectedItem == NewCustom;
            nameBox.IsEnabled = isNew;
            patternBox.IsEnabled = isNew;
            RefreshExisting();
        };
        standardCombo.SelectionChanged += (_, _) => RefreshExisting();
        nameBox.TextChanged += (_, _) => RefreshExisting();
        var keepModeCheck = new CheckBox
        {
            Content = "등록 후 영역 등록 모드 유지 (연속 등록)",
            IsChecked = _config.SectionBool("zones", "keep_mode", false),
            Margin = new Thickness(0, 10, 0, 0),
            ToolTip = "켜 두면 등록 뒤에도 드래그·클릭으로 계속 등록할 수 있습니다 (Esc로 해제 · 설정은 즉시 저장)",
        };
        void SaveKeepMode(object? _, RoutedEventArgs __)
        {
            _config.Section("zones")["keep_mode"] =
                System.Text.Json.Nodes.JsonValue.Create(keepModeCheck.IsChecked == true);
            try { _config.SaveSettings(); }
            catch (Exception ex) { Status($"설정 저장 실패: {ShortMessage(ex.Message)}", StatusLevel.Error); }
        }
        keepModeCheck.Checked += SaveKeepMode;
        keepModeCheck.Unchecked += SaveKeepMode;
        var okButton = new Button
        {
            Content = "등록", MinWidth = 90, IsDefault = true,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0),
        };
        var panel = new StackPanel { Margin = new Thickness(14) };
        void AddRow(string label, UIElement input)
        {
            panel.Children.Add(new TextBlock
            { Text = label, Margin = new Thickness(0, 6, 0, 2) });
            panel.Children.Add(input);
        }
        panel.Children.Add(header);
        panel.Children.Add(existingText);
        AddRow("이 위치에서 검출할 필드:", fieldCombo);
        AddRow("적용 규격 (전체 = 모든 규격):", standardCombo);
        AddRow("새 커스텀 필드 이름:", nameBox);
        AddRow("새 커스텀 필드 패턴 (찾을 값):", patternBox);
        panel.Children.Add(keepModeCheck);
        okButton.Style = (Style)FindResource("PrimaryButton");
        var cancelButton = new Button { Content = "취소", IsCancel = true, MinWidth = 70 };
        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttonRow.Children.Add(okButton);
        buttonRow.Children.Add(cancelButton);
        panel.Children.Add(buttonRow);
        RefreshExisting();
        var dialog = new Window
        {
            Title = "필드 영역 등록", Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            Owner = Window.GetWindow(this),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
        };
        okButton.Click += (_, _) => dialog.DialogResult = true;
        var keepMode = () => keepModeCheck.IsChecked == true;
        if (dialog.ShowDialog() != true)
        {
            if (!keepMode()) ZoneModeButton.IsChecked = false;
            return;
        }

        var standard = SelectedStandard();
        string zoneField;
        if ((string?)fieldCombo.SelectedItem == NewCustom)
        {
            var name = nameBox.Text.Trim();
            var pattern = patternBox.Text.Trim();
            if (name.Length == 0 || pattern.Length == 0)
            {
                Dialogs.Info(this, "커스텀 필드 이름과 패턴을 입력하세요.", "필드 영역 등록");
                return;
            }
            if (fields["custom"] is not System.Text.Json.Nodes.JsonArray customArray)
            {
                customArray = new System.Text.Json.Nodes.JsonArray();
                fields["custom"] = customArray;
            }
            customArray.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = name, ["pattern"] = pattern,
                ["is_regex"] = false, ["expected"] = null, ["standard"] = "",
            });
            zoneField = name;
        }
        else zoneField = (string?)fieldCombo.SelectedItem ?? "";

        // 같은 필드·규격에 이미 영역이 있으면 교체/추가를 묻는다 (잘못 드래그된 영역이 영구 미검출을
        // 만들지 않게 — 버튼 라벨이 동작을 말하고, Esc/취소는 아무것도 바꾸지 않는다)
        var replace = false;
        var existing = FieldZoneStore.CountFor(zoneArray, zoneField, standard);
        if (existing > 0)
        {
            var choice = ChoiceDialog.Show(Window.GetWindow(this), "필드 영역 등록",
                $"'{zoneField}'({(standard.Length > 0 ? standard : "전체 규격")})에 이미 영역 {existing}개가 있습니다.",
                ("기존 영역 교체", ChoiceStyle.Primary, true),
                ("영역 추가 (둘 다 인정)", ChoiceStyle.Default, false));
            if (choice < 0) return;
            replace = choice == 0;
        }
        var zone = new System.Text.Json.Nodes.JsonObject
        {
            ["field"] = zoneField,
            ["standard"] = standard,
            ["region"] = new System.Text.Json.Nodes.JsonArray(
                System.Text.Json.Nodes.JsonValue.Create(Math.Round(regionPercent.X, 1)),
                System.Text.Json.Nodes.JsonValue.Create(Math.Round(regionPercent.Y, 1)),
                System.Text.Json.Nodes.JsonValue.Create(Math.Round(regionPercent.W, 1)),
                System.Text.Json.Nodes.JsonValue.Create(Math.Round(regionPercent.H, 1))),
        };
        var removed = FieldZoneStore.Upsert(zoneArray, zone, replace);
        _config.SaveSettings();
        if (!keepMode()) ZoneModeButton.IsChecked = false;
        ReloadEngineAndReinspect();   // 끝에 DrawZones() — 새 영역이 점선으로 나타난다
        // BuildEngine과 같은 변환(% → 비율, 최소 0.01)으로 방금 그린 사각형을 찾아 앰버로 번쩍인다
        FlashZone(zoneField, standard, (
            Math.Clamp(Math.Round(regionPercent.X, 1) / 100.0, 0, 1),
            Math.Clamp(Math.Round(regionPercent.Y, 1) / 100.0, 0, 1),
            Math.Max(0.01, Math.Clamp(Math.Round(regionPercent.W, 1) / 100.0, 0, 1)),
            Math.Max(0.01, Math.Clamp(Math.Round(regionPercent.H, 1) / 100.0, 0, 1))));
        var replaced = removed > 0 ? $" (기존 {removed}개 교체)" : "";
        Status(keepMode()
            ? $"'{zoneField}' 필드 영역 등록됨{replaced} — 계속 드래그하거나 Esc로 해제"
            : $"'{zoneField}' 필드 영역 등록됨{replaced} — 이제 이 영역 안의 검출만 인정합니다.");
    }

    // ---------------- 대시보드 / 페이지 슬롯 / CSV ----------------

    /// <summary>세션 집계 — LOT 미매칭 페이지는 판정이 있어도 '미검사'(수동 LOT 선택 대기)로 센다.</summary>
    private SessionStats CurrentStats()
    {
        var passedByPage = _outcomes
            .Where(p => !_lotUnmatchedPages.Contains(p.Key))
            .ToDictionary(p => p.Key, p => p.Value.Passed);
        return SessionStats.Of(_pdf.IsOpen ? _pdf.PageCount : 0, passedByPage, _savedPages);
    }

    private void UpdateDashboard()
    {
        var passed = _outcomes.Values.Count(o => o.Passed);
        var check = _outcomes.Count - passed;
        PassCountText.Text = passed.ToString();
        CheckCountText.Text = check.ToString();
        var stats = CurrentStats();
        ProgressCountText.Text = _pdf.IsOpen
            ? $"검사 {stats.Inspected} / {stats.Total} · 미검사 {stats.Uninspected}"
            : "검사 0 / 0 · 미검사 0";
        ProgressCountText.Foreground =
            (Brush)FindResource(_pdf.IsOpen && stats.Uninspected > 0 ? "WarnBrush" : "HintBrush");
        // 미저장 상시 표시 (UDInspect '● CSV 미저장 / ✓ CSV 저장됨' 규범)
        var unsaved = UnsavedCount;
        if (_outcomes.Count == 0) DirtyText.Text = "";
        else if (unsaved > 0)
        {
            DirtyText.Text = $"● 결과 미저장 {unsaved}건";
            DirtyText.Foreground = (Brush)FindResource("StatusErrorBrush");
        }
        else
        {
            DirtyText.Text = "✓ 결과 저장됨";
            DirtyText.Foreground = (Brush)FindResource("SavedGreenBrush");
        }
    }

    /// <summary>판정은 끝났지만 결과 이미지·이력이 저장되지 않은 페이지 수.</summary>
    public int UnsavedCount => _outcomes.Keys.Count(p => !_savedPages.Contains(p));

    /// <summary>페이지 슬롯 5상태(OCR 대기 / 검사 대기 / 합격 / 확인 필요 / LOT 미매칭) + 저장됨 띠 + 현재 페이지.
    /// 기호(✓ ! ?)를 숫자 앞에 붙여 색각·흑백에서도 구분된다.</summary>
    private void UpdatePageSlots()
    {
        if (!_pdf.IsOpen || _pdf.PageCount <= 1)
        {
            PageSlots.ItemsSource = null;
            return;
        }
        var idleFill = (Brush)FindResource("SlotIdleBrush");
        var idleStroke = (Brush)FindResource("SlotBorderBrush");
        var idleText = (Brush)FindResource("SlotIdleTextBrush");
        var readyFill = (Brush)FindResource("PanelBrush");
        var readyStroke = (Brush)FindResource("CardBorderBrush");
        var passFill = (Brush)FindResource("ScannedBgBrush");
        var checkFill = (Brush)FindResource("WarnBgBrush");
        var lotStroke = (Brush)FindResource("WarnBrush");
        var currentStroke = (Brush)FindResource("PrimaryBrush");
        var normalText = (Brush)FindResource("TextBrush");
        var slots = new List<PageSlotVm>();
        // 페이지가 아주 많으면 슬롯이 읽을 수 없게 작아진다 — 현재 페이지 주변 60개만
        const int MaxSlots = 60;
        var first = 0;
        var last = _pdf.PageCount;
        if (_pdf.PageCount > MaxSlots)
        {
            first = Math.Clamp(_currentPage - MaxSlots / 2, 0, _pdf.PageCount - MaxSlots);
            last = first + MaxSlots;
        }
        // 슬롯 폭이 18px 미만이면 숫자를 빼고 기호만 (겹침 방지)
        var slotWidth = PageSlots.ActualWidth > 0 ? PageSlots.ActualWidth / (last - first) : 30;
        var symbolOnly = slotWidth < 18;
        for (var page = first; page < last; page++)
        {
            var hasOutcome = _outcomes.TryGetValue(page, out var outcome);
            var lotUnmatched = _lotUnmatchedPages.Contains(page);
            var isCurrent = page == _currentPage;
            var saved = _savedPages.Contains(page);
            Brush fill;
            Brush stroke;
            double thickness;
            string symbol;
            string tip;
            if (!hasOutcome)
            {
                var ready = _analyses.ContainsKey(page);
                fill = ready ? readyFill : idleFill;
                stroke = ready ? readyStroke : idleStroke;
                thickness = 1;
                symbol = "";
                tip = ready ? $"{page + 1}페이지: 검사 대기" : $"{page + 1}페이지: OCR 대기";
            }
            else if (lotUnmatched)
            {
                fill = checkFill;
                stroke = lotStroke;
                thickness = 2;
                symbol = "?";
                tip = $"{page + 1}페이지: LOT 미매칭 (수동 선택 필요)";
            }
            else if (outcome!.Passed)
            {
                fill = passFill;
                stroke = readyStroke;
                thickness = 1;
                symbol = "✓";
                tip = $"{page + 1}페이지: 합격 ({outcome.Record.Lot})";
            }
            else
            {
                fill = checkFill;
                stroke = readyStroke;
                thickness = 1;
                symbol = "!";
                tip = $"{page + 1}페이지: 확인 필요 ({outcome.Record.Lot})";
            }
            if (saved) tip += " · 저장됨";
            if (isCurrent) tip += " · 현재";
            var number = symbolOnly && symbol.Length > 0 ? symbol : symbol + (page + 1);
            slots.Add(new PageSlotVm(
                page, number, tip, fill,
                isCurrent ? currentStroke : stroke,
                new Thickness(isCurrent ? 3 : thickness),
                hasOutcome ? normalText : idleText,
                saved,
                isCurrent ? FontWeights.Bold : FontWeights.Normal));
        }
        PageSlots.ItemsSource = slots;
    }

    private void OnPageSlotClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: int page }) Navigate(page);
    }

    /// <summary>전체 페이지(미검사 포함) CSV — Core InspectionCsv 고정 열 + UTF-8 BOM + ="값" 텍스트 보호.
    /// 확인 필요·미검사·미저장이 있으면 내보내기 전에 확인한다 (프로그램 표시는 참고값).</summary>
    private void OnExportCsv(object sender, RoutedEventArgs e)
    {
        if (!_pdf.IsOpen)
        {
            Dialogs.Info(this, "내보낼 검사 결과가 없습니다 — PDF를 먼저 여세요.", "CSV");
            return;
        }
        var stats = CurrentStats();
        if (stats.NeedsConfirmation
            && !Dialogs.Confirm(this, stats.ConfirmMessage(), "내보내기 전 확인"))
            return;
        var dialog = new SaveFileDialog
        {
            Title = "검사 결과 CSV", Filter = "CSV 파일|*.csv",
            FileName = $"검사결과_{DateTime.Now:yyyyMMdd_HHmm}.csv",
        };
        if (dialog.ShowDialog() != true) return;
        var rows = Enumerable.Range(0, _pdf.PageCount).Select(page => (
            page,
            _outcomes.TryGetValue(page, out var outcome) ? outcome : null,
            _savedFile.TryGetValue(page, out var img) ? img : null));
        var textProtect = _config.SectionBool("export", "csv_text_protect", true);
        var csv = InspectionCsv.Build(rows, _pdf.Path ?? "", App.InformationalVersion, textProtect);
        ExportGuard.Run("CSV", dialog.FileName,
            () => File.WriteAllText(dialog.FileName, csv, new System.Text.UTF8Encoding(true)),
            Status);
    }

    // ---------------- OCR 교정 등록 ----------------

    private void OnFieldDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FieldGrid.SelectedItem is not FieldRowVm row || row.Term is "-" or "") return;
        if (!_analyses.TryGetValue(_currentPage, out var analysis)) return;

        // 기대값과 '비슷하지만 불일치'한 OCR 단어 후보 (혼동 정규형 거리 기반)
        var term = row.Term;
        var candidates = analysis.Words
            .Select(w => w.Text.Trim())
            .Distinct()
            .Where(text => !text.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Select(text => (Text: text, Distance: BoundedDistance(
                _corrections.Canonicalize(text).ToUpperInvariant(),
                _corrections.Canonicalize(term).ToUpperInvariant(), 3)))
            .Where(c => c.Distance >= 0 && c.Distance <= 2)
            .OrderBy(c => c.Distance)
            .Select(c => c.Text)
            .Take(12)
            .ToList();

        // 아예 검출되지 않아 가까운 후보조차 없으면: 원인 진단 + 인식 강화 제안
        if (candidates.Count == 0)
        {
            candidates = analysis.Words
                .Select(w => w.Text.Trim())
                .Distinct()
                .Where(t => t.Length >= 2
                    && !t.Contains(term, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(t => InspectionEngine.SimilarityScore(t, term, 95))
                .Take(12)
                .ToList();
            var choice = ChoiceDialog.Show(Window.GetWindow(this), "미검출 진단",
                DiagnoseMissingField(row.Field, term, analysis) +
                "\n\nOCR 인식 로그에서 해당 구간이 실제로 어떻게 읽혔는지 확인하고 교정을 " +
                "등록하거나, 인식 강화 파라미터(대비 보정 + 렌더 배율 5.0 + 최소 신뢰도 0)를 " +
                "적용해 이 PDF를 다시 OCR할 수 있습니다.",
                ("OCR 로그 열기", ChoiceStyle.Primary, true),
                ("인식 강화 후 이 PDF 전체 재OCR (AWS 과금 발생)", ChoiceStyle.Caution, false));
            if (choice < 0) return;
            if (choice == 1)
            {
                ApplyRecognitionBoost();
                return;
            }
            OpenOcrLog(row.Field, term);
            return;
        }

        var dialog = new CorrectionDialog(row.Field, term, candidates, _corrections)
        { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
        {
            ReloadEngineAndReinspect();
            Status("교정이 등록되었습니다 — 이후 검사부터 자동 적용됩니다.");
        }
    }

    /// <summary>현재 페이지의 OCR 인식 로그 창 열기.</summary>
    private void OnShowOcrLog(object sender, RoutedEventArgs e) => OpenOcrLog("", "");

    private void OpenOcrLog(string field, string expectedTerm)
    {
        if (!_analyses.TryGetValue(_currentPage, out var analysis)
            || _displayed is null)
        {
            Dialogs.Info(this, "이 페이지의 OCR이 아직 끝나지 않았습니다.", "OCR 로그");
            return;
        }
        var log = new OcrLogWindow(analysis.Words,
                                   (_displayed.Width, _displayed.Height),
                                   field, expectedTerm, _corrections, _merges)
        { Owner = Window.GetWindow(this) };
        log.ShowDialog();
        if (log.Changed)   // 병합/교정이 있었으면 (X로 닫아도) 재검사
        {
            ReloadEngineAndReinspect();
            Status("병합/교정이 등록되었습니다 — 이후 검사부터 자동 적용됩니다.");
        }
    }

    /// <summary>필드가 전혀 검출되지 않은 원인 분석 문구.</summary>
    private string DiagnoseMissingField(string field, string term,
                                        PageAnalysis analysis)
    {
        var reasons = new List<string>();
        var words = analysis.Words;
        if (words.Count == 0)
            reasons.Add("이 페이지에서 OCR 단어가 하나도 인식되지 않았습니다 — " +
                        "이미지 품질 또는 OCR 엔진 설정을 확인하세요.");
        else
        {
            var average = words.Average(w => (double)w.Confidence);
            if (average < 80)
                reasons.Add($"페이지 OCR 평균 신뢰도가 낮습니다 ({average:F0}%).");
            var best = words
                .OrderByDescending(w => InspectionEngine.SimilarityScore(
                    w.Text.Trim(), term, w.Confidence))
                .First();
            var bestScore = InspectionEngine.SimilarityScore(
                best.Text.Trim(), term, best.Confidence);
            if (bestScore >= 55)
                reasons.Add($"가장 유사한 인식 결과: '{best.Text.Trim()}' " +
                            $"(유사도 {bestScore:F0}) — 교정 등록으로 해결할 수 있습니다.");
            else
                reasons.Add("기대값과 비슷한 인식 결과가 없습니다 — 값이 여러 단어로 " +
                            "분리되었거나 렌더 해상도가 부족할 수 있습니다.");
            if (field == "GTIN")
            {
                var joined = _corrections.Canonicalize(
                    string.Concat(words.Select(w => w.Text.Trim())));
                if (term.Length > 0 && joined.Contains(term, StringComparison.Ordinal))
                    reasons.Add("GTIN 숫자열이 여러 단어로 나뉘어 인식됐습니다 — " +
                                "렌더 배율을 높이면 붙여서 인식됩니다.");
            }
        }
        var minConfidence = _config.SectionInt("ocr", "min_confidence", 0);
        if (minConfidence > 0)
            reasons.Add($"최소 신뢰도 필터가 {minConfidence}%라 저신뢰 단어가 " +
                        "버려졌을 수 있습니다 (0 권장).");
        if (!_config.SectionBool("ocr", "contrast_stretch", false))
            reasons.Add("이미지 대비 보정이 꺼져 있습니다 (흐린 인쇄에 유리).");
        return $"'{field}' 필드(기대값: {term})가 검출되지 않았습니다.\n\n[원인 분석]\n· "
               + string.Join("\n· ", reasons);
    }

    /// <summary>인식 강화 파라미터 적용 후 현재 PDF 재-OCR.</summary>
    private void ApplyRecognitionBoost()
    {
        var ocr = _config.Section("ocr");
        ocr["contrast_stretch"] = true;
        ocr["min_confidence"] = 0;
        if (_config.GetDouble("pdf_render_zoom", 4.0) < 5.0)
            _config.Settings["pdf_render_zoom"] = 5.0;
        _config.SaveSettings();
        ApplyConfig();   // 설정 서명 변경 감지 → 렌더/분석 무효화 후 재OCR
        Status("인식 강화 파라미터 적용 — 페이지를 다시 OCR합니다.");
    }

    /// <summary>제한 거리 이하일 때만 편집 거리 반환 (초과 시 -1).</summary>
    private static int BoundedDistance(string a, string b, int limit)
    {
        if (Math.Abs(a.Length - b.Length) > limit) return -1;
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowMin = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1),
                                      previous[j - 1] + cost);
                rowMin = Math.Min(rowMin, current[j]);
            }
            if (rowMin > limit) return -1;
            (previous, current) = (current, previous);
        }
        return previous[b.Length] <= limit ? previous[b.Length] : -1;
    }

    // ---------------- 저장 ----------------

    private static string DefaultSaveDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "LaVIS_결과");

    private string? _saveDirWarnedFor;

    /// <summary>결과 저장 폴더 — 설정값이 올바른 절대 경로가 아니면 기본 폴더로 폴백하고 한 번만 경고한다.</summary>
    private string SaveDir()
    {
        var configured = _config.GetString("save_directory").Trim();
        if (configured.Length == 0) return DefaultSaveDir();
        if (PathRules.IsValidDirectory(configured)) return configured;
        if (_saveDirWarnedFor != configured)
        {
            _saveDirWarnedFor = configured;
            Status($"저장 경로가 올바르지 않아 기본 폴더에 저장합니다: {DefaultSaveDir()} (설정값: {configured})",
                   StatusLevel.Warn);
            AppLog.Warn($"save_directory 무효 → 기본 폴더 폴백: '{configured}'");
        }
        return DefaultSaveDir();
    }

    /// <summary>폴더 접근 가능 여부를 UI를 막지 않고 확인 (네트워크 드라이브 끊김 대비 3초 타임아웃).
    /// 폴더가 아직 없어도 상위 폴더에 닿으면 접근 가능으로 본다(첫 저장 때 만들어진다).</summary>
    private static async Task<bool> ProbeDirectoryAsync(string dir)
    {
        var probe = Task.Run(() =>
        {
            try
            {
                if (Directory.Exists(dir)) return true;
                var parent = Path.GetDirectoryName(Path.GetFullPath(dir));
                return parent is { Length: > 0 } && Directory.Exists(parent);
            }
            catch (Exception) { return false; }
        });
        var finished = await Task.WhenAny(probe, Task.Delay(3000));
        return finished == probe && probe.Result;
    }

    /// <summary>저장 폴더 접근 확인 — 실패/타임아웃이면 경고하고(옵션) 자동 저장을 해제한다.</summary>
    private async Task VerifySaveDirAsync(bool disableAutoSaveOnFailure)
    {
        var dir = SaveDir();
        if (await ProbeDirectoryAsync(dir)) return;
        Status($"저장 폴더에 접근할 수 없습니다 (네트워크 드라이브 연결 확인): {dir}", StatusLevel.Warn);
        AppLog.Warn($"저장 폴더 접근 실패/타임아웃: {dir}");
        if (disableAutoSaveOnFailure && AutoSaveCheck.IsChecked == true)
            AutoSaveCheck.IsChecked = false;   // OnAutoSaveToggled가 설정에 반영
    }

    private void OnPickSaveDir(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "결과 저장 폴더" };
        if (dialog.ShowDialog() != true) return;
        if (!PathRules.IsValidDirectory(dialog.FolderName))
        {
            Dialogs.Warn(this, $"올바른 절대 경로가 아닙니다: {dialog.FolderName}", "저장 경로");
            return;
        }
        _config.Settings["save_directory"] = dialog.FolderName;
        _config.SaveSettings();
        _saveDirWarnedFor = null;
        Status($"저장 경로: {dialog.FolderName}");
        _ = VerifySaveDirAsync(disableAutoSaveOnFailure: true);
    }

    /// <summary>결과 저장 폴더를 탐색기로 연다 (없으면 만든다).</summary>
    private void OnOpenSaveDir(object sender, RoutedEventArgs e)
    {
        var dir = SaveDir();
        try
        {
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            { FileName = "explorer.exe", Arguments = $"\"{dir}\"", UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status($"폴더를 열 수 없습니다: {ExportGuard.Describe(ex, dir)}", StatusLevel.Warn);
            AppLog.Warn($"폴더 열기 실패: {dir} — {ex.Message}");
        }
    }

    private bool _loadingConfig;

    /// <summary>툴바 옵션은 UDInspect처럼 바꾸는 즉시 저장 (설정 창을 거치지 않음).
    /// 켤 때 저장 경로가 무효면 체크를 해제하고 경고, 유효하면 접근 확인(비차단)을 시작한다.</summary>
    private void OnAutoSaveToggled(object sender, RoutedEventArgs e)
    {
        if (_loadingConfig || _config is null) return;
        if (AutoSaveCheck.IsChecked == true)
        {
            var configured = _config.GetString("save_directory").Trim();
            if (configured.Length > 0 && !PathRules.IsValidDirectory(configured))
            {
                AutoSaveCheck.IsChecked = false;   // 재진입 → 아래 저장 분기에서 false 기록
                Status($"자동 저장을 켤 수 없습니다 — 저장 경로가 올바르지 않습니다: {configured} ([저장 경로]로 다시 지정)",
                       StatusLevel.Warn);
                return;
            }
        }
        try
        {
            _config.Settings["auto_save_default"] =
                System.Text.Json.Nodes.JsonValue.Create(AutoSaveCheck.IsChecked == true);
            _config.SaveSettings();
        }
        catch (Exception ex)
        {
            Status($"설정 저장 실패 (재실행 시 이전 설정으로 돌아갈 수 있음): {ShortMessage(ex.Message)}",
                   StatusLevel.Error);
        }
        if (AutoSaveCheck.IsChecked == true) _ = VerifySaveDirAsync(disableAutoSaveOnFailure: true);
    }

    private void OnSaveCurrent(object sender, RoutedEventArgs e)
    {
        if (!_pdf.IsOpen || !_outcomes.TryGetValue(_currentPage, out var outcome))
        {
            Dialogs.Info(this, "저장할 검사 결과가 없습니다.", "저장");
            return;
        }
        // 같은 판정이 이미 저장돼 있으면 확인 (기본 '아니오') — 새 번호 파일·이력 행이 의도치 않게 늘지 않게
        if (_savedSignature.TryGetValue(_currentPage, out var savedSig)
            && savedSig == outcome.Signature())
        {
            var saved = _savedFile.TryGetValue(_currentPage, out var file)
                ? Path.GetFileName(file) : "저장됨";
            if (!Dialogs.Confirm(this,
                    $"이 페이지의 동일한 판정이 이미 저장되어 있습니다 ({saved}).\n" +
                    "다시 저장하면 새 번호로 파일과 이력이 추가됩니다. 저장할까요?", "결과 저장"))
                return;
        }
        SaveOutcome(_currentPage, outcome, RenderProcessed(_currentPage), notify: true);
    }

    private void SaveOutcome(int page, InspectionOutcome outcome, SKBitmap image,
                             bool notify)
    {
        var counter = _history?.NextFileCounter() ?? 1;
        var filename = Annotate.MakeResultFilename(
            counter, outcome.Record.Lot, outcome.Record.Ref, outcome.Passed);
        var dir = SaveDir();
        // 같은 이름이 있으면 _(2), _(3)… 로 비켜 간다 — 기존 결과 이미지를 덮어쓰지 않는다
        var path = PathRules.UniquePath(Path.Combine(dir, filename));
        filename = Path.GetFileName(path);
        try
        {
            Annotate.SaveAnnotatedJpeg(image, outcome, _standards.FieldColors, path,
                                       _config.GetDouble("save_scale", 0.5),
                                       _config.GetInt("jpeg_quality", 90),
                                       CurrentOverlayStyle());
        }
        catch (Exception ex)
        {
            // 원인별 안내 (권한/경로/디스크/네트워크) — 자동 저장은 상태바+로그, 수동 저장만 대화상자
            var reason = ExportGuard.Describe(ex, dir);
            if (notify) Dialogs.Error(this, reason, "저장 실패");
            else { Status($"자동 저장 실패: {reason}", StatusLevel.Error); AppLog.Error("자동 저장 실패", ex); }
            return;
        }
        _savedPages.Add(page);
        _savedSignature[page] = outcome.Signature();
        _savedFile[page] = path;
        UpdateDashboard();
        UpdatePageSlots();   // 저장됨 띠
        try { _history?.RecordInspection(outcome, path, "pdf", _pdf.Path, page); }
        catch (Exception ex)
        {
            // 이미지는 저장됐으므로 이력 기록 실패만 알리고 계속 진행
            Status($"저장됨: {filename} (이력 기록 실패: {ShortMessage(ex.Message)})", StatusLevel.Warn);
            return;
        }
        Status($"저장됨: {filename}");
        if (notify) FlashBadge((SolidColorBrush)FindResource("ScannedBgBrush"));   // 정보성 팝업 대신 시각 피드백
    }
}

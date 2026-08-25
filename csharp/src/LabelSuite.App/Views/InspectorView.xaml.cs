// 라벨 검사 탭 — 파이썬 inspector_page.py 포팅.
// PDF 로드 시 전 페이지 백그라운드 선행 OCR(프리페치) + 캐시로 페이지 이동 무지연 표시.
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LabelSuite.App.Services;
using LabelSuite.Core;
using Microsoft.Win32;
using SkiaSharp;

namespace LabelSuite.App.Views;

public partial class InspectorView : UserControl
{
    // 전처리 서명 — 라벨 정렬 옵션이 바뀌면 OCR 캐시를 분리한다
    private string PreprocessSig =>
        _config?.SectionBool("preprocess", "crop_label", true) == true
            ? "crop-v1" : "none-v1";

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

    public event Action<string>? StatusMessage;
    public event Action<bool, string>? AwsStatusChanged;

    private OcrCorrections _corrections = null!;
    private GlyphLibrary _glyphLibrary = null!;
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
                           OcrCorrections corrections, GlyphLibrary glyphs)
    {
        _config = config;
        _history = history;
        _corrections = corrections;
        _glyphLibrary = glyphs;
        _glyphEngine = new GlyphOcrEngine(_glyphLibrary);
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
        AutoSaveCheck.IsChecked = _config.GetBool("auto_save_default", false);

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
            MessageBox.Show("PDF를 먼저 여세요.", "양식 학습");
            return;
        }
        var imageRules = _formRules
            .Where(r => r.UseImage && r.Name.Trim().Length > 0).ToList();
        if (imageRules.Count == 0)
        {
            MessageBox.Show(
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
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock
        { Text = "현재 페이지를 어느 양식의 기준 이미지로 학습할까요?" });
        panel.Children.Add(combo);
        panel.Children.Add(okButton);
        var dialog = new Window
        {
            Title = "양식 이미지 학습", Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            Owner = Window.GetWindow(this),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
        };
        okButton.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() != true || combo.SelectedIndex < 0) return;

        var rule = imageRules[combo.SelectedIndex];
        var image = RenderProcessed(_currentPage);   // 캐시 소유 — 해제 금지
        if (_formDetector.LearnTemplate(rule, image))
        {
            StatusMessage?.Invoke(
                $"양식 '{rule.Name}' 기준 이미지 학습 완료 " +
                $"(영역 {rule.Region.X * 100:F0}%,{rule.Region.Y * 100:F0}% " +
                $"{rule.Region.W * 100:F0}x{rule.Region.H * 100:F0}%)");
            ReinspectCurrent();
        }
        else
            MessageBox.Show("규칙 영역이 너무 작습니다. 설정에서 영역(%)을 확인하세요.",
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
    }

    /// <summary>교정 사전이 바뀐 뒤 재검사 (설정 창/교정 등록에서 호출).</summary>
    public void ReloadEngineAndReinspect()
    {
        _engine = BuildEngine();
        ReinspectCurrent();
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
            StatusMessage?.Invoke("AWS 인증 실패 — OCR 실행 전에 설정에서 자격증명을 확인하세요.");
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
                MessageBox.Show(string.Join("\n", warnings.Take(20)), "목록 경고",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "목록 오류",
                            MessageBoxButton.OK, MessageBoxImage.Error);
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
            MessageBox.Show(ex.Message, "PDF 오류",
                            MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        _currentPage = 0;
        _analyses.Clear();
        _outcomes.Clear();
        _manualLotPages.Clear();
        _pageLotChoice.Clear();
        _manualStandardPages.Clear();
        _typeLearnedPages.Clear();
        _typeAlarmPages.Clear();
        ClearProcessed();
        _worker.NewGeneration();
        foreach (var button in new[] { FirstButton, PrevButton, NextButton, LastButton })
            button.IsEnabled = true;
        PdfNameLabel.Text = $"{Path.GetFileName(path)} · {_pdf.PageCount}페이지";
        StatusMessage?.Invoke($"PDF 로드: {Path.GetFileName(path)} ({_pdf.PageCount}페이지)");
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
            _worker.Submit(page, key, () => RenderProcessed(pageCopy),
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

    private void OnKeyDown(object sender, KeyEventArgs e) => HandleGlobalKey(e);

    /// <summary>전역 키 입력 (MainWindow가 검사 탭 활성 시 라우팅) —
    /// ←/→ 페이지 이동, WASD 이미지 이동, Q 확대 / E 축소.</summary>
    public void HandleGlobalKey(KeyEventArgs e)
    {
        // 텍스트 입력 중에는 개입하지 않는다
        if (e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase
            or PasswordBox or ComboBox or ComboBoxItem) return;
        if (!_pdf.IsOpen) return;
        const double PanStep = 90;
        switch (e.Key)
        {
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
                           () => RenderProcessed(pageCopy), priority: 0);
            _worker.Prioritize(page);
        }
        UpdatePrefetchLabel();
        UpdatePageSlots();
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

    private void OnPageFailed(int generation, int page, string message)
    {
        if (generation != _worker.Generation) return;
        StatusMessage?.Invoke($"{page + 1}페이지 OCR 실패: {message}");
        if (page == _currentPage) ClearResultPanel($"OCR 실패: {message}");
        MessageBox.Show(message, "OCR 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void UpdatePrefetchLabel()
    {
        if (!_pdf.IsOpen) { PrefetchLabel.Text = ""; return; }
        var done = _analyses.Count;
        var total = _pdf.PageCount;
        PrefetchLabel.Text = done >= total
            ? $"OCR 완료 {done}/{total} ✓" : $"OCR 진행 {done}/{total}…";
        PrefetchLabel.Foreground = done >= total
            ? (Brush)FindResource("SuccessBrush") : (Brush)FindResource("MutedBrush");
    }

    // ---------------- 검사 ----------------

    private sealed record FieldRowVm(string Field, string Term, string Count, string State);
    private sealed record BarcodeRowVm(string Order, string Symbology, string Grade,
                                       string Value, string State);
    private sealed record PageSlotVm(int PageIndex, string Number, string Tip,
                                     Brush Fill, Brush Stroke, Thickness StrokeThickness,
                                     Brush TextBrush);

    private void RunInspection(int page, PageAnalysis analysis, SKBitmap image,
                               bool fit = false)
    {
        // 이 페이지에서 사용자가 직접 고른 규격 — 모든 자동 선택보다 우선
        var manualStandard = _manualStandardPages.TryGetValue(page, out var chosenStd)
            && _standards.Standards.ContainsKey(chosenStd) ? chosenStd : null;

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
        }
        else if (_records.Count > 0)
        {
            var match = _engine.MatchLot(analysis.Words, _records);
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
            }
            // 이 페이지에서 LOT 후보를 못 찾으면 현재 선택 유지
        }

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
        if (_formRules.Count > 0)
        {
            var form = _formDetector.Detect(_formRules, analysis.Words, image);
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
            ClearResultPanel(_records.Count > 0
                ? "LOT을 선택하면 검사를 시작합니다" : "검사 목록을 먼저 로드하세요");
            return;
        }

        var standardName = _selectedStandard ?? _standards.Standards.Keys.First();
        var barcodeChecks = BarcodeDetector.CrossCheckHits(analysis.Barcodes, record);
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
                                      (image.Width, image.Height));
        _outcomes[page] = outcome;
        CheckLabelType(page, formatKey, analysis, record, outcome);
        ShowOutcome(outcome, analysis);
        using var annotated = Annotate.RenderOverlays(
            image, outcome.AllMatches, _standards.FieldColors, CurrentOverlayStyle());
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
                StatusBadgeText.Text += $"  ·  ⚠ OCR 신뢰도 {quality.Average:F0}%";
                StatusMessage?.Invoke($"⚠ p{page + 1}: {quality.Summary}");
            }
        }
        SetViewerImage(annotated, fit);
        UpdateDashboard();
        UpdatePageSlots();
        if (AutoSaveCheck.IsChecked == true)
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
            StatusMessage?.Invoke($"⚠ 유형 이상 (p{page + 1}): {report.Summary}");
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
            var answer = MessageBox.Show(
                $"이 라벨이 지금까지 학습된 유형과 다릅니다.\n\n{report.Summary}{detail}\n\n" +
                "라벨 개정 등 정상적인 변경이면 [예]를 눌러 이 라벨을 새 유형으로 " +
                "학습하세요. 인쇄 오류가 의심되면 [아니오]를 누르고 라벨을 확인하세요.",
                "라벨 유형 이상 감지", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes && _typeLearnedPages.Add(page))
                _profiler.Learn(formatKey, analysis.Words, record);
        }
        else if (outcome.Passed && _typeLearnedPages.Add(page))
        {
            _profiler.Learn(formatKey, analysis.Words, record);
            if (report.SampleCount < _profiler.MinSamples)
                StatusMessage?.Invoke(
                    $"라벨 유형 학습 중 ({report.SampleCount + 1}/{_profiler.MinSamples})");
        }
    }

    private void ShowOutcome(InspectionOutcome outcome, PageAnalysis? analysis = null)
    {
        _lastAnalysisShown = analysis;
        if (outcome.Passed)
        {
            StatusBadgeText.Text = $"✓ 합격 (PASSED) · 규격 {outcome.Standard.DisplayName}";
            StatusBadgeText.Foreground = (Brush)FindResource("SuccessBrush");
            StatusBadge.Background = (Brush)FindResource("SuccessBgBrush");
        }
        else
        {
            StatusBadgeText.Text = $"⚠ 확인 필요 (CHECK) · 규격 {outcome.Standard.DisplayName}";
            StatusBadgeText.Foreground = (Brush)FindResource("WarnBrush");
            StatusBadge.Background = (Brush)FindResource("WarnBgBrush");
        }
        FieldGrid.ItemsSource = outcome.Fields.Values
            .Where(f => f.Field != "PRODUCTS")   // 요청: 필드별 검출에서 PRODUCTS 제외
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
                }
                catch (Gs1ParseException)
                {
                    value = hit.Text.Replace('\x1d', '|');
                    state = "-";
                }
            }
            else
            {
                value = hit.Text;
                state = "-";
            }
            barcodeRows.Add(new BarcodeRowVm($"#{i + 1}", hit.Symbology,
                                             hit.Grade ?? "", value, state));
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

    private void ClearResultPanel(string message)
    {
        StatusBadgeText.Text = message;
        StatusBadgeText.Foreground = (Brush)FindResource("MutedBrush");
        StatusBadge.Background = (Brush)FindResource("ReadoutBrush");
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
        ViewerScroll.Cursor = Cursors.Arrow;
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
                StatusMessage?.Invoke("OCR이 끝난 뒤 객체를 선택할 수 있습니다.");
                return;
            }
            var word = analysis.Words.FirstOrDefault(w =>
                x0 >= w.Bbox.X - 4 && x0 <= w.Bbox.X + w.Bbox.W + 4
                && y0 >= w.Bbox.Y - 4 && y0 <= w.Bbox.Y + w.Bbox.H + 4);
            if (word is null)
            {
                MessageBox.Show("클릭 위치에서 인식된 객체가 없습니다.\n" +
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
        fieldCombo.SelectionChanged += (_, _) =>
        {
            var isNew = (string?)fieldCombo.SelectedItem == NewCustom;
            nameBox.IsEnabled = isNew;
            patternBox.IsEnabled = isNew;
        };
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
        panel.Children.Add(new TextBlock
        {
            Text = clickedText is null
                ? $"선택 영역: X {regionPercent.X:F0}%, Y {regionPercent.Y:F0}%, " +
                  $"{regionPercent.W:F0}×{regionPercent.H:F0}%"
                : $"선택 객체: '{clickedText}'",
            FontWeight = FontWeights.Bold,
        });
        AddRow("이 위치에서 검출할 필드:", fieldCombo);
        AddRow("적용 규격 (전체 = 모든 규격):", standardCombo);
        AddRow("새 커스텀 필드 이름:", nameBox);
        AddRow("새 커스텀 필드 패턴 (찾을 값):", patternBox);
        panel.Children.Add(okButton);
        var dialog = new Window
        {
            Title = "필드 영역 등록", Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            Owner = Window.GetWindow(this),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
        };
        okButton.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() != true) { ZoneModeButton.IsChecked = false; return; }

        var standard = (string?)standardCombo.SelectedItem is { } sel && sel != "전체"
            ? sel : "";
        var fields = _config.Section("fields");
        string zoneField;
        if ((string?)fieldCombo.SelectedItem == NewCustom)
        {
            var name = nameBox.Text.Trim();
            var pattern = patternBox.Text.Trim();
            if (name.Length == 0 || pattern.Length == 0)
            {
                MessageBox.Show("커스텀 필드 이름과 패턴을 입력하세요.", "필드 영역 등록");
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

        if (fields["zones"] is not System.Text.Json.Nodes.JsonArray zoneArray)
        {
            zoneArray = new System.Text.Json.Nodes.JsonArray();
            fields["zones"] = zoneArray;
        }
        zoneArray.Add(new System.Text.Json.Nodes.JsonObject
        {
            ["field"] = zoneField,
            ["standard"] = standard,
            ["region"] = new System.Text.Json.Nodes.JsonArray(
                System.Text.Json.Nodes.JsonValue.Create(Math.Round(regionPercent.X, 1)),
                System.Text.Json.Nodes.JsonValue.Create(Math.Round(regionPercent.Y, 1)),
                System.Text.Json.Nodes.JsonValue.Create(Math.Round(regionPercent.W, 1)),
                System.Text.Json.Nodes.JsonValue.Create(Math.Round(regionPercent.H, 1))),
        });
        _config.SaveSettings();
        ZoneModeButton.IsChecked = false;
        ReloadEngineAndReinspect();
        StatusMessage?.Invoke(
            $"'{zoneField}' 필드 영역 등록됨 — 이제 이 영역 안의 검출만 인정합니다.");
    }

    // ---------------- 대시보드 / 페이지 슬롯 / CSV ----------------

    private void UpdateDashboard()
    {
        var passed = _outcomes.Values.Count(o => o.Passed);
        var check = _outcomes.Count - passed;
        PassCountText.Text = passed.ToString();
        CheckCountText.Text = check.ToString();
        ProgressCountText.Text = _pdf.IsOpen
            ? $"검사 {_outcomes.Count} / {_pdf.PageCount} 페이지" : "검사 0 / 0 페이지";
    }

    private void UpdatePageSlots()
    {
        if (!_pdf.IsOpen || _pdf.PageCount <= 1)
        {
            PageSlots.ItemsSource = null;
            return;
        }
        var gray = new SolidColorBrush(Color.FromRgb(0xEF, 0xEF, 0xEF));
        var green = new SolidColorBrush(Color.FromRgb(0xD7, 0xEF, 0xD7));  // verifier SN 슬롯 녹색
        var orange = new SolidColorBrush(Color.FromRgb(0xFD, 0xEB, 0xD0));
        var currentStroke = (Brush)FindResource("AccentBrush");
        var normalStroke = new SolidColorBrush(Color.FromRgb(0xCF, 0xCF, 0xCF));
        var slots = new List<PageSlotVm>();
        for (var page = 0; page < _pdf.PageCount; page++)
        {
            var hasOutcome = _outcomes.TryGetValue(page, out var outcome);
            var fill = !hasOutcome ? gray : outcome!.Passed ? green : orange;
            var tip = !hasOutcome
                ? (_analyses.ContainsKey(page) ? $"{page + 1}페이지: 검사 대기"
                                               : $"{page + 1}페이지: OCR 대기")
                : outcome!.Passed ? $"{page + 1}페이지: 합격 ({outcome.Record.Lot})"
                                  : $"{page + 1}페이지: 확인 필요 ({outcome.Record.Lot})";
            var isCurrent = page == _currentPage;
            slots.Add(new PageSlotVm(
                page, (page + 1).ToString(), tip, fill,
                isCurrent ? currentStroke : normalStroke,
                new Thickness(isCurrent ? 2 : 1),
                new SolidColorBrush(hasOutcome
                    ? Colors.Black : Color.FromRgb(0x9A, 0x9A, 0x9A))));
        }
        PageSlots.ItemsSource = slots;
    }

    private void OnPageSlotClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: int page }) Navigate(page);
    }

    private void OnExportCsv(object sender, RoutedEventArgs e)
    {
        if (_outcomes.Count == 0)
        {
            MessageBox.Show("내보낼 검사 결과가 없습니다.", "CSV",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "검사 결과 CSV", Filter = "CSV 파일|*.csv",
            FileName = $"검사결과_{DateTime.Now:yyyyMMdd_HHmm}.csv",
        };
        if (dialog.ShowDialog() != true) return;
        var lines = new List<string>
        { "페이지,LOT,REF,규격,판정,필드요약,바코드요약" };
        foreach (var (page, outcome) in _outcomes.OrderBy(p => p.Key))
        {
            var fieldSummary = string.Join(" / ", outcome.Fields.Values
                .Where(f => f.Expected is not null)
                .Select(f => $"{f.Field} {f.Found}:{f.Expected}"));
            var barcodeSummary = string.Join(" / ", outcome.BarcodeChecks
                .Select(c => $"{c.Field} {(c.Matched ? "OK" : "NG")}"));
            static string Escape(string value) =>
                value.Contains(',') || value.Contains('"')
                    ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
            lines.Add(string.Join(",",
                (page + 1).ToString(), Escape(outcome.Record.Lot),
                Escape(outcome.Record.Ref), Escape(outcome.Standard.DisplayName),
                outcome.Passed ? "합격" : "확인필요",
                Escape(fieldSummary), Escape(barcodeSummary)));
        }
        File.WriteAllText(dialog.FileName, string.Join("\r\n", lines),
                          System.Text.Encoding.UTF8);
        StatusMessage?.Invoke($"CSV 저장됨: {dialog.FileName}");
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
            var choice = MessageBox.Show(
                DiagnoseMissingField(row.Field, term, analysis) +
                "\n\n[예] 이 페이지의 OCR 인식 로그를 열어 해당 구간이 실제로 어떻게 " +
                "읽혔는지 확인하고 교정을 등록합니다.\n" +
                "[아니오] 인식 강화 파라미터(대비 보정 + 렌더 배율 5.0 + 최소 신뢰도 0)를 " +
                "적용하고 이 PDF를 다시 OCR합니다.",
                "미검출 진단", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (choice == MessageBoxResult.Cancel) return;
            if (choice == MessageBoxResult.No)
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
            StatusMessage?.Invoke("교정이 등록되었습니다 — 이후 검사부터 자동 적용됩니다.");
        }
    }

    /// <summary>현재 페이지의 OCR 인식 로그 창 열기.</summary>
    private void OnShowOcrLog(object sender, RoutedEventArgs e) => OpenOcrLog("", "");

    private void OpenOcrLog(string field, string expectedTerm)
    {
        if (!_analyses.TryGetValue(_currentPage, out var analysis)
            || _displayed is null)
        {
            MessageBox.Show("이 페이지의 OCR이 아직 끝나지 않았습니다.", "OCR 로그");
            return;
        }
        var log = new OcrLogWindow(analysis.Words,
                                   (_displayed.Width, _displayed.Height),
                                   field, expectedTerm, _corrections)
        { Owner = Window.GetWindow(this) };
        if (log.ShowDialog() == true)
        {
            ReloadEngineAndReinspect();
            StatusMessage?.Invoke("교정이 등록되었습니다 — 이후 검사부터 자동 적용됩니다.");
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
        StatusMessage?.Invoke("인식 강화 파라미터 적용 — 페이지를 다시 OCR합니다.");
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

    private string SaveDir()
    {
        var configured = _config.GetString("save_directory");
        return configured.Length > 0 ? configured
            : Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile), "LaVIS_결과");
    }

    private void OnPickSaveDir(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "결과 저장 폴더" };
        if (dialog.ShowDialog() != true) return;
        _config.Settings["save_directory"] = dialog.FolderName;
        _config.SaveSettings();
        StatusMessage?.Invoke($"저장 경로: {dialog.FolderName}");
    }

    private void OnSaveCurrent(object sender, RoutedEventArgs e)
    {
        if (!_pdf.IsOpen || !_outcomes.TryGetValue(_currentPage, out var outcome))
        {
            MessageBox.Show("저장할 검사 결과가 없습니다.", "저장",
                            MessageBoxButton.OK, MessageBoxImage.Information);
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
        var path = Path.Combine(SaveDir(), filename);
        try
        {
            Annotate.SaveAnnotatedJpeg(image, outcome, _standards.FieldColors, path,
                                       _config.GetDouble("save_scale", 0.5),
                                       _config.GetInt("jpeg_quality", 90),
                                       CurrentOverlayStyle());
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "저장 실패",
                            MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        _history?.RecordInspection(outcome, path, "pdf", _pdf.Path, page);
        StatusMessage?.Invoke($"저장됨: {filename}");
        if (notify)
            MessageBox.Show($"저장됨: {filename}", "저장 완료",
                            MessageBoxButton.OK, MessageBoxImage.Information);
    }
}

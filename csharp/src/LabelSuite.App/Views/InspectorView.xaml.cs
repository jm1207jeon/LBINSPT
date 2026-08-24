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
    private const string PreprocessSig = "none-v1";   // PDF 렌더는 축 정렬 — 스큐 보정 불필요

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
        FormRow.Visibility = _formRules.Count > 0 ? Visibility.Visible
                                                  : Visibility.Collapsed;
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
                        && exp.AsValue().TryGetValue<int>(out var count) ? count : null));
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
        var image = _pdf.RenderPage(_currentPage);   // PdfDoc 캐시 소유 — 해제 금지
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

    public void ApplyConfig()
    {
        _standards = StandardsBundle.Load(_config);
        _engine = BuildEngine();
        _textract = MakeTextract();
        _sameValueRules = LoadSameValueRules();
        _formRules = LoadFormRules();
        FormRow.Visibility = _formRules.Count > 0 ? Visibility.Visible
                                                  : Visibility.Collapsed;
        _profiler.MinSamples = _config.SectionInt("type_learning", "min_samples", 5);
        _pdf.RenderZoom = _config.GetDouble("pdf_render_zoom", 4.0);
        PopulateStandardButtons();
        _ = CheckAwsAsync();
        ReinspectCurrent();
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
        _typeLearnedPages.Clear();
        _typeAlarmPages.Clear();
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
            _worker.Submit(page, key, () => _pdf.RenderPage(pageCopy),
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
    }

    private void OnFirstPage(object s, RoutedEventArgs e) => Navigate(0);
    private void OnPrevPage(object s, RoutedEventArgs e) => Navigate(_currentPage - 1);
    private void OnNextPage(object s, RoutedEventArgs e) => Navigate(_currentPage + 1);
    private void OnLastPage(object s, RoutedEventArgs e) => Navigate(_pdf.PageCount - 1);

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!_pdf.IsOpen) return;
        switch (e.Key)
        {
            case Key.Left: Navigate(_currentPage - 1); e.Handled = true; break;
            case Key.Right: Navigate(_currentPage + 1); e.Handled = true; break;
            case Key.Home: Navigate(0); e.Handled = true; break;
            case Key.End: Navigate(_pdf.PageCount - 1); e.Handled = true; break;
        }
    }

    private void ShowPage(int page, bool fit = false)
    {
        PageLabel.Text = $"{page + 1} / {_pdf.PageCount}";
        var image = _pdf.RenderPage(page);

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
                           () => _pdf.RenderPage(pageCopy), priority: 0);
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
            RunInspection(page, analysis, _pdf.RenderPage(page));
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
    private sealed record BarcodeRowVm(string Source, string Field, string Value,
                                       string Grade, string State);
    private sealed record PageSlotVm(int PageIndex, string Number, string Tip,
                                     Brush Fill, Brush Stroke, Thickness StrokeThickness,
                                     Brush TextBrush);

    private void RunInspection(int page, PageAnalysis analysis, SKBitmap image,
                               bool fit = false)
    {
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
                    if (_records[index].Standard is { } std) SelectStandard(std);
                }
                LotMatchLabel.Text = match.MatchType switch
                {
                    "exact" => "자동(정확)", "suffix_unique" => "자동(끝4자리)",
                    _ => "자동(유사)",
                };
            }
            // 이 페이지에서 LOT 후보를 못 찾으면 현재 선택 유지
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
                if (form.Standard.Length > 0
                    && _standards.Standards.ContainsKey(form.Standard))
                    SelectStandard(form.Standard);
            }
            else
            {
                FormMatchLabel.Text = "미감지";
                FormMatchLabel.Foreground = (Brush)FindResource("MutedBrush");
            }
        }

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
                                      barcodeChecks, SearchBox.Text);
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
        // 검출 바코드별 손상 등급 매핑 (같은 심볼로지 첫 검출의 등급 표시)
        string GradeFor(string source) =>
            _lastAnalysisShown?.Barcodes
                .FirstOrDefault(b => b.Symbology == source)?.Grade ?? "";
        BarcodeGrid.ItemsSource = outcome.BarcodeChecks.Count > 0
            ? outcome.BarcodeChecks.Select(c => new BarcodeRowVm(
                c.Source, c.Field,
                c.Matched ? c.BarcodeValue : $"{c.BarcodeValue} (기대: {c.ExpectedValue})",
                GradeFor(c.Source),
                c.Matched ? "일치" : "불일치")).ToList()
            : [new BarcodeRowVm("", "", "검출된 GS1 바코드 없음", "", "")];
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
            RunInspection(_currentPage, analysis, _pdf.RenderPage(_currentPage));
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

    private void OnViewerWheel(object sender, MouseWheelEventArgs e)
    {
        if (_displayed is null) return;
        var oldZoom = _zoom;
        _zoom = Math.Clamp(_zoom + (e.Delta > 0 ? 0.3 : -0.3), 0.1, 5.0);
        if (Math.Abs(_zoom - oldZoom) < 1e-6) { e.Handled = true; return; }
        var mouse = e.GetPosition(ViewerScroll);
        var ratio = _zoom / oldZoom;
        ApplyZoom();
        ViewerScroll.ScrollToHorizontalOffset(
            (ViewerScroll.HorizontalOffset + mouse.X) * ratio - mouse.X);
        ViewerScroll.ScrollToVerticalOffset(
            (ViewerScroll.VerticalOffset + mouse.Y) * ratio - mouse.Y);
        e.Handled = true;
    }

    private void OnViewerMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && _displayed is not null)
        {
            _panning = true;
            _panStart = e.GetPosition(ViewerScroll);
            ViewerScroll.Cursor = Cursors.Hand;
        }
    }

    private void OnViewerMouseMove(object sender, MouseEventArgs e)
    {
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
        _panning = false;
        ViewerScroll.Cursor = Cursors.Arrow;
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

        var dialog = new CorrectionDialog(row.Field, term, candidates, _corrections)
        { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
        {
            ReloadEngineAndReinspect();
            StatusMessage?.Invoke("교정이 등록되었습니다 — 이후 검사부터 자동 적용됩니다.");
        }
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
        SaveOutcome(_currentPage, outcome, _pdf.RenderPage(_currentPage), notify: true);
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

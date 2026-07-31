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
    private bool _lotAutoSelected;
    private bool _suppressEvents;
    private double _zoom = 1.0;
    private SKBitmap? _displayed;
    private Point _panStart;
    private bool _panning;

    public event Action<string>? StatusMessage;
    public event Action<bool, string>? AwsStatusChanged;

    public InspectorView() => InitializeComponent();

    public void Initialize(AppConfig config, HistoryDb history)
    {
        _config = config;
        _history = history;
        _standards = StandardsBundle.Load(config);
        _engine = new InspectionEngine(_standards);
        _textract = MakeTextract();
        _cache = new OcrCache(Path.Combine(AppConfig.DataDir(), "ocr_cache"),
                              _config.GetInt("ocr_cache_max_entries", 500));
        _pdf.RenderZoom = _config.GetDouble("pdf_render_zoom", 4.0);
        _pdf.CachePages = _config.GetInt("page_image_cache_pages", 6);
        AutoSaveCheck.IsChecked = _config.GetBool("auto_save_default", false);

        _worker = new PrefetchWorker(async image =>
        {
            var words = await _textract.DetectWordsAsync(image);
            var barcodes = BarcodeDetector.Detect(image);
            return new PageAnalysis { Words = words, Barcodes = barcodes };
        });
        _worker.PageDone += (gen, page, key, analysis) =>
            Dispatcher.Invoke(() => OnPageDone(gen, page, key, analysis));
        _worker.PageFailed += (gen, page, message) =>
            Dispatcher.Invoke(() => OnPageFailed(gen, page, message));

        PopulateStandardCombo();
        _ = CheckAwsAsync();
    }

    private TextractClient MakeTextract()
    {
        var aws = _config.Settings["aws"]?.AsObject();
        return new TextractClient(
            aws?["region"]?.GetValue<string>() ?? "ap-northeast-2",
            aws?["profile"]?.GetValue<string>() is { Length: > 0 } profile ? profile : null);
    }

    public void ApplyConfig()
    {
        _standards = StandardsBundle.Load(_config);
        _engine = new InspectionEngine(_standards);
        _textract = MakeTextract();
        _pdf.RenderZoom = _config.GetDouble("pdf_render_zoom", 4.0);
        PopulateStandardCombo();
        _ = CheckAwsAsync();
    }

    public void Shutdown()
    {
        _worker.Dispose();
        _pdf.Dispose();
    }

    private async Task CheckAwsAsync()
    {
        var status = await _textract.ValidateCredentialsAsync();
        AwsStatusChanged?.Invoke(status.Ok,
            status.Ok ? "AWS 인증 확인됨" : $"AWS 인증 실패: {status.Error}");
        if (!status.Ok)
            StatusMessage?.Invoke("AWS 인증 실패 — OCR 실행 전에 설정에서 자격증명을 확인하세요.");
    }

    private void PopulateStandardCombo()
    {
        _suppressEvents = true;
        var current = StandardCombo.SelectedItem as string;
        StandardCombo.ItemsSource = _standards.Standards.Keys.ToList();
        StandardCombo.SelectedItem =
            current is not null && _standards.Standards.ContainsKey(current)
                ? current : _standards.Standards.Keys.FirstOrDefault();
        _suppressEvents = false;
    }

    // ---------------- 목록 ----------------

    public void LoadRecords(List<LabelRecord> records)
    {
        _records = records;
        _lotAutoSelected = false;
        _suppressEvents = true;
        var items = new List<string> { "LOT 선택…" };
        items.AddRange(records.Select(r => r.Lot));
        LotCombo.ItemsSource = items;
        LotCombo.SelectedIndex = 0;
        _suppressEvents = false;
        ListStatus.Text = $"{records.Count}건 로드됨";
        ListStatus.Foreground = (Brush)FindResource("SuccessBrush");
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
        var record = CurrentRecord();
        if (record?.Standard is { } standard && _standards.Standards.ContainsKey(standard))
        {
            _suppressEvents = true;
            StandardCombo.SelectedItem = standard;
            _suppressEvents = false;
        }
        ReinspectCurrent();
    }

    private void OnStandardChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressEvents) ReinspectCurrent();
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
        _lotAutoSelected = false;
        _worker.NewGeneration();
        foreach (var button in new[] { FirstButton, PrevButton, NextButton, LastButton })
            button.IsEnabled = true;
        PdfNameLabel.Text = $"{Path.GetFileName(path)} · {_pdf.PageCount}페이지";
        StatusMessage?.Invoke($"PDF 로드: {Path.GetFileName(path)} ({_pdf.PageCount}페이지)");
        ShowPage(0, fit: true);
        SubmitPrefetchJobs();
    }

    private string CacheKeyForPage(int page) =>
        OcrCache.PageKey(_pdf.Path ?? "", _pdf.Mtime, page, _pdf.RenderZoom, PreprocessSig);

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
    private sealed record BarcodeRowVm(string Source, string Field, string Value, string State);

    private void RunInspection(int page, PageAnalysis analysis, SKBitmap image,
                               bool fit = false)
    {
        if (!_lotAutoSelected && _records.Count > 0 && LotCombo.SelectedIndex <= 0)
        {
            var match = _engine.MatchLot(analysis.Words, _records);
            if (match is not null)
            {
                _lotAutoSelected = true;
                var index = _records.FindIndex(r => r.Lot == match.Lot);
                if (index >= 0)
                {
                    _suppressEvents = true;
                    LotCombo.SelectedIndex = index + 1;
                    var record0 = _records[index];
                    if (record0.Standard is { } std && _standards.Standards.ContainsKey(std))
                        StandardCombo.SelectedItem = std;
                    _suppressEvents = false;
                }
                LotMatchLabel.Text = match.MatchType switch
                {
                    "exact" => "자동(정확)", "suffix_unique" => "자동(끝4자리)",
                    _ => "자동(유사)",
                };
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

        var standardName = StandardCombo.SelectedItem as string
            ?? _standards.Standards.Keys.First();
        var barcodeChecks = BarcodeDetector.CrossCheckHits(analysis.Barcodes, record);
        var outcome = _engine.Inspect(record, standardName, analysis.Words,
                                      barcodeChecks, SearchBox.Text);
        _outcomes[page] = outcome;
        ShowOutcome(outcome);
        using var annotated = Annotate.RenderOverlays(
            image, outcome.AllMatches, _standards.FieldColors);
        SetViewerImage(annotated, fit);
        if (AutoSaveCheck.IsChecked == true)
            SaveOutcome(page, outcome, image, notify: false);
    }

    private void ShowOutcome(InspectionOutcome outcome)
    {
        if (outcome.Passed)
        {
            StatusBadgeText.Text = $"✓ 합격 (PASSED) · 규격 {outcome.Standard.Name}";
            StatusBadgeText.Foreground = (Brush)FindResource("SuccessBrush");
            StatusBadge.Background = (Brush)FindResource("SuccessBgBrush");
        }
        else
        {
            StatusBadgeText.Text = $"⚠ 확인 필요 (CHECK) · 규격 {outcome.Standard.Name}";
            StatusBadgeText.Foreground = (Brush)FindResource("WarnBrush");
            StatusBadge.Background = (Brush)FindResource("WarnBgBrush");
        }
        FieldGrid.ItemsSource = outcome.Fields.Values.Select(f => new FieldRowVm(
            f.Field, f.Term.Length > 0 ? f.Term : "-",
            f.Expected is { } expected ? $"{f.Found}/{expected}" : f.Found.ToString(),
            f.Expected is null ? "info" : f.Passed ? "pass" : "fail")).ToList();
        BarcodeGrid.ItemsSource = outcome.BarcodeChecks.Count > 0
            ? outcome.BarcodeChecks.Select(c => new BarcodeRowVm(
                c.Source, c.Field,
                c.Matched ? c.BarcodeValue : $"{c.BarcodeValue} (기대: {c.ExpectedValue})",
                c.Matched ? "일치" : "불일치")).ToList()
            : [new BarcodeRowVm("", "", "검출된 GS1 바코드 없음", "")];
    }

    private void ClearResultPanel(string message)
    {
        StatusBadgeText.Text = message;
        StatusBadgeText.Foreground = (Brush)FindResource("MutedBrush");
        StatusBadge.Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xF1, 0xF5));
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

    // ---------------- 저장 ----------------

    private string SaveDir()
    {
        var configured = _config.GetString("save_directory");
        return configured.Length > 0 ? configured
            : Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile), "LabelSuite_결과");
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
                                       _config.GetInt("jpeg_quality", 90));
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

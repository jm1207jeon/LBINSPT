// 목록 생성 탭 — 파이썬 generator_page.py 포팅.
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClosedXML.Excel;
using LabelSuite.Core;
using Microsoft.Win32;

namespace LabelSuite.App.Views;

public partial class GeneratorView : UserControl
{
    private AppConfig _config = null!;
    private ColumnMaps _maps = null!;
    private readonly Dictionary<string, InputFrame?> _frames = new()
    { ["schedule"] = null, ["product"] = null, ["bsc"] = null };
    private GenerationResult? _result;

    public event Action<string>? StatusMessage;
    public event Action<List<LabelRecord>>? ListGenerated;

    public GeneratorView() => InitializeComponent();

    public void Initialize(AppConfig config)
    {
        _config = config;
        _maps = ColumnMaps.FromConfig(config.ColumnMapsRaw);
        RestoreLastFiles();
    }

    public void ApplyConfig() => _maps = ColumnMaps.FromConfig(_config.ColumnMapsRaw);

    private TextBlock StatusFor(string key) => key switch
    {
        "schedule" => ScheduleStatus, "product" => ProductStatus, _ => BscStatus,
    };

    private static readonly Dictionary<string, string> FileLabels = new()
    {
        ["schedule"] = "주문일정 체크리스트",
        ["product"] = "제품 품목번호 리스트",
        ["bsc"] = "BSC FGD 리스트",
    };

    // ---------- 파일 로드 ----------

    private void RestoreLastFiles()
    {
        if (_config.Settings["last_files"] is not System.Text.Json.Nodes.JsonObject lastFiles)
            return;
        foreach (var (key, node) in lastFiles)
        {
            var path = node?.GetValue<string>() ?? "";
            if (path.Length > 0 && File.Exists(path)) LoadFile(key, path, silent: true);
            else if (path.Length > 0)
            {
                StatusFor(key).Text = $"이전 경로 없음: {Path.GetFileName(path)}";
                StatusFor(key).Foreground = (Brush)FindResource("WarnBrush");
            }
        }
    }

    private void PickFile(string key)
    {
        var dialog = new OpenFileDialog
        { Title = $"{FileLabels[key]} 선택", Filter = "Excel 파일|*.xlsx" };
        if (dialog.ShowDialog() == true) LoadFile(key, dialog.FileName, silent: false);
    }

    private void OnPickSchedule(object s, RoutedEventArgs e) => PickFile("schedule");
    private void OnPickProduct(object s, RoutedEventArgs e) => PickFile("product");
    private void OnPickBsc(object s, RoutedEventArgs e) => PickFile("bsc");

    private async void LoadFile(string key, string path, bool silent)
    {
        var status = StatusFor(key);
        status.Text = "읽는 중…";
        status.Foreground = (Brush)FindResource("AccentBrush");
        var spec = key switch
        { "schedule" => _maps.Schedule, "product" => _maps.Product, _ => _maps.Bsc };
        try
        {
            var frame = await Task.Run(() => InputFrame.Load(path, spec));
            _frames[key] = frame;
            status.Text = "✓ " + Path.GetFileName(path);
            status.Foreground = (Brush)FindResource("SuccessBrush");
            if (_config.Settings["last_files"] is System.Text.Json.Nodes.JsonObject lastFiles)
            {
                lastFiles[key] = path;
                _config.SaveSettings();
            }
            if (key == "schedule")
                PopulateDateTree(ListGenerator.ExtractAvailableDates(frame, _maps));
            UpdateButtons();
            if (!silent) StatusMessage?.Invoke($"{FileLabels[key]} 로드 완료: {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            _frames[key] = null;
            status.Text = "로드 실패";
            status.Foreground = (Brush)FindResource("DangerBrush");
            UpdateButtons();
            if (!silent)
                MessageBox.Show($"{FileLabels[key]}\n{ex.Message}", "파일 오류",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------- 드래그앤드롭 (시트명으로 자동 분류) ----------

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        foreach (var path in paths.Where(p => p.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)))
        {
            var key = IdentifyFileKey(path);
            if (key is null)
                MessageBox.Show(
                    $"{Path.GetFileName(path)}\n필요한 시트를 찾지 못했습니다. 버튼으로 직접 선택해 주세요.",
                    "파일 판별 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
            else LoadFile(key, path, silent: false);
        }
    }

    private string? IdentifyFileKey(string path)
    {
        HashSet<string> sheets = [];
        try
        {
            using var workbook = new XLWorkbook(path);
            sheets = workbook.Worksheets.Select(w => w.Name).ToHashSet();
        }
        catch (Exception) { }
        if (sheets.Contains(_maps.Schedule.Sheet)) return "schedule";
        if (sheets.Contains(_maps.Product.Sheet)) return "product";
        if (sheets.Contains(_maps.Bsc.Sheet)) return "bsc";
        var name = Path.GetFileName(path);
        if (name.Contains("주문일정") || name.Contains("일정")) return "schedule";
        if (name.Contains("품목")) return "product";
        if (name.Contains("BSC", StringComparison.OrdinalIgnoreCase)
            || name.Contains("FGD", StringComparison.OrdinalIgnoreCase)) return "bsc";
        return null;
    }

    // ---------- 날짜 트리 (연>월>일 3상태 체크박스) ----------

    private void PopulateDateTree(List<DateOnly> dates)
    {
        DateTree.Items.Clear();   // 기존 선택 초기화 (레거시 잔존 선택 버그 수정)
        foreach (var yearGroup in dates.GroupBy(d => d.Year).OrderBy(g => g.Key))
        {
            var yearItem = MakeCheckItem($"{yearGroup.Key}년", null);
            foreach (var monthGroup in yearGroup.GroupBy(d => d.Month).OrderBy(g => g.Key))
            {
                var monthItem = MakeCheckItem($"{monthGroup.Key}월", null);
                foreach (var date in monthGroup.OrderBy(d => d))
                    monthItem.Items.Add(MakeCheckItem(
                        $"{date.Day}일 ({date:yyyy-MM-dd})", date));
                yearItem.Items.Add(monthItem);
            }
            yearItem.IsExpanded = true;
            DateTree.Items.Add(yearItem);
        }
        UpdateButtons();
    }

    private TreeViewItem MakeCheckItem(string text, DateOnly? date)
    {
        var checkBox = new CheckBox { Content = text, Tag = date };
        checkBox.Checked += (_, _) => OnTreeCheckChanged(checkBox, true);
        checkBox.Unchecked += (_, _) => OnTreeCheckChanged(checkBox, false);
        return new TreeViewItem { Header = checkBox };
    }

    private bool _propagating;

    private void OnTreeCheckChanged(CheckBox source, bool isChecked)
    {
        if (_propagating) { UpdateButtons(); return; }
        _propagating = true;
        try
        {
            // 부모 체크 → 하위 전파 (연/월 일괄 선택)
            if (source.Parent is TreeViewItem item)
                foreach (var child in Descendants(item))
                    if (child.Header is CheckBox childBox) childBox.IsChecked = isChecked;
        }
        finally { _propagating = false; }
        UpdateButtons();
    }

    private static IEnumerable<TreeViewItem> Descendants(TreeViewItem item)
    {
        foreach (var child in item.Items.OfType<TreeViewItem>())
        {
            yield return child;
            foreach (var grandChild in Descendants(child)) yield return grandChild;
        }
    }

    private HashSet<DateOnly> CheckedDates()
    {
        var dates = new HashSet<DateOnly>();
        foreach (var yearItem in DateTree.Items.OfType<TreeViewItem>())
            foreach (var item in Descendants(yearItem))
                if (item.Header is CheckBox { IsChecked: true, Tag: DateOnly date })
                    dates.Add(date);
        return dates;
    }

    private void UpdateButtons() =>
        GenerateButton.IsEnabled = _frames["schedule"] is not null && CheckedDates().Count > 0;

    // ---------- 생성/저장/핸드오프 ----------

    private async void OnGenerate(object sender, RoutedEventArgs e)
    {
        var selected = CheckedDates();
        if (_frames["product"] is null && _frames["bsc"] is null)
        {
            var answer = MessageBox.Show(
                "품목번호/BSC 리스트가 없어 GTIN·REF를 조회할 수 없습니다.\n그래도 생성할까요?",
                "확인", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
        }
        GenerateButton.IsEnabled = false;
        StatusMessage?.Invoke("리스트 생성 중…");
        var schedule = _frames["schedule"]!;
        var product = _frames["product"];
        var bsc = _frames["bsc"];
        var countryMap = _config.CountryStandardMap();
        var shelfLife = _config.GetInt("shelf_life_months", 36);
        try
        {
            _result = await Task.Run(() => ListGenerator.Generate(
                schedule, product, bsc, selected, _maps, countryMap, shelfLife));
        }
        catch (Exception ex)
        {
            UpdateButtons();
            MessageBox.Show($"리스트 생성 중 오류: {ex.Message}", "오류",
                            MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        PreviewGrid.ItemsSource = _result.Records;
        IssuesBox.Text = _result.Issues.Count > 0
            ? string.Join("\n", _result.Issues.Select(
                i => $"[{i.Severity}] {i.RowIndex}행 {i.Lot}: {i.Message}"))
            : "이슈 없음";
        SaveButton.IsEnabled = _result.Records.Count > 0;
        SendButton.IsEnabled = _result.Records.Count > 0;
        UpdateButtons();
        var summary = $"{_result.Records.Count}건 생성 " +
                      $"(경고 {_result.WarningCount}, 오류 {_result.ErrorCount})";
        StatusMessage?.Invoke(summary);
        if (_result.WarningCount + _result.ErrorCount > 0)
            MessageBox.Show(summary + "\n자세한 내용은 경고/오류 패널을 확인하세요.",
                            "생성 완료", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnSaveXlsx(object sender, RoutedEventArgs e)
    {
        if (_result is null || _result.Records.Count == 0) return;
        var tag = string.Join(",", _result.SelectedDates.Take(3)
            .Select(d => d.ToString("yyMMdd")));
        var dialog = new SaveFileDialog
        {
            Title = "검사 목록 저장",
            Filter = "Excel 파일|*.xlsx",
            FileName = $"Label Inspection List_{tag}.xlsx",
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            Schema.SaveInspectionList(_result.Records, dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "저장 실패",
                            MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        _config.Settings["last_list_path"] = dialog.FileName;
        _config.SaveSettings();
        StatusMessage?.Invoke($"저장 완료: {dialog.FileName}");
    }

    private void OnSendToInspector(object sender, RoutedEventArgs e)
    {
        if (_result is { Records.Count: > 0 })
            ListGenerated?.Invoke(_result.Records.ToList());
    }
}

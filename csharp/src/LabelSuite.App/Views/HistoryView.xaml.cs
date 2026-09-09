// 검사 이력 탭 — 파이썬 history_page.py 포팅.
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LabelSuite.Core;
using Microsoft.Win32;

namespace LabelSuite.App.Views;

public partial class HistoryView : UserControl
{
    public event Action<string, StatusLevel>? StatusMessage;

    private void Status(string message, StatusLevel level = StatusLevel.Info) =>
        StatusMessage?.Invoke(message, level);

    private AppConfig _config = null!;
    private HistoryDb? _db;

    public HistoryView() => InitializeComponent();

    public void Initialize(AppConfig config, HistoryDb db)
    {
        _config = config;
        _db = db;
        Refresh();
    }

    private sealed record RowVm(long Id, string Ts, string Lot, string Ref, string Pn,
                                string Standard, string Page, string Verdict,
                                string ImagePath);

    /// <summary>지금까지 표시한 이력 id — 새로 저장돼 처음 나타나는 행만 번쩍이기 위해 유지.</summary>
    private readonly HashSet<long> _knownIds = [];
    private bool _loadedOnce;

    public void Refresh()
    {
        if (_db is null) return;
        bool? passed = (VerdictFilter.SelectedIndex) switch
        { 1 => true, 2 => false, _ => null };
        var lot = LotFilter.Text.Trim();
        var rows = _db.Query(lot: lot.Length > 0 ? lot : null, passed: passed);
        var vms = rows.Select(r => new RowVm(
            r.Id, r.Ts.Replace('T', ' '), r.Lot, r.Ref, r.Pn, r.Standard,
            r.Page is { } page ? (page + 1).ToString() : "",
            r.InspectorVerdict == "PASS" ? "합격(검사자)" : r.InspectorVerdict == "FAIL" ? "부적합(검사자)"
                : r.Passed ? "합격" : "확인 필요", r.ImagePath)).ToList();
        Table.ItemsSource = vms;
        // 첫 로드는 전부 '새 행'이므로 제외 — 이후 검사에서 저장된 행만 앰버로 650ms
        var fresh = _loadedOnce ? vms.Where(v => !_knownIds.Contains(v.Id)).ToList() : new List<RowVm>();
        foreach (var vm in vms) _knownIds.Add(vm.Id);
        _loadedOnce = true;
        foreach (var vm in fresh) UiFx.FlashRow(Table, vm);
    }

    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Refresh();
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) Refresh();
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Refresh();

    private void OnOpenMasterDb(object sender, RoutedEventArgs e)
    {
        if (_db is null) return;
        new MasterDbWindow(_db) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Table.SelectedItem is not RowVm row) return;
        if (row.ImagePath.Length == 0 || !File.Exists(row.ImagePath))
        {
            Dialogs.Info(this, "저장된 이미지 파일을 찾을 수 없습니다.", "이미지 없음");
            return;
        }
        Process.Start(new ProcessStartInfo(row.ImagePath) { UseShellExecute = true });
    }

    private void OnExportReport(object sender, RoutedEventArgs e)
    {
        if (_db is null) return;
        var lots = _db.Lots();
        if (lots.Count == 0)
        {
            Dialogs.Info(this, "저장된 검사 이력이 없습니다.", "리포트");
            return;
        }
        var lot = LotFilter.Text.Trim();
        if (!lots.Contains(lot)) lot = lots.Count == 1 ? lots[0] : "";
        if (lot.Length == 0)
        {
            var preview = string.Join(", ", lots.Take(10));
            Dialogs.Info(this, 
                $"LOT 필터에 리포트를 만들 LOT을 입력하세요.\n보유 LOT: {preview}" +
                (lots.Count > 10 ? " …" : ""),
                "리포트");
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "리포트 저장", Filter = "Excel 파일|*.xlsx",
            FileName = $"검사리포트_{lot}.xlsx",
        };
        if (dialog.ShowDialog() != true) return;
        // 파일 잠김(엑셀에서 열림)·권한·디스크 부족은 상태바 오류로 보고 — 이력은 그대로 유지
        var db = _db;
        var count = 0;
        ExportGuard.Run($"LOT {lot} 검사 리포트", dialog.FileName,
                        () => count = Report.ExportLotReport(db, lot, dialog.FileName),
                        (message, level) => Status(
                            level == StatusLevel.Info ? $"LOT {lot} 검사 {count}건 리포트 저장 완료: {dialog.FileName}" : message,
                            level));
    }
}

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
    private AppConfig _config = null!;
    private HistoryDb? _db;

    public HistoryView() => InitializeComponent();

    public void Initialize(AppConfig config, HistoryDb db)
    {
        _config = config;
        _db = db;
        Refresh();
    }

    private sealed record RowVm(string Ts, string Lot, string Ref, string Pn,
                                string Standard, string Page, string Verdict,
                                string ImagePath);

    public void Refresh()
    {
        if (_db is null) return;
        bool? passed = (VerdictFilter.SelectedIndex) switch
        { 1 => true, 2 => false, _ => null };
        var lot = LotFilter.Text.Trim();
        var rows = _db.Query(lot: lot.Length > 0 ? lot : null, passed: passed);
        Table.ItemsSource = rows.Select(r => new RowVm(
            r.Ts.Replace('T', ' '), r.Lot, r.Ref, r.Pn, r.Standard,
            r.Page is { } page ? (page + 1).ToString() : "",
            r.Passed ? "합격" : "확인 필요", r.ImagePath)).ToList();
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

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Table.SelectedItem is not RowVm row) return;
        if (row.ImagePath.Length == 0 || !File.Exists(row.ImagePath))
        {
            MessageBox.Show("저장된 이미지 파일을 찾을 수 없습니다.", "이미지 없음",
                            MessageBoxButton.OK, MessageBoxImage.Information);
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
            MessageBox.Show("저장된 검사 이력이 없습니다.", "리포트",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var lot = LotFilter.Text.Trim();
        if (!lots.Contains(lot)) lot = lots.Count == 1 ? lots[0] : "";
        if (lot.Length == 0)
        {
            var preview = string.Join(", ", lots.Take(10));
            MessageBox.Show(
                $"LOT 필터에 리포트를 만들 LOT을 입력하세요.\n보유 LOT: {preview}" +
                (lots.Count > 10 ? " …" : ""),
                "리포트", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "리포트 저장", Filter = "Excel 파일|*.xlsx",
            FileName = $"검사리포트_{lot}.xlsx",
        };
        if (dialog.ShowDialog() != true) return;
        var count = Report.ExportLotReport(_db, lot, dialog.FileName);
        MessageBox.Show($"LOT {lot} 검사 {count}건을 내보냈습니다.\n{dialog.FileName}",
                        "리포트 완료", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}

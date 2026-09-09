// 검사 결과 목록(비모달) — 자동 검사 뒤 합격/부적합 로트를 나눠 보고, 부적합 행을 클릭해 해당 페이지로 이동,
// 문제를 해결한 뒤 검사자 확인 합격 처리를 한다. 데이터·동작은 InspectorView가 콜백으로 제공한다.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LabelSuite.App.Views;

namespace LabelSuite.App;

public partial class ResultListWindow : Window
{
    private readonly Func<IReadOnlyList<InspectorView.ResultRow>> _rows;
    private readonly Action<int> _navigate;
    private readonly Action<int> _overridePass;
    private readonly Action<int> _clearOverride;

    private sealed record RowVm(int Page, string PageDisplay, string Lot, string Ref, string Standard,
                                string Auto, string Final, string Reason, string SavedMark, string Tone,
                                bool IsFail, bool HasOverride, bool Inspected);

    public ResultListWindow(Func<IReadOnlyList<InspectorView.ResultRow>> rows, Action<int> navigate,
                            Action<int> overridePass, Action<int> clearOverride)
    {
        _rows = rows;
        _navigate = navigate;
        _overridePass = overridePass;
        _clearOverride = clearOverride;
        InitializeComponent();
        Refresh();
    }

    public void SetFailOnly(bool failOnly) => FailOnlyCheck.IsChecked = failOnly;

    /// <summary>InspectorView.ResultsChanged 때마다 호출 — 선택 페이지를 유지한 채 다시 채운다.</summary>
    public void Refresh()
    {
        var selectedPage = (Grid.SelectedItem as RowVm)?.Page;
        var all = _rows();
        var failOnly = FailOnlyCheck.IsChecked == true;
        var vms = all
            .Where(r => !failOnly || r.IsFail)
            .Select(r => new RowVm(r.Page, (r.Page + 1).ToString(), r.Lot, r.Ref, r.Standard, r.Auto, r.Final,
                                   r.Reason, r.Saved ? "✓" : "", 
                                   !r.Inspected ? "" : r.HasOverride ? "override" : r.IsFail ? "fail" : "pass",
                                   r.IsFail, r.HasOverride, r.Inspected))
            .ToList();
        Grid.ItemsSource = vms;
        var inspected = all.Count(r => r.Inspected);
        var fail = all.Count(r => r.Inspected && r.IsFail);
        var overridden = all.Count(r => r.HasOverride);
        var uninspected = all.Count(r => !r.Inspected);
        SummaryText.Text = $"부적합 {fail} · 합격 {inspected - fail} (검사자 확인 {overridden}) · 미검사 {uninspected} / 전체 {all.Count}페이지";
        if (selectedPage is { } page && vms.FirstOrDefault(v => v.Page == page) is { } keep)
            Grid.SelectedItem = keep;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var row = Grid.SelectedItem as RowVm;
        GoButton.IsEnabled = row is not null;
        OverrideButton.IsEnabled = row is { Inspected: true, IsFail: true };
        ClearOverrideButton.IsEnabled = row is { HasOverride: true };
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) Refresh();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Grid.SelectedItem is RowVm row) _navigate(row.Page);
    }

    private void OnGo(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is RowVm row) _navigate(row.Page);
    }

    private void OnOverride(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not RowVm row) return;
        _navigate(row.Page);   // 처리 전에 해당 페이지를 화면에 띄워 검사자가 보고 결정하게
        _overridePass(row.Page);
        Refresh();
    }

    private void OnClearOverride(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not RowVm row) return;
        _clearOverride(row.Page);
        Refresh();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}

// OCR 인식 로그 — 페이지의 모든 OCR 단어를 위→아래 순으로 나열해
// "그 구간이 실제로 어떻게 읽혔는지" 확인하고, 잘못 읽힌 단어를 골라
// 바로 오인식 교정을 등록할 수 있다 (미검출 필드 원인 파악용).
using System.Windows;
using System.Windows.Controls;
using LabelSuite.Core;

namespace LabelSuite.App;

public partial class OcrLogWindow : Window
{
    public sealed record RowVm(string Order, string Text, int Confidence,
                               string X, string Y, string Similarity, bool Low,
                               double SimilarityValue);

    private readonly string _field;
    private readonly string _expectedTerm;
    private readonly OcrCorrections _corrections;
    private readonly List<RowVm> _rows;

    public OcrLogWindow(IReadOnlyList<OcrWord> words, (int W, int H) pageSize,
                        string field, string expectedTerm,
                        OcrCorrections corrections)
    {
        InitializeComponent();
        _field = field;
        _expectedTerm = expectedTerm;
        _corrections = corrections;

        HeaderText.Text = expectedTerm.Length > 0
            ? $"필드 {field} — 기대값 '{expectedTerm}' 이(가) 검출되지 않았습니다. " +
              "아래에서 해당 구간이 어떻게 읽혔는지 확인하고, 잘못 읽힌 단어를 " +
              "선택해 교정을 등록하세요."
            : "현재 페이지의 OCR 인식 결과 전체 (위→아래, 왼→오른쪽 순)";
        RightBox.Text = expectedTerm;
        SimilarSortCheck.IsChecked = expectedTerm.Length > 0;
        SimilarSortCheck.Visibility = expectedTerm.Length > 0
            ? Visibility.Visible : Visibility.Collapsed;

        var ordered = words.OrderBy(w => w.Bbox.Y).ThenBy(w => w.Bbox.X).ToList();
        _rows = ordered.Select((w, i) =>
        {
            var similarity = expectedTerm.Length > 0
                ? InspectionEngine.SimilarityScore(w.Text.Trim(), expectedTerm,
                                                   w.Confidence)
                : 0;
            return new RowVm(
                (i + 1).ToString(), w.Text.Trim(), w.Confidence,
                $"{(w.Bbox.X + w.Bbox.W / 2.0) / Math.Max(1, pageSize.W) * 100:F0}",
                $"{(w.Bbox.Y + w.Bbox.H / 2.0) / Math.Max(1, pageSize.H) * 100:F0}",
                expectedTerm.Length > 0 ? similarity.ToString("F0") : "",
                w.Confidence < 70, similarity);
        }).ToList();
        Refresh();
    }

    private void Refresh()
    {
        var filter = FilterBox.Text.Trim();
        IEnumerable<RowVm> rows = _rows;
        if (filter.Length > 0)
            rows = rows.Where(r =>
                r.Text.Contains(filter, StringComparison.OrdinalIgnoreCase));
        if (SimilarSortCheck.IsChecked == true && _expectedTerm.Length > 0)
            rows = rows.OrderByDescending(r => r.SimilarityValue);
        var list = rows.ToList();
        LogGrid.ItemsSource = list;
        CountText.Text = $"{list.Count}/{_rows.Count}개 단어";
        if (list.Count > 0 && _expectedTerm.Length > 0
            && SimilarSortCheck.IsChecked == true)
            LogGrid.SelectedIndex = 0;
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) Refresh();
    }

    private void OnRowSelected(object sender, SelectionChangedEventArgs e) { }

    private void OnRegister(object sender, RoutedEventArgs e)
    {
        if (LogGrid.SelectedItem is not RowVm row)
        {
            MessageBox.Show("교정할 단어(잘못 읽힌 값)를 목록에서 선택하세요.",
                            "교정 등록", MessageBoxButton.OK,
                            MessageBoxImage.Information);
            return;
        }
        var right = RightBox.Text.Trim();
        if (right.Length == 0)
        {
            MessageBox.Show("올바른 값을 입력하세요.", "교정 등록",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (row.Text == right)
        {
            MessageBox.Show("선택한 단어와 올바른 값이 동일합니다.", "교정 등록",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _corrections.Add(row.Text, right,
                         _field.Length > 0 ? _field : null);
        DialogResult = true;
    }

    private void OnClose(object sender, RoutedEventArgs e) => DialogResult = false;
}

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
    private readonly WordMergeRules _merges;
    private readonly List<OcrWord> _words;
    private readonly (int W, int H) _pageSize;
    private List<RowVm> _rows = [];
    private bool _changed;   // 병합/교정이 있었으면 닫을 때 재검사 트리거

    /// <summary>병합/교정 등록이 한 건이라도 있었는지 (호출측 재검사 판단용 —
    /// X 버튼으로 닫아도 유효).</summary>
    public bool Changed => _changed;

    public OcrLogWindow(IReadOnlyList<OcrWord> words, (int W, int H) pageSize,
                        string field, string expectedTerm,
                        OcrCorrections corrections, WordMergeRules merges)
    {
        InitializeComponent();
        _field = field;
        _expectedTerm = expectedTerm;
        _corrections = corrections;
        _merges = merges;
        _words = words.ToList();
        _pageSize = pageSize;

        HeaderText.Text = expectedTerm.Length > 0
            ? $"필드 {field} — 기대값 '{expectedTerm}' 이(가) 검출되지 않았습니다. " +
              "아래에서 해당 구간이 어떻게 읽혔는지 확인하고, 잘못 읽힌 단어를 " +
              "선택해 교정을 등록하세요."
            : "현재 페이지의 OCR 인식 결과 전체 (위→아래, 왼→오른쪽 순)";
        RightBox.Text = expectedTerm;
        SimilarSortCheck.IsChecked = expectedTerm.Length > 0;
        SimilarSortCheck.Visibility = expectedTerm.Length > 0
            ? Visibility.Visible : Visibility.Collapsed;

        BuildRows();
        Refresh();
    }

    /// <summary>병합 규칙을 적용한 상태로 행 목록 구성 — 병합된 문구는
    /// 재오픈/재구성 시에도 묶인 상태로 표시된다.</summary>
    private void BuildRows()
    {
        var effective = _merges.Apply(_words);
        var ordered = effective.OrderBy(w => w.Bbox.Y).ThenBy(w => w.Bbox.X).ToList();
        _rows = ordered.Select((w, i) =>
        {
            var similarity = _expectedTerm.Length > 0
                ? InspectionEngine.SimilarityScore(w.Text.Trim(), _expectedTerm,
                                                   w.Confidence)
                : 0;
            return new RowVm(
                (i + 1).ToString(), w.Text.Trim(), w.Confidence,
                $"{(w.Bbox.X + w.Bbox.W / 2.0) / Math.Max(1, _pageSize.W) * 100:F0}",
                $"{(w.Bbox.Y + w.Bbox.H / 2.0) / Math.Max(1, _pageSize.H) * 100:F0}",
                _expectedTerm.Length > 0 ? similarity.ToString("F0") : "",
                w.Confidence < 70, similarity);
        }).ToList();
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

    /// <summary>선택한 여러 단어를 읽기 순서대로 하나의 문장으로 병합 학습.</summary>
    private void OnLearnMerge(object sender, RoutedEventArgs e)
    {
        var selected = LogGrid.SelectedItems.OfType<RowVm>()
            .OrderBy(r => int.Parse(r.Order)).ToList();
        if (selected.Count < 2)
        {
            Dialogs.Info(this, "Ctrl 클릭으로 병합할 단어를 2개 이상 선택하세요.",
                            "병합 학습");
            return;
        }
        var phrase = string.Join(" ", selected.Select(r => r.Text));
        if (!_merges.Add(selected.Select(r => r.Text)))
        {
            Dialogs.Info(this, "이미 등록된 병합 패턴입니다.", "병합 학습");
            return;
        }
        _changed = true;
        // 창을 유지하고 병합된 상태로 목록 갱신 — 다음 단어를 계속 처리
        BuildRows();
        Refresh();
        CountText.Text += $"  ·  병합됨: \"{phrase}\"";
        // 병합된 행을 선택하고 앰버로 번쩍여 '어디로 합쳐졌는지' 보여 준다
        var merged = LogGrid.Items.OfType<RowVm>().FirstOrDefault(r => r.Text == phrase)
                     ?? LogGrid.Items.OfType<RowVm>().FirstOrDefault(r => r.Text.Contains(phrase));
        if (merged is not null)
        {
            LogGrid.SelectedItem = merged;
            UiFx.FlashRow(LogGrid, merged);
        }
    }

    private void OnRegister(object sender, RoutedEventArgs e)
    {
        if (LogGrid.SelectedItem is not RowVm row)
        {
            Dialogs.Info(this, "교정할 단어(잘못 읽힌 값)를 목록에서 선택하세요.",
                            "교정 등록");
            return;
        }
        var right = RightBox.Text.Trim();
        if (right.Length == 0)
        {
            Dialogs.Info(this, "올바른 값을 입력하세요.", "교정 등록");
            return;
        }
        if (row.Text == right)
        {
            Dialogs.Info(this, "선택한 단어와 올바른 값이 동일합니다.", "교정 등록");
            return;
        }
        _corrections.Add(row.Text, right,
                         _field.Length > 0 ? _field : null);
        _changed = true;
        DialogResult = true;
    }

    private void OnClose(object sender, RoutedEventArgs e) => DialogResult = _changed;
}

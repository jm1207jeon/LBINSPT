using System.Windows;
using System.Windows.Controls;
using LabelSuite.Core;

namespace LabelSuite.App;

public partial class CorrectionDialog : Window
{
    private readonly string _field;
    private readonly OcrCorrections _corrections;

    public CorrectionDialog(string field, string expectedTerm,
                            List<string> candidates, OcrCorrections corrections)
    {
        InitializeComponent();
        _field = field;
        _corrections = corrections;
        HeaderText.Text = $"필드 {field} — 기대값: {expectedTerm}";
        CandidateList.ItemsSource = candidates;
        RightBox.Text = expectedTerm;
        if (candidates.Count > 0) CandidateList.SelectedIndex = 0;
    }

    private void OnCandidateSelected(object sender, SelectionChangedEventArgs e)
    {
        if (CandidateList.SelectedItem is string candidate) WrongBox.Text = candidate;
    }

    private void OnRegister(object sender, RoutedEventArgs e)
    {
        var wrong = WrongBox.Text.Trim();
        var right = RightBox.Text.Trim();
        if (wrong.Length == 0 || right.Length == 0)
        {
            Dialogs.Info(this, "잘못 읽힌 값과 올바른 값을 모두 입력하세요.", "교정");
            return;
        }
        if (wrong == right)
        {
            Dialogs.Info(this, "두 값이 동일합니다.", "교정");
            return;
        }
        _corrections.Add(wrong, right, _field);
        DialogResult = true;
    }

    private void OnClose(object sender, RoutedEventArgs e) => DialogResult = false;
}

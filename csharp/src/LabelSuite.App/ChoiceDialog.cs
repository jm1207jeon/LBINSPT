using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LabelSuite.App;

public enum ChoiceStyle { Default, Primary, Caution }

/// <summary>버튼 라벨이 동작을 그대로 말하는 선택 대화상자 — '예/아니오'로는 무엇이 실행되는지
/// 알 수 없는 분기(학습할지 / 과금 재OCR할지 등)에 쓴다. 마지막에 '취소'(Esc)가 자동으로 붙는다.
/// 반환: 누른 버튼의 index, 취소/Esc/X는 -1.</summary>
public sealed class ChoiceDialog : Window
{
    private int _choice = -1;

    private ChoiceDialog(Window? owner, string title, string message,
                         (string Label, ChoiceStyle Style, bool IsDefault)[] buttons)
    {
        Owner = owner;
        Title = title;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(new TextBlock { Text = message, FontSize = 13, TextWrapping = TextWrapping.Wrap });
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        for (var i = 0; i < buttons.Length; i++)
        {
            var (label, style, isDefault) = buttons[i];
            var index = i;
            var button = new Button
            {
                Content = label, IsDefault = isDefault, Padding = new Thickness(14, 5, 14, 5),
                Margin = new Thickness(0, 2, 8, 2),
            };
            if (style == ChoiceStyle.Primary) button.Style = (Style)FindResource("PrimaryButton");
            else if (style == ChoiceStyle.Caution) button.Style = (Style)FindResource("CautionButton");
            button.Click += (_, _) => { _choice = index; DialogResult = true; };
            row.Children.Add(button);
        }
        var cancel = new Button { Content = "취소", IsCancel = true, Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 2, 0, 2) };
        cancel.Click += (_, _) => { _choice = -1; DialogResult = false; };
        row.Children.Add(cancel);
        root.Children.Add(row);
        Content = root;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { _choice = -1; DialogResult = false; } };
    }

    public static int Show(Window? owner, string title, string message,
                           params (string Label, ChoiceStyle Style, bool IsDefault)[] buttons)
    {
        var dialog = new ChoiceDialog(owner, title, message, buttons);
        return dialog.ShowDialog() == true ? dialog._choice : -1;
    }
}

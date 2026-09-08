using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LabelSuite.App;

/// <summary>짧은 텍스트 입력 대화상자 (UDInspect InputDialog와 동일 규범):
/// Enter=확인, Esc=취소, 열릴 때 기존 값 전체 선택, 확인은 파랑 주 동작 버튼.</summary>
public sealed class InputDialog : Window
{
    private readonly TextBox _input;

    public string Value => _input.Text.Trim();

    public InputDialog(Window? owner, string title, string prompt, string initial = "",
                       string? hint = null)
    {
        Owner = owner;
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(new TextBlock { Text = prompt, FontSize = 13, TextWrapping = TextWrapping.Wrap });
        _input = new TextBox { Text = initial, FontSize = 15, Padding = new Thickness(4), Margin = new Thickness(0, 8, 0, 0) };
        root.Children.Add(_input);
        if (hint is not null)
            root.Children.Add(new TextBlock
            {
                Text = hint, Margin = new Thickness(0, 6, 0, 0),
                Style = (Style)FindResource("HintText"),
            });
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var ok = new Button
        {
            Content = "확인", IsDefault = true, Padding = new Thickness(18, 4, 18, 4),
            Style = (Style)FindResource("PrimaryButton"),
        };
        ok.Click += (_, _) => { if (Value.Length > 0) DialogResult = true; };
        var cancel = new Button
        {
            Content = "취소", IsCancel = true, Padding = new Thickness(18, 4, 18, 4),
            Margin = new Thickness(8, 2, 2, 2),
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;

        Loaded += (_, _) => { _input.SelectAll(); _input.Focus(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; } };
    }

    /// <summary>값을 입력받는다. 취소하면 null.</summary>
    public static string? Ask(Window? owner, string title, string prompt, string initial = "",
                              string? hint = null)
    {
        var dialog = new InputDialog(owner, title, prompt, initial, hint);
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }
}

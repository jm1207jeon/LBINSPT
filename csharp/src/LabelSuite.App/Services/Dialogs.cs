using System.Windows;

namespace LabelSuite.App;

/// <summary>공용 대화상자 규범 (UDInspect ShowConfirm과 동일):
/// · 예/아니오 확인은 항상 기본 버튼 '아니오' — Enter(또는 스캐너 종료 문자) 오조작으로
///   삭제·학습·과금이 실행되지 않게 한다.
/// · 항상 Owner를 지정해 다중 모니터에서 부모 창 위에 뜨게 한다.
/// App 코드에서 MessageBox.Show를 직접 부르지 말고 이 클래스를 쓴다(AppSourceGuardTests가 감시).</summary>
public static class Dialogs
{
    public static Window? OwnerOf(DependencyObject? anchor)
    {
        if (anchor is Window window) return window;
        if (anchor is not null && Window.GetWindow(anchor) is { } owner) return owner;
        return Application.Current?.MainWindow;
    }

    /// <summary>예/아니오 확인. 기본 버튼은 '아니오'. 반환: 예를 눌렀는가.</summary>
    public static bool Confirm(DependencyObject? anchor, string message, string caption,
                               MessageBoxImage icon = MessageBoxImage.Warning)
    {
        var owner = OwnerOf(anchor);
        var result = owner is null
            ? MessageBox.Show(message, caption, MessageBoxButton.YesNo, icon, MessageBoxResult.No)
            : MessageBox.Show(owner, message, caption, MessageBoxButton.YesNo, icon, MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    public static void Info(DependencyObject? anchor, string message, string caption) =>
        Show(anchor, message, caption, MessageBoxImage.Information);

    public static void Warn(DependencyObject? anchor, string message, string caption) =>
        Show(anchor, message, caption, MessageBoxImage.Warning);

    public static void Error(DependencyObject? anchor, string message, string caption)
    {
        AppLog.Error($"{caption}: {message}");
        Show(anchor, message, caption, MessageBoxImage.Error);
    }

    private static void Show(DependencyObject? anchor, string message, string caption,
                             MessageBoxImage icon)
    {
        var owner = OwnerOf(anchor);
        if (owner is null) MessageBox.Show(message, caption, MessageBoxButton.OK, icon);
        else MessageBox.Show(owner, message, caption, MessageBoxButton.OK, icon);
    }
}

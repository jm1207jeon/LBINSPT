using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace LabelSuite.App;

/// <summary>피드백 애니메이션 (UDInspect FlashRow/FlashCell 규범): "방금 무엇이 바뀌었는지"를
/// 앰버(650ms)/주황(900ms) 페이드로 알린다. 스타일 배경을 덮지 않도록 끝나면 값을 지운다.</summary>
public static class UiFx
{
    private static Color Amber(FrameworkElement scope) =>
        scope.TryFindResource("FlashAmberColor") is Color c ? c : Color.FromRgb(255, 193, 7);

    private static Color Orange(FrameworkElement scope) =>
        scope.TryFindResource("FlashOrangeColor") is Color c ? c : Color.FromRgb(255, 152, 0);

    /// <summary>DataGrid의 특정 항목 행을 앰버로 번쩍인다 (가상화된 행은 한 번 재시도).</summary>
    public static void FlashRow(DataGrid grid, object item, int ms = 650)
    {
        if (item is null) return;
        try
        {
            grid.ScrollIntoView(item);
            grid.UpdateLayout();
        }
        catch (Exception) { return; }
        if (grid.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow row)
            Flash(row, Amber(grid), 70, ms);
        else
            grid.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (grid.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow retry)
                    Flash(retry, Amber(grid), 70, ms);
            });
    }

    /// <summary>셀·패널 등 임의 Control/Panel의 배경을 주황으로 번쩍인다 (900ms).</summary>
    public static void FlashCell(FrameworkElement element, int ms = 900) =>
        Flash(element, Orange(element), 170, ms);

    /// <summary>Shape(사각형 등)의 Fill을 앰버 → 투명으로 페이드한다 (영역 등록 직후 표시).</summary>
    public static void FlashShape(Shape shape, int ms = 650)
    {
        var color = Amber(shape);
        var brush = new SolidColorBrush(Color.FromArgb(70, color.R, color.G, color.B));
        shape.Fill = brush;
        var animation = new ColorAnimation(Color.FromArgb(0, color.R, color.G, color.B),
                                           TimeSpan.FromMilliseconds(ms))
        { FillBehavior = FillBehavior.Stop };
        animation.Completed += (_, _) => shape.Fill = Brushes.Transparent;
        brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
    }

    private static void Flash(FrameworkElement element, Color color, byte alpha, int ms)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        var animation = new ColorAnimation(Color.FromArgb(0, color.R, color.G, color.B),
                                           TimeSpan.FromMilliseconds(ms))
        { FillBehavior = FillBehavior.Stop };
        switch (element)
        {
            case Control control:
                control.Background = brush;
                animation.Completed += (_, _) => control.ClearValue(Control.BackgroundProperty);
                break;
            case Panel panel:
                panel.Background = brush;
                animation.Completed += (_, _) => panel.ClearValue(Panel.BackgroundProperty);
                break;
            case Border border:
                border.Background = brush;
                animation.Completed += (_, _) => border.ClearValue(Border.BackgroundProperty);
                break;
            default:
                return;
        }
        brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
    }
}

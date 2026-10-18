using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using MyPan.Contracts;

namespace MyPan.Desktop;

internal static class DesktopTheme
{
    internal static readonly IBrush Teal = Brush.Parse("#13AEBB"), Navy = Brush.Parse("#243942"), Muted = Brush.Parse("#7B858E"), Line = Brush.Parse("#E7EBEE"), Ink = Brush.Parse("#303940");
    public static void Apply(Application app)
    {
        app.Resources["SystemAccentColor"] = Color.Parse("#13AEBB");
        app.Styles.Add(new Style(x => x.OfType<Window>()) { Setters = { new Setter(Window.BackgroundProperty, Brushes.White), new Setter(Window.FontFamilyProperty, new FontFamily("Segoe UI,Microsoft YaHei,sans-serif")), new Setter(Window.FontSizeProperty, 13d), new Setter(Window.ForegroundProperty, Ink) } });
        app.Styles.Add(new Style(x => x.OfType<Button>()) { Setters = { new Setter(Button.PaddingProperty, new Thickness(11, 7)), new Setter(Button.CornerRadiusProperty, new CornerRadius(6)), new Setter(Button.MinHeightProperty, 32d), new Setter(Button.FontSizeProperty, 12d), new Setter(Button.BackgroundProperty, Brushes.White), new Setter(Button.ForegroundProperty, Brush.Parse("#294553")), new Setter(Button.BorderBrushProperty, Brush.Parse("#D9E4EB")), new Setter(Button.BorderThicknessProperty, new Thickness(1)), new Setter(Button.HorizontalContentAlignmentProperty, HorizontalAlignment.Center) } });
        app.Styles.Add(new Style(x => x.OfType<Button>().Class("accent")) { Setters = { new Setter(Button.BackgroundProperty, Teal), new Setter(Button.ForegroundProperty, Brushes.White) } });
        app.Styles.Add(new Style(x => x.OfType<Button>().Class("nav")) { Setters = { new Setter(Button.BackgroundProperty, Brushes.Transparent), new Setter(Button.ForegroundProperty, Brush.Parse("#616C75")), new Setter(Button.BorderThicknessProperty, new Thickness(0)), new Setter(Button.HorizontalContentAlignmentProperty, HorizontalAlignment.Left), new Setter(Button.PaddingProperty, new Thickness(14, 14)), new Setter(Button.CornerRadiusProperty, new CornerRadius(4)), new Setter(Button.FontSizeProperty, 14d) } });
        app.Styles.Add(new Style(x => x.OfType<Button>().Class("nav").Class("active")) { Setters = { new Setter(Button.BackgroundProperty, Teal), new Setter(Button.ForegroundProperty, Brushes.White) } });
        app.Styles.Add(new Style(x => x.OfType<Button>().Class("link")) { Setters = { new Setter(Button.BackgroundProperty, Brushes.Transparent), new Setter(Button.BorderThicknessProperty, new Thickness(0)), new Setter(Button.ForegroundProperty, Brush.Parse("#168DCA")), new Setter(Button.PaddingProperty, new Thickness(0)), new Setter(Button.MinHeightProperty, 20d), new Setter(Button.HorizontalContentAlignmentProperty, HorizontalAlignment.Left) } });
        app.Styles.Add(new Style(x => x.OfType<Button>().Class("icon")) { Setters = { new Setter(Button.BackgroundProperty, Brushes.Transparent), new Setter(Button.BorderThicknessProperty, new Thickness(0)), new Setter(Button.PaddingProperty, new Thickness(7)), new Setter(Button.MinWidthProperty, 30d) } });
        app.Styles.Add(new Style(x => x.OfType<TextBox>()) { Setters = { new Setter(TextBox.CornerRadiusProperty, new CornerRadius(6)), new Setter(TextBox.PaddingProperty, new Thickness(10, 7)), new Setter(TextBox.FontSizeProperty, 13d) } });
        app.Styles.Add(new Style(x => x.OfType<TabItem>()) { Setters = { new Setter(TabItem.FontSizeProperty, 12d), new Setter(TabItem.PaddingProperty, new Thickness(13, 8)), new Setter(TabItem.MinHeightProperty, 34d) } });
    }
    public static Control Logo(double size = 35) => new Border { Width = size, Height = size, CornerRadius = new CornerRadius(9), Background = Teal, HorizontalAlignment = HorizontalAlignment.Left,
        Child = new Avalonia.Controls.Shapes.Path { Width = size * .68, Height = size * .68, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Stretch = Stretch.Uniform, Stroke = Brushes.White, StrokeThickness = 1.6, StrokeJoin = PenLineJoin.Round, Data = Geometry.Parse("M12 2L23 8L12 14L1 8Z M1 13L12 19L23 13 M1 18L12 24L23 18") } };
    public static void Fit(Window window)
    {
        var screen = window.Screens.ScreenFromWindow(window);
        if (screen == null) return;
        var width = screen.WorkingArea.Width / screen.Scaling - 32;
        var height = screen.WorkingArea.Height / screen.Scaling - 40;
        window.MinWidth = Math.Min(window.MinWidth, Math.Max(320, width)); window.MinHeight = Math.Min(window.MinHeight, Math.Max(320, height));
        window.Width = Math.Min(window.Width, width); window.Height = Math.Min(window.Height, height);
    }
}

internal sealed class ListPager : StackPanel
{
    private readonly TextBlock label = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = DesktopTheme.Muted, FontSize = 11 };
    private readonly Button previous = new() { Content = "上一页" }, next = new() { Content = "下一页" };
    private int page = 1, pages = 1;
    public event Action<int>? Changed;
    public ListPager()
    {
        Orientation = Orientation.Horizontal; Spacing = 8; HorizontalAlignment = HorizontalAlignment.Right;
        Children.Add(label); Children.Add(previous); Children.Add(next);
        previous.Click += (_, _) => Changed?.Invoke(page - 1); next.Click += (_, _) => Changed?.Invoke(page + 1);
    }
    public int Update(int total, int current, int pageSize)
    {
        pages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)); page = Math.Clamp(current, 1, pages);
        label.Text = $"共 {total} 条 · {page} / {pages} 页"; previous.IsEnabled = page > 1; next.IsEnabled = page < pages; return page;
    }
}

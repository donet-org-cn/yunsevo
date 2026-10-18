using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MyPan.Desktop;

internal static class DesktopGlyphs
{
    public static Control File(string path, double size)
    {
        var ext = System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        var color = Brush.Parse(ext is "XLS" or "XLSX" or "CSV" ? "#328760" : ext is "PDF" or "PPT" or "PPTX" ? "#C9584E" : ext is "PNG" or "JPG" or "WEBP" ? "#8A70C9" : "#4C99C4");
        var grid = new Grid { Width = size, Height = size + 4, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left }; grid.Children.Add(Create("file", size, Brush.Parse("#ABB4BC")));
        var badge = new Border { Background = color, CornerRadius = new CornerRadius(2), Padding = new Thickness(3, 1), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom };
        badge.Child = new TextBlock { Text = ext.Length is > 0 and <= 4 ? ext : "FILE", FontSize = 7, Foreground = Brushes.White, FontWeight = FontWeight.SemiBold }; grid.Children.Add(badge); return grid;
    }
    public static Control Create(string name, double size = 20, IBrush? color = null)
    {
        var data = name switch
        {
            "tasks" => "M3 5H10L12 7H21V21H3Z M8 11L5 14L8 17 M16 11L19 14L16 17 M5 14H19",
            "server" => "M3 3H21V8H3Z M3 10H21V15H3Z M3 17H21V22H3Z M6 5.5H7 M6 12.5H7 M6 19.5H7 M11 5.5H18 M11 12.5H18 M11 19.5H18",
            "folder" => "M2 6H10L12 9H22V21H2Z M2 6V3H9L12 6H20V9",
            "log" => "M4 2H16L21 7V22H4Z M15 2V8H21 M8 12H17 M8 16H17 M8 19H14",
            "bell" => "M5 17H19L17 14V9C17 2 7 2 7 9V14Z M10 21H14 M12 2V4",
            "settings" => "M8 3L10 5H14L16 3L20 7L18 9V13L20 15L16 19L14 17H10L8 19L4 15L6 13V9L4 7Z M15 11A3 3 0 1 1 9 11A3 3 0 1 1 15 11",
            "plus" => "M12 4V20 M4 12H20",
            "pause" => "M9 5V19 M15 5V19",
            "play" => "M7 4L20 12L7 20Z",
            "trash" => "M3 6H21 M9 3H15 M6 6L7 22H17L18 6 M10 10V18 M14 10V18",
            "check" => "M4 12L10 18L21 6",
            "warning" => "M12 3L23 22H1Z M12 9V15 M12 18V19",
            "cloud" => "M6 19C0 19 0 10 6 10C7 1 20 3 19 11C25 11 25 19 19 19Z",
            "lock" => "M5 11H19V22H5Z M8 11V6C8 0 16 0 16 6V11",
            "chevron" => "M7 9L12 14L17 9",
            "external" => "M14 3H22V11 M22 3L11 14 M10 5H3V22H20V15",
            "user" => "M17 6A5 5 0 1 1 7 6A5 5 0 1 1 17 6 M3 23V20C3 10 21 10 21 20V23",
            "globe" => "M23 12A11 11 0 1 1 1 12A11 11 0 1 1 23 12 M1 12H23 M12 1C5 5 5 19 12 23C19 19 19 5 12 1",
            "search" => "M17 9A7 7 0 1 1 3 9A7 7 0 1 1 17 9 M15 15L22 22",
            "download" => "M12 2V17 M6 11L12 17L18 11 M3 17V22H21V17",
            "upload" => "M12 17V2 M6 8L12 2L18 8 M3 17V22H21V17",
            "refresh" => "M21 10A9 9 0 1 0 20 18 M21 3V10H14",
            "close" => "M5 5L19 19 M19 5L5 19",
            _ => "M4 2H15L21 8V22H4Z M15 2V8H21 M8 12H17 M8 16H17"
        };
        return new Avalonia.Controls.Shapes.Path { Width = size, Height = size, Stretch = Stretch.Uniform, Stroke = color ?? DesktopTheme.Muted, StrokeThickness = 1.7, StrokeJoin = PenLineJoin.Round, StrokeLineCap = PenLineCap.Round, Data = Geometry.Parse(data), IsHitTestVisible = false };
    }
}

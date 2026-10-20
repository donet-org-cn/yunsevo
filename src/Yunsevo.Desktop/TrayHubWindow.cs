using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Yunsevo.Contracts;

namespace Yunsevo.Desktop;

internal sealed class TrayHubWindow : Window
{
    private readonly Border badge = new() { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock heading = new() { FontSize = 12 }, detail = new() { FontSize = 10, Foreground = DesktopTheme.Muted, TextTrimming = TextTrimming.CharacterEllipsis }, identity = new() { FontSize = 16, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel recentRows = new(), notificationRows = new();
    private readonly TextBlock notificationTab = new() { Text = "通知", FontSize = 12 };
    private string? fingerprint;
    internal DateTimeOffset LastDeactivated { get; private set; }
    internal readonly TabControl Tabs = new();
    public TrayHubWindow(Action openMain, Action openSettings, Action openFolder, Action openWeb, Action openActivity)
    {
        Title = ProductBrand.Name + " · 同步状态"; Width = 416; Height = 586; CanResize = false; ShowInTaskbar = false; Topmost = true; WindowDecorations = WindowDecorations.None; Background = Brushes.Transparent; TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        var content = new Grid { RowDefinitions = new("91,*,72") };
        var summary = new Grid { ColumnDefinitions = new("28,*,32"), RowDefinitions = new("30,25,16"), Margin = new Thickness(18, 12, 14, 8) };
        var logo = DesktopTheme.Logo(23); logo.VerticalAlignment = VerticalAlignment.Center; summary.Children.Add(logo); Grid.SetColumn(identity, 1); summary.Children.Add(identity);
        var settings = new Button { Content = DesktopGlyphs.Create("settings", 21) }; settings.Classes.Add("icon"); ToolTip.SetTip(settings, "全局设置"); settings.Click += (_, _) => { Hide(); openSettings(); }; Grid.SetColumn(settings, 2); summary.Children.Add(settings);
        Grid.SetRow(badge, 1); summary.Children.Add(badge); Grid.SetColumn(heading, 1); Grid.SetRow(heading, 1); heading.VerticalAlignment = VerticalAlignment.Center; summary.Children.Add(heading);
        Grid.SetColumn(detail, 1); Grid.SetRow(detail, 2); summary.Children.Add(detail); content.Children.Add(summary);
        Tabs.ItemsSource = new[] { new TabItem { Header = new TextBlock { Text = "最近更改", FontSize = 12 }, Content = new ScrollViewer { Content = recentRows } }, new TabItem { Header = notificationTab, Content = new ScrollViewer { Content = notificationRows } } }; Tabs.Margin = new Thickness(10, 0); Grid.SetRow(Tabs, 1); content.Children.Add(Tabs);
        var footer = new Grid { ColumnDefinitions = new("*,*,*,*"), Margin = new Thickness(6, 3) };
        var index = 0;
        foreach (var (icon, tip, action) in new[] { ("folder", "打开文件夹", openFolder), ("globe", "在线查看", openWeb), ("tasks", "管理任务", openMain), ("log", "同步日志", openActivity) })
        {
            var label = new StackPanel { Spacing = 7, HorizontalAlignment = HorizontalAlignment.Center }; var glyph = DesktopGlyphs.Create(icon, 20); glyph.HorizontalAlignment = HorizontalAlignment.Center; label.Children.Add(glyph); label.Children.Add(new TextBlock { Text = tip, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center });
            var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch }; button.Classes.Add("icon"); ToolTip.SetTip(button, tip); button.Click += (_, _) => { Hide(); action(); }; Grid.SetColumn(button, index++); footer.Children.Add(button);
        }
        var footerBorder = new Border { Child = footer, BorderBrush = DesktopTheme.Line, BorderThickness = new Thickness(0, 1, 0, 0), Background = Brush.Parse("#F3F5F7") }; Grid.SetRow(footerBorder, 2); content.Children.Add(footerBorder);
        Content = new Border { Child = content, Background = Brush.Parse("#FAFBFC"), CornerRadius = new CornerRadius(9), Margin = new Thickness(8), BorderBrush = Brush.Parse("#D9E1E5"), BorderThickness = new Thickness(1), BoxShadow = BoxShadows.Parse("0 2 8 0 #30000000"), ClipToBounds = true };
        Deactivated += (_, _) => { if (IsVisible) { LastDeactivated = DateTimeOffset.UtcNow; Hide(); } };
    }
    internal void Update(string state, string title, string subtitle, string accountName, IReadOnlyList<ClientActivity> recent, IReadOnlyList<ClientActivity> notifications, bool showRecent)
    {
        var signature = state + title + subtitle + accountName + showRecent + string.Join('|', recent.Select(x => x.Message + x.Time)) + string.Join('|', notifications.Select(x => x.Message));
        if (signature == fingerprint) return; fingerprint = signature;
        var color = state == "connected" ? Brush.Parse("#60BC44") : state is "error" or "offline" ? Brush.Parse("#DC9B36") : DesktopTheme.Muted;
        badge.Background = color; badge.Child = DesktopGlyphs.Create(state == "connected" ? "check" : state == "signed-out" ? "user" : state is "error" or "offline" ? "warning" : "pause", 9, Brushes.White);
        identity.Text = ProductBrand.Desktop + (accountName.Length == 0 ? "" : " · " + accountName); heading.Text = title; heading.Foreground = DesktopTheme.Ink; detail.Text = subtitle;
        recentRows.Children.Clear(); notificationRows.Children.Clear(); notificationTab.Text = notifications.Count == 0 ? "通知" : "通知 · " + notifications.Count;
        if (showRecent) foreach (var activity in recent.Take(12)) recentRows.Children.Add(MainWindow.ActivityRow(activity, true));
        if (!showRecent || recent.Count == 0) recentRows.Children.Add(new TextBlock { Text = !showRecent ? "已关闭最近活动显示\n可在全局设置中开启" : state == "signed-out" ? "登录后在这里查看文件更改" : "暂无文件更改", Foreground = DesktopTheme.Muted, TextAlignment = TextAlignment.Center, Margin = new Thickness(20, 48), TextWrapping = TextWrapping.Wrap });
        foreach (var item in notifications.Take(12)) notificationRows.Children.Add(MainWindow.ActivityRow(item, true));
        if (notifications.Count == 0) notificationRows.Children.Add(new TextBlock { Text = "暂无需要处理的通知", Foreground = DesktopTheme.Muted, TextAlignment = TextAlignment.Center, Margin = new Thickness(20, 48) });
    }
    public void ShowNearTray()
    {
        var point = new PixelPoint(0, 0);
#if WINDOWS
        if (GetCursorPos(out var cursor)) point = new PixelPoint(cursor.X, cursor.Y);
#endif
        var screen = Screens.ScreenFromPoint(point) ?? Screens.Primary;
        if (screen != null)
        {
            var area = screen.WorkingArea; Width = Math.Min(416, area.Width / screen.Scaling - 12); Height = Math.Min(586, area.Height / screen.Scaling - 12); var width = (int)(Width * screen.Scaling); var height = (int)(Height * screen.Scaling);
            var x = point.X == 0 ? area.Right - width - 12 : point.X - width + 22;
            var y = OperatingSystem.IsMacOS() ? area.Y + 8 : area.Bottom - height - 8;
            Position = new PixelPoint(Math.Clamp(x, area.X + 4, Math.Max(area.X + 4, area.Right - width - 4)), Math.Max(area.Y + 4, y));
        }
        Show(); Activate();
    }
#if WINDOWS
    [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);
#endif
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Yunsevo.Desktop;

internal sealed partial class MainWindow
{
    private void BuildActivityPage()
    {
        activityPage.RowDefinitions = new("Auto,Auto,*,Auto");
        activityPage.Children.Add(new TextBlock { Text = "同步日志", FontSize = 19, FontWeight = FontWeight.Medium, Margin = new Thickness(0, 0, 0, 15) });
        var tools = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 10, Margin = new Thickness(0, 0, 0, 12) }; tools.Children.Add(activityFilter); Grid.SetColumn(activitySearch, 1); tools.Children.Add(activitySearch);
        var logs = ToolButton("打开日志目录", "external"); logs.Click += (_, _) => { Directory.CreateDirectory(history.DirectoryPath); OpenFolder(history.DirectoryPath); }; Grid.SetColumn(logs, 2); tools.Children.Add(logs); Grid.SetRow(tools, 1); activityPage.Children.Add(tools);
        var scroll = new ScrollViewer { Content = activityRows }; Grid.SetRow(scroll, 2); activityPage.Children.Add(scroll);
        activityPager.Margin = new Thickness(0, 10); Grid.SetRow(activityPager, 3); activityPage.Children.Add(activityPager);
        activityFilter.SelectionChanged += (_, _) => { activityPageIndex = 1; RefreshActivities(); }; activitySearch.TextChanged += (_, _) => { activityPageIndex = 1; RefreshActivities(); }; activityPager.Changed += page => { activityPageIndex = page; RefreshActivities(); };
    }
    private void RenderActivityRows()
    {
        activityRows.Children.Clear();
        var items = activities.Select(ClientActivity.Parse).Where(x => (activityFilter.SelectedIndex != 1 || x.IsFile) && (activityFilter.SelectedIndex != 2 || x.Issue) && (x.Message + x.Task).Contains(activitySearch.Text?.Trim() ?? "", StringComparison.OrdinalIgnoreCase)).ToList();
        activityPageIndex = activityPager.Update(items.Count, activityPageIndex, 15);
        foreach (var item in items.Skip((activityPageIndex - 1) * 15).Take(15)) activityRows.Children.Add(ActivityRow(item, false));
        if (items.Count == 0) activityRows.Children.Add(new TextBlock { Text = "暂无匹配的同步记录", Foreground = DesktopTheme.Muted, Margin = new Thickness(15, 35) });
        if (items.Any(x => x.Message.StartsWith("同步完成", StringComparison.Ordinal))) lastSynchronized = items.Where(x => x.Message.StartsWith("同步完成", StringComparison.Ordinal)).Select(x => x.Time).FirstOrDefault();
        RenderNotifications(); UpdateTrayHub();
    }
    internal static Control ActivityRow(ClientActivity item, bool compact)
    {
        var row = new Grid { ColumnDefinitions = new(compact ? "43,*" : "34,*,88"), Margin = new Thickness(compact ? 12 : 8, compact ? 12 : 10), MinHeight = compact ? 46 : 34 };
        var icon = item.Path.Length > 0 && compact ? DesktopGlyphs.File(item.Path, 31) : DesktopGlyphs.Create(item.Path.Length > 0 ? "file" : item.Glyph, 20, item.Issue ? Brush.Parse("#D39A39") : item.Kind == "upload" ? DesktopTheme.Teal : DesktopTheme.Muted); icon.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(icon);
        var text = new StackPanel { Spacing = 3 }; var title = item.Path.Length == 0 ? item.Message : System.IO.Path.GetFileName(item.Path);
        text.Children.Add(new TextBlock { Text = title, FontSize = compact ? 12 : 13, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = compact ? item.Label + (item.Task.Length == 0 ? "" : " · " + item.Task) + " / " + item.RelativeTime() : (item.Task.Length == 0 ? item.Label : item.Task + " · " + item.Label) + (item.Path.Length == 0 ? "" : " · " + item.Path), Foreground = DesktopTheme.Muted, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis }); Grid.SetColumn(text, 1); row.Children.Add(text);
        if (!compact) { var time = new TextBlock { Text = item.Time?.ToLocalTime().ToString("MM-dd HH:mm") ?? "", Foreground = DesktopTheme.Muted, FontSize = 10, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right }; Grid.SetColumn(time, 2); row.Children.Add(time); }
        var border = new Border { Child = row, BorderBrush = DesktopTheme.Line, BorderThickness = new Thickness(0, 0, 0, 1) }; ToolTip.SetTip(border, item.Message); return border;
    }
    private void BuildNotificationsPage()
    {
        notificationsPage.RowDefinitions = new("Auto,Auto,*,Auto"); notificationsPage.Children.Add(new TextBlock { Text = "通知", FontSize = 19, FontWeight = FontWeight.Medium, Margin = new Thickness(0, 0, 0, 12) });
        var description = Caption("查看连接异常、需要处理的项目和离线删除确认。历史问题同时保留在同步日志中。"); description.Margin = new Thickness(0, 0, 0, 12); Grid.SetRow(description, 1); notificationsPage.Children.Add(description);
        var scroll = new ScrollViewer { Content = notificationRows }; Grid.SetRow(scroll, 2); notificationsPage.Children.Add(scroll); Grid.SetRow(notificationPager, 3); notificationPager.Margin = new Thickness(0, 10); notificationsPage.Children.Add(notificationPager); notificationPager.Changed += page => { notificationPage = page; RenderNotifications(); };
    }
    private List<ClientActivity> CurrentNotifications()
    {
        var items = new List<ClientActivity>();
        foreach (var item in accounts.Where(x => x.NeedsLogin || x.Session == null)) items.Add(new(null, item.ToString(), "issue", "", item.Status, true));
        foreach (var pair in pending) items.Add(new(null, tasks.Find(x => x.Profile.Id == pair.Key.Task)?.Profile.Name ?? "同步任务", "issue", pair.Value.Path, "离线删除等待确认：" + pair.Value.Path, true));
        foreach (var task in tasks.Where(x => x.State is "error" or "offline" or "signed-out" && !pending.Keys.Any(p => p.Task == x.Profile.Id))) items.Add(new(null, task.Profile.Name, "issue", "", task.Message, true));
        return items;
    }
    private void RenderNotifications()
    {
        var items = CurrentNotifications(); notificationCount.Text = items.Count == 0 ? "" : items.Count.ToString(); notificationRows.Children.Clear(); notificationPage = notificationPager.Update(items.Count, notificationPage, 10);
        if (items.Count == 0)
        {
            var clear = new StackPanel { Spacing = 15, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(20, 65) }; clear.Children.Add(DesktopGlyphs.Create("check", 38, Brush.Parse("#55B943"))); clear.Children.Add(new TextBlock { Text = "暂无需要处理的通知", Foreground = DesktopTheme.Muted }); notificationRows.Children.Add(clear);
        }
        foreach (var item in items.Skip((notificationPage - 1) * 10).Take(10))
        {
            notificationRows.Children.Add(ActivityRow(item, false));
            if (pending.Values.Any(x => x.Path == item.Path)) { var button = ToolButton("确认离线删除", "warning"); button.Margin = new Thickness(38, 5); button.HorizontalAlignment = HorizontalAlignment.Left; button.Click += async (_, _) => await Review(); notificationRows.Children.Add(button); }
        }
    }
}

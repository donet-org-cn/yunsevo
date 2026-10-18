using System.Net.Http.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MyPan.Contracts;

namespace MyPan.Desktop;

internal sealed partial class MainWindow
{
    private readonly TabControl settingsTabs = new();
    private void BuildSettingsPage()
    {
        accountPage.RowDefinitions = new("Auto,*"); accountPage.Children.Add(new TextBlock { Text = "全局设置", FontSize = 19, FontWeight = FontWeight.Medium, Margin = new Thickness(0, 0, 0, 14) });
        var general = new StackPanel { Spacing = 17, Margin = new Thickness(5, 18) };
        general.Children.Add(new TextBlock { Text = "窗口与后台运行", FontWeight = FontWeight.SemiBold });
        var close = new CheckBox { Content = "关闭主窗口后继续在托盘运行", IsChecked = displaySettings.CloseToTray }; general.Children.Add(close);
        close.IsCheckedChanged += (_, _) => SaveDisplay(displaySettings with { CloseToTray = close.IsChecked == true });
        general.Children.Add(Caption("从托盘右键菜单选择“退出沄序”可停止所有任务并退出程序。")); general.Children.Add(Divider());
        general.Children.Add(new TextBlock { Text = "文件删除", FontWeight = FontWeight.SemiBold }); general.Children.Add(Caption("在线运行时，本地删除直接同步到云端回收站。退出、暂停或断线期间的本地删除会等待你确认；“稍后处理”不会自动转为删除云端。"));
        var appearance = new StackPanel { Spacing = 17, Margin = new Thickness(5, 18) };
        var compact = new CheckBox { Content = "使用紧凑的任务列表", IsChecked = displaySettings.CompactRows }; var fullPath = new CheckBox { Content = "在任务行中显示完整云端路径", IsChecked = displaySettings.ShowFullRemotePath }; var recent = new CheckBox { Content = "在托盘面板显示最近文件活动", IsChecked = displaySettings.ShowRecentInTray };
        appearance.Children.Add(compact); appearance.Children.Add(fullPath); appearance.Children.Add(recent); appearance.Children.Add(Caption("这些选项只影响界面显示，不改变目录绑定和同步状态。"));
        compact.IsCheckedChanged += (_, _) => { SaveDisplay(displaySettings with { CompactRows = compact.IsChecked == true }); taskPageSize = Math.Clamp((int)((Bounds.Height - 225) / (displaySettings.CompactRows ? 72 : 88)), 1, 15); BuildCards(); };
        fullPath.IsCheckedChanged += (_, _) => { SaveDisplay(displaySettings with { ShowFullRemotePath = fullPath.IsChecked == true }); BuildCards(); };
        recent.IsCheckedChanged += (_, _) => { SaveDisplay(displaySettings with { ShowRecentInTray = recent.IsChecked == true }); UpdateTrayHub(); };
        var usage = new Grid { RowDefinitions = new("Auto,*,Auto"), Margin = new Thickness(5, 16) };
        var intro = new StackPanel { Spacing = 8 }; intro.Children.Add(Caption("查看此账户可访问文件夹的使用量，包含当前文件、保留版本及回收站。子目录与父目录可能重叠，使用量不相加。")); intro.Children.Add(usageStatus); var refresh = ToolButton("刷新使用量", "refresh"); refresh.HorizontalAlignment = HorizontalAlignment.Left; refresh.Click += async (_, _) => await RefreshUsage(); intro.Children.Add(refresh); usage.Children.Add(intro);
        var usageScroll = new ScrollViewer { Content = usageRows, Margin = new Thickness(0, 10, 0, 0) }; Grid.SetRow(usageScroll, 1); usage.Children.Add(usageScroll); Grid.SetRow(usagePager, 2); usage.Children.Add(usagePager); usagePager.Changed += async page => { usagePage = page; await RefreshUsage(); };
        var identity = new StackPanel { Spacing = 17, Margin = new Thickness(5, 18) }; identity.Children.Add(new TextBlock { Text = "当前连接", FontWeight = FontWeight.SemiBold }); identity.Children.Add(account); identity.Children.Add(Caption("设备名称：" + Environment.MachineName));
        var passwordButton = ToolButton("修改登录密码", "lock"); passwordButton.HorizontalAlignment = HorizontalAlignment.Left; passwordButton.Click += async (_, _) => await Operate(ChangeLoginPassword); identity.Children.Add(passwordButton);
        var change = ToolButton("添加服务器连接", "server"); change.HorizontalAlignment = HorizontalAlignment.Left; change.Click += async (_, _) => await Operate(async () => { await AddServerDialog(); BuildCards(); }); identity.Children.Add(change);
        var signout = ToolButton("退出当前服务器登录", "lock"); signout.HorizontalAlignment = HorizontalAlignment.Left; signout.Click += async (_, _) => await Operate(Logout); identity.Children.Add(signout);
        identity.Children.Add(Caption("登录状态加密保存，下次启动自动连接。退出当前服务器会清除该连接的登录凭据，其他服务器继续同步。在任务列表中点击服务器标题可切换当前连接。"));
        var web = ToolButton("打开服务器网页", "external"); web.HorizontalAlignment = HorizontalAlignment.Left; web.Click += (_, _) => OpenServerWeb(); identity.Children.Add(web);
        account.TextWrapping = TextWrapping.Wrap;
        settingsTabs.ItemsSource = new[] { new TabItem { Header = new TextBlock { Text = "常规", FontSize = 12 }, Content = new ScrollViewer { Content = general } }, new TabItem { Header = new TextBlock { Text = "显示", FontSize = 12 }, Content = new ScrollViewer { Content = appearance } }, new TabItem { Header = new TextBlock { Text = "使用量", FontSize = 12 }, Content = usage }, new TabItem { Header = new TextBlock { Text = "账户", FontSize = 12 }, Content = new ScrollViewer { Content = identity } } };
        settingsTabs.SelectionChanged += async (_, _) => { if (settingsTabs.SelectedIndex == 2 && accountPage.IsVisible) await RefreshUsage(); }; Grid.SetRow(settingsTabs, 1); accountPage.Children.Add(settingsTabs);
    }
    private void SaveDisplay(DesktopDisplaySettings settings)
    {
        try { Directory.CreateDirectory(home); DisplayPreferences.Save(DisplaySettingsFile, settings); displaySettings = settings; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { notice.Text = "显示设置保存失败：" + ex.Message; Log(notice.Text); }
    }
    private void ClearUsage() { usageEpoch++; usagePage = 1; usageRows.Children.Clear(); usageStatus.Text = ""; }
    private static string FormatBytes(long value)
    {
        var bytes = (double)value; var units = new[] { "B", "KiB", "MiB", "GiB", "TiB" }; var index = 0;
        while (bytes >= 1024 && index < units.Length - 1) { bytes /= 1024; index++; }
        return bytes.ToString(index == 0 ? "0" : "0.##") + " " + units[index];
    }
    private async Task RefreshUsage()
    {
        if (connection == null || session == null) return;
        var version = ++usageEpoch; var activeConnection = connection; usageStatus.Text = "正在读取服务器使用量…";
        try
        {
            var page = await activeConnection.Http.GetFromJsonAsync("api/browser/folders?page=" + usagePage + "&pageSize=20", ApiJson.Default.PagedResultSyncFolder) ?? throw new IOException("目录响应为空。");
            var rows = await Task.WhenAll(page.Items.Where(x => (x.Access & 1) != 0).Select(async folder =>
            {
                try { return (Folder: folder, Info: await activeConnection.Http.GetFromJsonAsync("api/folders/" + folder.Id + "/policy", ApiJson.Default.FolderPolicyInfo), Error: (string?)null); }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException) { return (Folder: folder, Info: (FolderPolicyInfo?)null, Error: "暂时无法读取"); }
            }));
            if (version != usageEpoch || connection != activeConnection || session == null) return;
            usageRows.Children.Clear(); usagePage = usagePager.Update(page.Total, page.Page, page.PageSize);
            foreach (var item in rows)
            {
                var line = new Grid { ColumnDefinitions = new("34,*,Auto"), MinHeight = 67, Margin = new Thickness(9, 0) }; var glyph = DesktopGlyphs.Create(item.Folder.Category == "private" ? "lock" : "folder", 23); glyph.VerticalAlignment = VerticalAlignment.Center; line.Children.Add(glyph);
                var shown = MyPan.Sync.SyncProfiles.DisplayRemote(item.Folder.Path, session.User.Username);
                var name = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center }; name.Children.Add(new TextBlock { Text = System.IO.Path.GetFileName(shown), FontSize = 13 }); name.Children.Add(new TextBlock { Text = "/" + shown + " · " + AddTaskWizard.Category(item.Folder), FontSize = 10, Foreground = DesktopTheme.Muted, TextTrimming = TextTrimming.CharacterEllipsis }); Grid.SetColumn(name, 1); line.Children.Add(name);
                var amounts = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 200, Margin = new Thickness(10, 0) }; amounts.Children.Add(new TextBlock { Text = item.Info == null ? item.Error : "已用 " + FormatBytes(item.Info.UsedBytes), FontSize = 12 });
                if (item.Info != null) { var quota = item.Info.Policy.QuotaBytes; amounts.Children.Add(new TextBlock { Text = quota.HasValue ? "本层上限 " + FormatBytes(quota.Value) : "本层未设上限", FontSize = 10, Foreground = DesktopTheme.Muted }); if (quota.HasValue) amounts.Children.Add(new ProgressBar { Minimum = 0, Maximum = Math.Max(1, quota.Value), Value = Math.Min(quota.Value, item.Info.UsedBytes), Height = 3 }); }
                Grid.SetColumn(amounts, 2); line.Children.Add(amounts); usageRows.Children.Add(new Border { Child = line, BorderBrush = DesktopTheme.Line, BorderThickness = new Thickness(0, 0, 0, 1) });
            }
            usageStatus.Text = rows.Length == 0 ? "当前页没有可读取的文件夹" : "已从服务器更新 · " + DateTime.Now.ToString("HH:mm:ss");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or ObjectDisposedException) { if (version == usageEpoch) usageStatus.Text = "读取失败：" + ex.Message; }
    }
}

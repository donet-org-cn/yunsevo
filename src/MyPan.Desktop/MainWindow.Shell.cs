using System.Net.Http.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MyPan.Contracts;
using MyPan.Sync;

namespace MyPan.Desktop;

internal sealed partial class MainWindow
{
    private DesktopDisplaySettings displaySettings = new();
    private Guid? selectedTask;
    private readonly Button pauseSelected = new(), removeSelected = new(), configureSelected = new();
    private readonly TextBlock serverTitle = new(), serverSubtitle = new(), selectionHint = new(), notificationCount = new();
    private readonly Grid notificationsPage = new() { IsVisible = false };
    private readonly StackPanel activityRows = new(), notificationRows = new(), usageRows = new();
    private readonly ComboBox activityFilter = new() { ItemsSource = new[] { "全部记录", "文件活动", "需要处理" }, SelectedIndex = 0, MinWidth = 105 };
    private readonly TextBox activitySearch = new() { PlaceholderText = "搜索文件、任务或日志", MinWidth = 160 };
    private readonly ListPager notificationPager = new(), usagePager = new();
    private readonly TextBlock usageStatus = new() { Foreground = DesktopTheme.Muted, TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<Guid, (Border Row, Border Badge)> taskRows = [];
    private readonly Dictionary<string, Avalonia.Controls.Shapes.Path> navGlyphs = [];
    private TrayHubWindow? trayHub;
    private string activePage = "tasks", currentState = "signed-out";
    private int notificationPage = 1, usagePage = 1, usageEpoch;
    private bool collapsed;
    private DateTimeOffset? lastSynchronized;
    private string DisplaySettingsFile => Path.Combine(home, "display.json");

    private static Button ToolButton(string text, string glyph, bool accent = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(DesktopGlyphs.Create(glyph, 15, accent ? Brushes.White : DesktopTheme.Muted));
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = content, Padding = new Thickness(11, 6), CornerRadius = new CornerRadius(3), MinHeight = 31 };
        if (accent) button.Classes.Add("accent"); return button;
    }
    private static TextBlock Caption(string text) => new() { Text = text, Foreground = DesktopTheme.Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private static Border Divider() => new() { Height = 1, Background = DesktopTheme.Line };
    private static Button IconButton(string glyph, string tooltip, Action action)
    {
        var button = new Button { Content = DesktopGlyphs.Create(glyph, 17) }; button.Classes.Add("icon"); ToolTip.SetTip(button, tooltip); button.Click += (_, _) => action(); return button;
    }
    private void BuildShell()
    {
        Width = 980; Height = 660; MinWidth = 720; MinHeight = 500;
        try { displaySettings = DisplayPreferences.Load(DisplaySettingsFile); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { loginError.Text = "显示设置读取失败：" + ex.Message; }
        BuildLogin();
        workspace.ColumnDefinitions = new("192,*"); workspace.RowDefinitions = new("*,Auto");
        var sidebar = new Grid { RowDefinitions = new("Auto,*,Auto"), Margin = new Thickness(10, 19, 12, 12) };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, Margin = new Thickness(10, 0, 0, 27) };
        brand.Children.Add(DesktopTheme.Logo(28)); brand.Children.Add(new TextBlock { Text = ProductBrand.Desktop, FontSize = 17, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }); sidebar.Children.Add(brand);
        var nav = new StackPanel { Spacing = 5 }; Grid.SetRow(nav, 1); sidebar.Children.Add(nav);
        foreach (var (id, title, glyph) in new[] { ("tasks", "同步任务", "tasks"), ("activity", "同步日志", "log"), ("notifications", "通知", "bell"), ("settings", "全局设置", "settings") })
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            var symbol = (Avalonia.Controls.Shapes.Path)DesktopGlyphs.Create(glyph, 20); navGlyphs[id] = symbol; content.Children.Add(symbol);
            content.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
            if (id == "notifications") { notificationCount.FontSize = 10; notificationCount.VerticalAlignment = VerticalAlignment.Center; content.Children.Add(notificationCount); }
            var button = new Button { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch }; button.Classes.Add("nav"); button.Click += (_, _) => NavigateTo(id); navigation[id] = button; nav.Children.Add(button);
        }
        var sidebarFoot = new StackPanel { Spacing = 7, Margin = new Thickness(12, 0) }; sidebarFoot.Children.Add(Caption("YUNSEVO DRIVE")); sidebarFoot.Children.Add(Caption("文件有序，协作从容")); Grid.SetRow(sidebarFoot, 2); sidebar.Children.Add(sidebarFoot);
        var sidebarBorder = new Border { Child = sidebar, BorderBrush = DesktopTheme.Line, BorderThickness = new Thickness(0, 0, 1, 0) }; Grid.SetRowSpan(sidebarBorder, 2); workspace.Children.Add(sidebarBorder);
        var pages = new Grid { Margin = new Thickness(17, 16, 18, 0) }; Grid.SetColumn(pages, 1); workspace.Children.Add(pages);
        foreach (var page in new Grid[] { taskPage, activityPage, notificationsPage, accountPage }) pages.Children.Add(page);
        notice.FontSize = 11; notice.Margin = new Thickness(0, 5);
        BuildTaskPage(); BuildActivityPage(); BuildNotificationsPage(); BuildSettingsPage();
        var footer = new Grid { ColumnDefinitions = new("*,Auto"), RowDefinitions = new("Auto,37"), Margin = new Thickness(18, 0) };
        Grid.SetColumnSpan(notice, 2); footer.Children.Add(notice);
        headline.FontSize = 11; headline.VerticalAlignment = VerticalAlignment.Center; Grid.SetRow(headline, 1); footer.Children.Add(headline);
        status.FontSize = 10; status.MaxWidth = 300; status.TextTrimming = TextTrimming.CharacterEllipsis; status.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(status, 1); Grid.SetRow(status, 1); footer.Children.Add(status);
        var footerBorder = new Border { Child = footer, BorderBrush = DesktopTheme.Line, BorderThickness = new Thickness(0, 1, 0, 0) }; Grid.SetColumn(footerBorder, 1); Grid.SetRow(footerBorder, 1); workspace.Children.Add(footerBorder);
        var shell = new Grid(); shell.Children.Add(loginShell!); shell.Children.Add(workspace); Content = shell;
        login.Click += async (_, _) => await Operate(Login); password.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await Operate(Login); } };
        foreach (var line in history.Recent().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).TakeLast(150).Reverse()) activities.Add(line);
        activities.CollectionChanged += (_, _) => RefreshActivities(); RefreshActivities(); NavigateTo("tasks");
        void Responsive()
        {
            taskSearch.IsVisible = Bounds.Width >= 850;
            var count = Math.Clamp((int)((Bounds.Height - 225) / (displaySettings.CompactRows ? 72 : 88)), 1, 15);
            if (count != taskPageSize) { taskPageSize = count; BuildCards(); }
        }
        Opened += (_, _) => { DesktopTheme.Fit(this); Responsive(); }; SizeChanged += (_, _) => Responsive();
    }
    private Grid? loginShell;
    private void BuildLogin()
    {
        loginPage.Children.Clear(); loginPage.Width = double.NaN; loginPage.MaxWidth = 360; loginPage.Margin = new Thickness(35); loginPage.HorizontalAlignment = HorizontalAlignment.Stretch;
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 }; brand.Children.Add(DesktopTheme.Logo(34)); brand.Children.Add(new TextBlock { Text = ProductBrand.Desktop, FontSize = 23, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }); loginPage.Children.Add(brand);
        loginPage.Children.Add(Caption("登录服务器，开始管理你的同步任务"));
        foreach (var (title, field) in new (string, Control)[] { ("服务器地址", server), ("用户名", username), ("密码", password) }) { loginPage.Children.Add(new TextBlock { Text = title, FontSize = 12 }); loginPage.Children.Add(field); }
        loginPage.Children.Add(loginError); login.Classes.Add("accent"); loginPage.Children.Add(login);
        loginPage.Children.Add(Caption("登录后自动记住连接。可添加多个服务器，并为每个服务器设置多个同步目录。"));
        var left = new StackPanel { Spacing = 22, Margin = new Thickness(40), VerticalAlignment = VerticalAlignment.Center };
        var illustration = DesktopGlyphs.Create("tasks", 90, DesktopTheme.Teal); illustration.HorizontalAlignment = HorizontalAlignment.Left; left.Children.Add(illustration);
        left.Children.Add(new TextBlock { Text = "你的文件，\n始终相连。", FontSize = 31, FontWeight = FontWeight.SemiBold, Foreground = DesktopTheme.Ink, LineHeight = 45 });
        left.Children.Add(Caption("同步所需的目录，在每台设备上继续工作。\n双向同步  ·  按需下载  ·  最近活动"));
        var welcome = new Border { Background = Brush.Parse("#EFF8F9"), Child = left };
        loginShell = new Grid { ColumnDefinitions = new("*,430") }; loginShell.Children.Add(welcome);
        var form = new ScrollViewer { Content = loginPage, Background = Brushes.White }; Grid.SetColumn(form, 1); loginShell.Children.Add(form);
        loginPage.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) loginShell.IsVisible = loginPage.IsVisible; };
        SizeChanged += (_, _) => { var compact = Bounds.Width < 820; welcome.IsVisible = !compact; loginShell.ColumnDefinitions = new(compact ? "0,*" : "*,430"); };
    }
    private void BuildTaskPage()
    {
        taskPage.RowDefinitions = new("Auto,Auto,*,Auto");
        add.Content = ToolButton("创建", "plus", true).Content; add.Classes.Add("accent"); add.CornerRadius = new CornerRadius(3); add.Padding = new Thickness(12, 6);
        pauseSelected.Content = "暂停"; removeSelected.Content = "删除"; configureSelected.Content = "任务设置";
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 }; toolbar.Children.Add(add); toolbar.Children.Add(pauseSelected); toolbar.Children.Add(removeSelected); toolbar.Children.Add(configureSelected);
        var connectionSettings = ToolButton("连接设置", "server"); connectionSettings.Click += (_, _) => { NavigateTo("settings"); settingsTabs.SelectedIndex = 3; }; toolbar.Children.Add(connectionSettings);
        var tools = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new Thickness(0, 0, 0, 13) }; tools.Children.Add(toolbar); taskSearch.Width = 160; taskSearch.FontSize = 11; taskSearch.MinHeight = 31; Grid.SetColumn(taskSearch, 1); tools.Children.Add(taskSearch); taskPage.Children.Add(tools);
        add.Click += async (_, _) => await Operate(AddTask);
        pauseSelected.Click += async (_, _) => { if (SelectedTask is { } task) await Operate(() => Toggle(task)); };
        removeSelected.Click += async (_, _) => { if (SelectedTask is { } task) await Operate(() => Remove(task)); };
        configureSelected.Click += async (_, _) => { if (SelectedTask is { } task) await Operate(() => ConfigureTask(task)); };
        taskSearch.TextChanged += (_, _) => { taskPageIndex = 1; BuildCards(); }; taskPager.Changed += page => { taskPageIndex = page; BuildCards(); };
        var header = new Grid { ColumnDefinitions = new("28,39,*,Auto"), MinHeight = 58, Margin = new Thickness(9, 0) };
        var fold = IconButton("chevron", "展开或收起此连接", () => { collapsed = !collapsed; cards.IsVisible = !collapsed; }); header.Children.Add(fold);
        var serverIcon = DesktopGlyphs.Create("server", 28); serverIcon.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(serverIcon, 1); header.Children.Add(serverIcon);
        var description = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center }; serverTitle.FontWeight = FontWeight.SemiBold; serverTitle.FontSize = 13; serverSubtitle.FontSize = 11; serverSubtitle.Foreground = DesktopTheme.Muted; description.Children.Add(serverTitle); description.Children.Add(serverSubtitle); Grid.SetColumn(description, 2); header.Children.Add(description);
        var groupActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        groupActions.Children.Add(IconButton("pause", "暂停此连接的所有任务", () => _ = Operate(() => SetAll(false)))); groupActions.Children.Add(IconButton("play", "继续此连接的所有任务", () => _ = Operate(() => SetAll(true)))); groupActions.Children.Add(IconButton("external", "打开服务器网页", OpenServerWeb)); Grid.SetColumn(groupActions, 3); header.Children.Add(groupActions);
        var headerBorder = new Border { Background = Brush.Parse("#EBF7F8"), Child = header, IsVisible = false }; Grid.SetRow(headerBorder, 1); taskPage.Children.Add(headerBorder);
        var list = new Grid(); var scroll = new ScrollViewer { Content = cards }; list.Children.Add(scroll); empty.HorizontalAlignment = HorizontalAlignment.Center; empty.VerticalAlignment = VerticalAlignment.Center; empty.TextAlignment = TextAlignment.Center; list.Children.Add(empty); Grid.SetRow(list, 2); taskPage.Children.Add(list);
        var footer = new StackPanel { Spacing = 4, Margin = new Thickness(0, 8, 0, 8) };
        var footerLine = new Grid { ColumnDefinitions = new("*,Auto") }; selectionHint.FontSize = 11; selectionHint.Foreground = DesktopTheme.Muted; selectionHint.VerticalAlignment = VerticalAlignment.Center; footerLine.Children.Add(selectionHint); Grid.SetColumn(taskPager, 1); footerLine.Children.Add(taskPager); footer.Children.Add(footerLine);
        review.Classes.Add("link"); review.Content = "有离线删除等待确认，点击处理"; review.HorizontalAlignment = HorizontalAlignment.Left; review.Click += async (_, _) => await Review(); footer.Children.Add(review); Grid.SetRow(footer, 3); taskPage.Children.Add(footer);
    }
    private DesktopSyncTask? SelectedTask => tasks.Find(x => x.Profile.Id == selectedTask);
    private void SelectTask(Guid id)
    {
        selectedTask = id;
        if (SelectedTask is { } selected && accounts.Find(x => Belongs(selected, x)) is { } owner) ActivateAccount(owner);
        foreach (var row in taskRows) row.Value.Row.Background = row.Key == id ? Brush.Parse("#EDF8FA") : Brushes.White;
        RefreshSelection();
    }
    private void RefreshSelection()
    {
        var task = SelectedTask; var available = lifecycle.CurrentCount > 0; pauseSelected.IsEnabled = removeSelected.IsEnabled = configureSelected.IsEnabled = task != null && available; add.IsEnabled = available;
        pauseSelected.Content = task?.IsRunning == true ? "暂停" : "继续";
        selectionHint.Text = task == null ? $"{tasks.Count} 个同步任务 · 选择任务进行管理" : "已选择：" + task.Profile.Name;
    }
    private void RenderTaskRows()
    {
        cards.Children.Clear(); taskControls.Clear(); taskRows.Clear();
        if (!tasks.Any(x => x.Profile.Id == selectedTask)) selectedTask = null;
        var filtered = tasks.Where(x => (x.Profile.Name + " " + x.Profile.Remote + " " + x.Profile.Local).Contains(taskSearch.Text?.Trim() ?? "", StringComparison.OrdinalIgnoreCase)).ToList();
        taskPageIndex = taskPager.Update(filtered.Count, taskPageIndex, taskPageSize);
        var visible = filtered.Skip((taskPageIndex - 1) * taskPageSize).Take(taskPageSize).ToList();
        if (!visible.Any(x => x.Profile.Id == selectedTask)) selectedTask = null;
        foreach (var item in accounts)
        {
        cards.Children.Add(ConnectionHeader(item));
        foreach (var task in visible.Where(t => Belongs(t, item)))
        {
            var row = new Grid { ColumnDefinitions = new("44,*,110,Auto"), Margin = new Thickness(27, 0, 9, 0), MinHeight = displaySettings.CompactRows ? 71 : 87 };
            var folder = new Grid { Width = 34, Height = 34, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center }; folder.Children.Add(DesktopGlyphs.Create("folder", 29));
            var badge = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom }; folder.Children.Add(badge); row.Children.Add(folder);
            var copy = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            var title = new Grid { ColumnDefinitions = new("Auto,Auto,*"), ColumnSpacing = 5 };
            title.Children.Add(new TextBlock { Text = task.Profile.Name, FontSize = 14, FontWeight = FontWeight.Medium, MaxWidth = Bounds.Width < 850 ? 125 : 240, TextTrimming = TextTrimming.CharacterEllipsis });
            var mode = new TextBlock { Text = "（双向同步）", Foreground = DesktopTheme.Muted, FontSize = 11, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(mode, 1); title.Children.Add(mode);
            if (task.Profile.OnDemand && OperatingSystem.IsWindows()) { var cloud = DesktopGlyphs.Create("cloud", 13); cloud.HorizontalAlignment = HorizontalAlignment.Left; cloud.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(cloud, 2); title.Children.Add(cloud); }
            copy.Children.Add(title);
            var local = new Button { Content = new TextBlock { Text = task.Profile.Local, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis }, HorizontalAlignment = HorizontalAlignment.Stretch }; local.Classes.Add("link"); ToolTip.SetTip(local, task.Profile.Local); local.Click += (_, _) => { if (Directory.Exists(task.Profile.Local)) OpenFolder(task.Profile.Local); }; copy.Children.Add(local);
            var remote = new TextBlock { Text = "云端 /" + SyncProfiles.DisplayRemote(task.Profile.Remote, task.Profile.Username), FontSize = 10, Foreground = DesktopTheme.Muted, IsVisible = displaySettings.ShowFullRemotePath, TextTrimming = TextTrimming.CharacterEllipsis }; copy.Children.Add(remote); ToolTip.SetTip(copy, "云端 /" + SyncProfiles.DisplayRemote(task.Profile.Remote, task.Profile.Username) + "\n" + (task.Profile.OnDemand ? "按需下载" : "完整副本")); Grid.SetColumn(copy, 1); row.Children.Add(copy);
            var stateText = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 0) }; Grid.SetColumn(stateText, 2); row.Children.Add(stateText);
            var pause = new Button { FontSize = 11, Padding = new Thickness(8, 4), MinHeight = 27, VerticalAlignment = VerticalAlignment.Center }; pause.Click += async (_, _) => await Operate(() => Toggle(task)); Grid.SetColumn(pause, 3); row.Children.Add(pause);
            var border = new Border { Child = row, BorderBrush = DesktopTheme.Line, BorderThickness = new Thickness(0, 0, 0, 1), Background = task.Profile.Id == selectedTask ? Brush.Parse("#EDF8FA") : Brushes.White };
            border.PointerPressed += (_, _) => SelectTask(task.Profile.Id); border.PointerEntered += (_, _) => { if (selectedTask != task.Profile.Id) border.Background = Brush.Parse("#F7FAFB"); }; border.PointerExited += (_, _) => { if (selectedTask != task.Profile.Id) border.Background = Brushes.White; };
            cards.Children.Add(border); taskControls[task.Profile.Id] = (stateText, pause); taskRows[task.Profile.Id] = (border, badge);
        }
        }
        empty.Text = tasks.Count == 0 ? "还没有同步任务\n点击“创建”，选择云端文件夹和本地目录" : "没有匹配的同步任务"; empty.IsVisible = filtered.Count == 0;
        cards.IsVisible = !collapsed; RefreshStates();
    }
    private void NavigateTo(string page)
    {
        activePage = page; taskPage.IsVisible = page == "tasks"; activityPage.IsVisible = page == "activity"; notificationsPage.IsVisible = page == "notifications"; accountPage.IsVisible = page == "settings";
        foreach (var nav in navigation) { var active = nav.Key == page; nav.Value.Classes.Set("active", active); navGlyphs[nav.Key].Stroke = active ? Brushes.White : DesktopTheme.Muted; }
        if (page == "settings" && settingsTabs.SelectedIndex == 2) _ = RefreshUsage();
        if (page == "notifications") RenderNotifications();
    }
    private void RefreshShellState(string state)
    {
        currentState = state; serverTitle.Text = Uri.TryCreate(server.Text, UriKind.Absolute, out var uri) ? uri.Host : "服务器";
        serverSubtitle.Text = (server.Text ?? "") + (session == null ? "" : "  ·  " + session.User.Username);
        foreach (var task in tasks) if (taskRows.TryGetValue(task.Profile.Id, out var row))
        {
            var healthy = task.State == "connected" && task.HasSynchronized;
            row.Badge.Background = healthy ? Brush.Parse("#55B943") : task.State is "offline" or "error" ? Brush.Parse("#DA9A37") : Brush.Parse("#A0A8AE");
            row.Badge.Child = DesktopGlyphs.Create(healthy ? "check" : task.IsRunning ? "refresh" : "pause", 10, Brushes.White);
        }
        RefreshSelection(); review.IsVisible = pending.Count > 0; notice.IsVisible = !string.IsNullOrEmpty(notice.Text);
        if (tasks.Count > 0 && cards.Children.Count == 0) empty.IsVisible = true;
        RenderNotifications(); UpdateTrayHub();
    }
    private void OpenServerWeb()
    {
        if (Uri.TryCreate(server.Text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
    }
    private async Task ConfigureTask(DesktopSyncTask task)
    {
        var dialog = new Window { Title = "同步任务设置", Width = 530, Height = 360, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 13 }; var name = new TextBox { Text = task.Profile.Name, MaxLength = 100 };
        panel.Children.Add(new TextBlock { Text = "任务名称", FontWeight = FontWeight.SemiBold }); panel.Children.Add(name);
        panel.Children.Add(Caption("云端 /" + SyncProfiles.DisplayRemote(task.Profile.Remote, task.Profile.Username))); panel.Children.Add(Caption("本地 " + task.Profile.Local)); panel.Children.Add(Caption("双向同步 · " + (task.Profile.OnDemand ? "按需下载" : "完整副本")));
        panel.Children.Add(Caption("要改变同步目录或下载方式，请移除此任务后重新创建；已有文件会保留。"));
        var save = ToolButton("保存设置", "check", true); save.HorizontalAlignment = HorizontalAlignment.Right; save.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(name.Text)) dialog.Close(name.Text.Trim()); }; panel.Children.Add(save); dialog.Content = new ScrollViewer { Content = panel }; dialog.Opened += (_, _) => DesktopTheme.Fit(dialog);
        if (await dialog.ShowDialog<string?>(this) is { } title) RenameTask(task, title);
    }
    private void RenameTask(DesktopSyncTask task, string title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > 100) throw new ArgumentException("任务名称须为 1–100 个字符。");
        task.Profile = task.Profile with { Name = title.Trim() }; SetProfile(task, task.Profile.Enabled); BuildCards();
    }
}

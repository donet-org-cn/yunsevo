using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using MyPan.Contracts;
using MyPan.Sync;
using System.Net.Http.Json;

namespace MyPan.Desktop;

internal sealed class AddTaskWizard : Window
{
    private readonly CloudConnection connection;
    private readonly LoginResult session;
    private readonly string server;
    private readonly IReadOnlyList<SyncTaskProfile> existing;
    private readonly StackPanel folderPage = new() { Spacing = 12 }, localPage = new() { Spacing = 14 }, summaryPage = new() { Spacing = 16 };
    private readonly ListBox folders = new() { MinHeight = 230, MaxHeight = 320 };
    private readonly TextBox search = new() { PlaceholderText = "搜索文件夹名称或路径" };
    private readonly TextBox name = new() { PlaceholderText = "例如：工作资料" }, local = new();
    private readonly CheckBox onDemand = new() { Content = "启用按需下载", IsChecked = OperatingSystem.IsWindows(), IsEnabled = OperatingSystem.IsWindows() };
    private readonly TextBlock error = new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap }, stepTitle = new() { FontSize = 21, FontWeight = FontWeight.SemiBold };
    private readonly Button back = new() { Content = "上一步", IsEnabled = false }, next = new() { Content = "下一步" };
    private List<SyncFolder> available = [];
    private readonly ListPager pager = new();
    private int folderPageNumber = 1, requestVersion, folderTotal;
    private SyncFolder? selectedFolder;
    private readonly List<(Border Box, TextBlock Text)> stepLabels = [];
    private int step;
    private readonly CheckBox continueAdding = new() { Content = "完成后继续为此服务器添加文件夹" };
    internal bool ContinueAdding => continueAdding.IsChecked == true;
    public AddTaskWizard(CloudConnection connection, LoginResult session, string server, IReadOnlyList<SyncTaskProfile> existing)
    {
        this.connection = connection; this.session = session; this.server = server; this.existing = existing;
        Title = ProductBrand.Name + " · 添加同步任务"; Width = 690; Height = 610; MinWidth = 500; MinHeight = 450; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var content = new StackPanel { Margin = new Thickness(28), Spacing = 18 };
        var steps = new Grid { ColumnDefinitions = new("*,*,*"), ColumnSpacing = 8 };
        foreach (var (title, index) in new[] { ("1  选择云端目录", 0), ("2  同步选项", 1), ("3  确认", 2) })
        {
            var text = new TextBlock { Text = title, FontSize = 11 }; var box = new Border { Child = text, Padding = new Thickness(10, 9), CornerRadius = new CornerRadius(4) }; stepLabels.Add((box, text)); Grid.SetColumn(box, index); steps.Children.Add(box);
        }
        content.Children.Add(steps);
        content.Children.Add(stepTitle);
        folders.ItemTemplate = new FuncDataTemplate<SyncFolder>((item, _) =>
        {
            if (item == null) return new TextBlock();
            var row = new Grid { ColumnDefinitions = new("34,*"), Margin = new Thickness(8, 6) };
            var symbol = DesktopGlyphs.Create(item.Category == "private" ? "lock" : "folder", 23); symbol.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(symbol);
            var labels = new StackPanel { Spacing = 4 }; labels.Children.Add(new TextBlock { Text = "/" + SyncProfiles.DisplayRemote(item.Path, session.User.Username), FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
            labels.Children.Add(new TextBlock { Text = Category(item) + "  ·  " + Rights(item), FontSize = 11, Foreground = (item.Access & 1) == 0 ? Brushes.Gray : Brushes.SlateGray }); Grid.SetColumn(labels, 1); row.Children.Add(labels);
            return row;
        });
        var refresh = new Button { Content = "刷新目录", HorizontalAlignment = HorizontalAlignment.Left };
        refresh.Click += async (_, _) => await Refresh();
        search.TextChanged += async (_, _) => { folderPageNumber = 1; var version = ++requestVersion; await Task.Delay(180); if (version == requestVersion) await Refresh(); };
        folders.SelectionChanged += (_, _) => { if (folders.SelectedItem is SyncFolder selected) selectedFolder = selected; };
        pager.Changed += async page => { folderPageNumber = page; await Refresh(); };
        folderPage.Children.Add(new TextBlock { Text = "选择此账户可访问的文件夹。子文件夹会继承对应目录类型，也可以单独选择。", TextWrapping = TextWrapping.Wrap });
        folderPage.Children.Add(search); folderPage.Children.Add(folders); folderPage.Children.Add(pager); folderPage.Children.Add(refresh);
        localPage.Children.Add(new TextBlock { Text = "任务名称" }); localPage.Children.Add(name);
        localPage.Children.Add(new TextBlock { Text = "本地同步目录" });
        var localRow = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 10 }; localRow.Children.Add(local);
        var browse = new Button { Content = "浏览…" }; Grid.SetColumn(browse, 1); localRow.Children.Add(browse); localPage.Children.Add(localRow);
        browse.Click += async (_, _) =>
        {
            var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择空的本地同步目录", AllowMultiple = false });
            if (result.FirstOrDefault()?.TryGetLocalPath() is { } path) local.Text = path;
        };
        localPage.Children.Add(onDemand);
        localPage.Children.Add(new TextBlock { Text = OperatingSystem.IsWindows() ? "按需下载：文件显示在资源管理器中，打开时下载内容。关闭后将下载完整本地副本。" : "当前平台同步完整本地副本；按需下载目前支持 Windows。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray });
        localPage.Children.Add(new TextBlock { Text = "首次同步请选择空目录。在线删除会同步删除云端文件并进入回收站；退出、暂停或断线期间的删除需要确认。", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(folderPage); content.Children.Add(localPage); content.Children.Add(summaryPage); content.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };
        var cancel = new Button { Content = "取消" }; cancel.Click += (_, _) => Close();
        back.Click += (_, _) => { step--; ShowStep(); }; next.Click += async (_, _) => await Next();
        buttons.Children.Add(cancel); buttons.Children.Add(back); buttons.Children.Add(next); next.Classes.Add("accent");
        content.Margin = new Thickness(28, 24, 28, 12);
        buttons.Margin = new Thickness(28, 12, 28, 20);
        var layout = new Grid { RowDefinitions = new("*,Auto") };
        layout.Children.Add(new ScrollViewer { Content = content }); Grid.SetRow(buttons, 1); layout.Children.Add(buttons);
        Content = layout; ShowStep();
        Opened += async (_, _) => { DesktopTheme.Fit(this); await Refresh(); };
    }
    internal static string Category(SyncFolder item) => item.Category switch { "private" => "私有", "public" => "公共", "assigned" => "分配给我", _ => "路径分组" };
    internal static string Rights(SyncFolder item) => (item.Access & 1) == 0 ? "仅可展开，不能同步整个目录" : (item.Access & 2) != 0 ? "可读写" : "只读";
    private void Filter()
    {
        folders.ItemsSource = available;
        folders.SelectedItem = available.Find(x => x.Id == selectedFolder?.Id);
    }
    private async Task Refresh()
    {
        next.IsEnabled = false;
        try
        {
            var version = ++requestVersion;
            var result = await connection.Http.GetFromJsonAsync("api/browser/folders?page=" + folderPageNumber + "&pageSize=20&query=" + Uri.EscapeDataString(search.Text?.Trim() ?? ""), ApiJson.Default.PagedResultSyncFolder) ?? throw new InvalidDataException("目录响应为空。");
            if (version != requestVersion) return;
            available = result.Items; folderTotal = result.Total; folderPageNumber = pager.Update(result.Total, result.Page, result.PageSize); Filter(); error.Text = available.Count == 0 ? "没有匹配的目录，或尚未分配文件夹。" : "";
        }
        catch (Exception ex) { error.Text = ex.Message; }
        finally { next.IsEnabled = true; }
    }
    private void ShowStep()
    {
        folderPage.IsVisible = step == 0; localPage.IsVisible = step == 1; summaryPage.IsVisible = step == 2;
        for (var index = 0; index < stepLabels.Count; index++) { stepLabels[index].Box.Background = index == step ? Brush.Parse("#E6F6F8") : Brush.Parse("#F5F7F8"); stepLabels[index].Text.Foreground = index == step ? DesktopTheme.Teal : DesktopTheme.Muted; }
        back.IsEnabled = step > 0; next.Content = step == 2 ? "完成并开始同步" : "下一步";
        stepTitle.Text = step switch { 0 => "选择云端文件夹", 1 => "设置本地同步目录", _ => "确认同步任务" }; error.Text = "";
    }
    private SyncTaskProfile Profile()
    {
        var selected = selectedFolder;
        if (selected == null || (selected.Access & 1) == 0) throw new ArgumentException("请选择可读取的云端文件夹；路径分组不能作为同步根目录。");
        if (string.IsNullOrWhiteSpace(local.Text)) throw new ArgumentException("请选择本地同步目录。");
        var profile = new SyncTaskProfile(Guid.NewGuid(), server, session.User.Username, session.User.Id, name.Text?.Trim() ?? "",
            selected.Path, Path.GetFullPath(local.Text.Trim()), onDemand.IsChecked == true, true, selected.Id);
        SyncProfiles.ValidateNew(profile, existing);
        var state = SyncProfiles.StatePath(Environment.GetEnvironmentVariable("MYPAN_DESKTOP_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyPanDesktop"), profile);
        if (!File.Exists(state) && Directory.Exists(profile.Local) && Directory.EnumerateFileSystemEntries(profile.Local).Any()) throw new ArgumentException("首次同步请选择空的本地目录；已有文件会保留。");
        return profile;
    }
    private async Task Next()
    {
        try
        {
            error.Text = "";
            if (step == 0)
            {
                var selected = selectedFolder;
                if (selected == null || (selected.Access & 1) == 0) throw new ArgumentException("请选择可读取的云端文件夹。");
                name.Text = Path.GetFileName(SyncProfiles.DisplayRemote(selected.Path, session.User.Username));
                local.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "MyPanFiles", new Uri(server).Host, name.Text);
                step++; ShowStep(); return;
            }
            var profile = Profile();
            if (step == 1)
            {
                summaryPage.Children.Clear();
                foreach (var (label, value) in new[] { ("服务器", server), ("任务名称", profile.Name), ("云端目录", "/" + SyncProfiles.DisplayRemote(profile.Remote, profile.Username)), ("本地目录", profile.Local), ("下载方式", profile.OnDemand ? "按需下载" : "完整副本"), ("同步方向", "双向同步") })
                {
                    var row = new Grid { ColumnDefinitions = new("84,*") }; row.Children.Add(new TextBlock { Text = label, Foreground = DesktopTheme.Muted, FontSize = 12 }); var data = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, FontSize = 13 }; Grid.SetColumn(data, 1); row.Children.Add(data); summaryPage.Children.Add(row);
                }
                summaryPage.Children.Add(new TextBlock { Text = "在线删除自动同步到云端回收站；离线期间的本地删除需确认。", TextWrapping = TextWrapping.Wrap, Foreground = DesktopTheme.Muted, FontSize = 12, Margin = new Thickness(0, 12) });
                summaryPage.Children.Add(continueAdding);
                step++; ShowStep(); return;
            }
            next.IsEnabled = false;
            var fresh = await connection.Http.GetFromJsonAsync("api/sync-folders", ApiJson.Default.ListSyncFolder) ?? [];
            if (!fresh.Any(x => x.Id == profile.RemoteId && x.Path == profile.Remote && (x.Access & 1) != 0)) throw new InvalidOperationException("云端目录已变更或权限已收回，请重新选择。");
            Close(profile);
        }
        catch (Exception ex) { error.Text = ex.Message; }
        finally { next.IsEnabled = true; }
    }
    internal async Task Probe(string cloud, string localPath, bool demand, string screenshots)
    {
        await Refresh();
        while (!available.Any(x => x.Path == cloud) && folderPageNumber * 20 < folderTotal) { folderPageNumber++; await Refresh(); }
        folders.SelectedItem = available.Single(x => x.Path == cloud);
        await DesktopScreenshots.Save(this, Path.Combine(screenshots, "wizard-folders.png"));
        await Next(); if (step != 1) throw new InvalidOperationException(error.Text);
        local.Text = localPath; onDemand.IsChecked = demand;
        await DesktopScreenshots.Save(this, Path.Combine(screenshots, "wizard-local.png"));
        await Next(); if (step != 2) throw new InvalidOperationException(error.Text);
        await DesktopScreenshots.Save(this, Path.Combine(screenshots, "wizard-confirm.png"));
        await Next(); if (!string.IsNullOrEmpty(error.Text)) throw new InvalidOperationException(error.Text);
    }
}

using System.Net;
using System.Net.Http.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Yunsevo.Contracts;
using Yunsevo.Sync;

namespace Yunsevo.Desktop;

internal sealed partial class MainWindow
{
    private readonly List<DesktopAccount> accounts = [];
    private DesktopAccount? activeAccount;
    private readonly DispatcherTimer reconnectTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private static bool Belongs(DesktopSyncTask task, DesktopAccount item) => task.Profile.UserId == item.Saved.UserId && SyncProfiles.SameServer(task.Profile.Server, item.Saved.Server);
    private void SaveAccounts() => RememberedAccounts.Save(home, accounts.Where(x => !x.NeedsLogin && x.Saved.Token.Length > 0).Select(x => x.Saved));
    private void LoadRememberedAccounts()
    {
        if (Program.WorkflowProbe) return;
        try
        {
            foreach (var saved in RememberedAccounts.Load(home))
            {
                _ = new Uri(saved.Server, UriKind.Absolute);
                var item = new DesktopAccount(saved); accounts.Add(item);
                foreach (var profile in preferences.Tasks.Where(x => x.UserId == saved.UserId && SyncProfiles.SameServer(x.Server, saved.Server))) CreateRuntime(profile);
            }
            if (accounts.Count > 0) { ActivateAccount(accounts[0]); ShowWorkspace(); BuildCards(); }
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException or System.ComponentModel.Win32Exception or FormatException)
        { loginError.Text = "无法恢复已保存的登录，请重新登录：" + ex.Message; }
        reconnectTimer.Tick += async (_, _) => await Operate(RestoreAccounts); reconnectTimer.Start();
    }
    private void ActivateAccount(DesktopAccount item)
    {
        activeAccount = item; session = item.Session; connection = item.Connection;
        server.Text = item.Saved.Server; username.Text = item.Saved.Username;
        account.Text = (item.Session?.User.DisplayName ?? item.Saved.Username) + "（" + item.Saved.Username + "）\n" + item.Saved.Server + "\n" + item.Status;
        ClearUsage();
    }
    private void ShowWorkspace()
    {
        loginPage.IsVisible = false; workspace.IsVisible = true;
        NavigateTo("tasks");
    }
    private async Task<DesktopAccount> ConnectAccount(string address, string user, string secret)
    {
        address = address.Trim().TrimEnd('/');
        var client = new CloudConnection(address);
        LoginResult result;
        try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); result = await client.Login(user, secret, timeout.Token); }
        catch { client.Dispose(); throw; }
        var item = accounts.Find(x => x.Saved.UserId == result.User.Id && SyncProfiles.SameServer(x.Saved.Server, address));
        if (item == null) { item = new(new(Guid.NewGuid(), address, result.User.Username, result.User.Id, result.AccessToken)); accounts.Add(item); }
        else
        {
            foreach (var task in tasks.Where(t => Belongs(t, item))) await task.Stop();
            item.Connection?.Dispose();
            item.Saved = item.Saved with { Token = result.AccessToken };
        }
        item.Connection = client; item.Session = result; item.NeedsLogin = false; item.Status = "已连接 · 自动登录已开启"; item.LastChecked = DateTimeOffset.UtcNow;
        preferences = preferences with { Tasks = preferences.Tasks.Select(x => x.UserId == Guid.Empty && x.Username.Equals(result.User.Username, StringComparison.OrdinalIgnoreCase) && SyncProfiles.SameServer(x.Server, address) ? x with { UserId = result.User.Id } : x).ToList() };
        ActivateAccount(item); Save(); SaveAccounts();
        foreach (var profile in preferences.Tasks.Where(x => SyncProfiles.Matches(x, address, result.User) && !tasks.Any(t => t.Profile.Id == x.Id))) CreateRuntime(profile);
        ShowWorkspace(); BuildCards(); Log("[" + item + "] 已登录，登录状态已保存。");
        foreach (var task in tasks.Where(x => Belongs(x, item) && x.Profile.Enabled)) await StartTask(task);
        RefreshStates(); return item;
    }
    private async Task RestoreAccounts()
    {
        // Resolve servers independently and concurrently so an unreachable server does not block another.
        var candidates = accounts.Where(x => !x.NeedsLogin && (x.Session == null || DateTimeOffset.UtcNow - x.LastChecked > TimeSpan.FromHours(12))).ToArray();
        await Task.WhenAll(candidates.Select(RestoreAccount));
        if (activeAccount != null) ActivateAccount(activeAccount);
        BuildCards(); RefreshStates();
    }
    private async Task RestoreAccount(DesktopAccount item)
    {
        try
        {
            item.Connection ??= new(item.Saved.Server, item.Saved.Token);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var restored = await item.Connection.Resume(timeout.Token);
            if (restored.User.Id != item.Saved.UserId) throw new InvalidDataException("账户身份发生变化，请重新登录。");
            var wasDisconnected = item.Session == null;
            item.Session = restored; item.LastChecked = DateTimeOffset.UtcNow; item.Status = "已连接 · 自动登录已开启";
            if (wasDisconnected)
            {
                foreach (var task in tasks.Where(x => Belongs(x, item) && x.Profile.Enabled)) await StartTask(task);
                Log("[" + item + "] 已自动登录并恢复同步。");
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) { await ExpireAccount(item); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        { item.Status = "服务器暂不可达，将自动重连"; }
    }
    private async Task ExpireAccount(DesktopAccount item)
    {
        foreach (var task in tasks.Where(t => Belongs(t, item))) await task.Stop();
        item.Session = null; item.NeedsLogin = true; item.Saved = item.Saved with { Token = "" }; item.Status = "登录已失效，点击重新登录";
        item.Connection?.Dispose(); item.Connection = null; SaveAccounts();
        if (activeAccount == item) ActivateAccount(item);
        BuildCards(); Log("[" + item + "] 登录已失效，请重新登录；其他服务器继续同步。");
    }
    private async Task DisconnectAccount(DesktopAccount item)
    {
        foreach (var task in tasks.Where(t => Belongs(t, item)).ToArray())
        {
            await task.Stop(); tasks.Remove(task);
            foreach (var key in pending.Keys.Where(x => x.Task == task.Profile.Id).ToArray()) { pending.Remove(key); decisions.Remove(key); }
        }
        try { if (item.Connection != null) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)); using var response = await item.Connection.Http.PostAsync("api/auth/logout", null, timeout.Token); } }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { }
        item.Connection?.Dispose(); accounts.Remove(item); SaveAccounts();
        activeAccount = null; session = null; connection = null;
        if (accounts.Count > 0) ActivateAccount(accounts[0]);
        else { ClearUsage(); workspace.IsVisible = false; loginPage.IsVisible = true; }
        BuildCards(); RefreshStates(); Log("已退出 " + item + "，本地和云端文件及任务配置已保留。");
    }
    private async Task<DesktopAccount?> ChooseConnection()
    {
        var dialog = new Window { Title = "创建同步任务", Width = 480, Height = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new(26), Spacing = 16 };
        panel.Children.Add(new TextBlock { Text = "选择服务器连接", FontSize = 20, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(Caption("每台服务器可添加多个同步文件夹，连接和下载方式分别管理。"));
        var list = new ComboBox { ItemsSource = accounts.ToArray(), SelectedItem = activeAccount, HorizontalAlignment = HorizontalAlignment.Stretch }; panel.Children.Add(list);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var create = ToolButton("添加服务器", "plus"); create.Click += (_, _) => dialog.Close(Guid.Empty); actions.Children.Add(create);
        var next = new Button { Content = "选择文件夹" }; next.Classes.Add("accent"); next.Click += (_, _) => { if (list.SelectedItem is DesktopAccount chosen) dialog.Close(chosen.Saved.Id); }; actions.Children.Add(next); panel.Children.Add(actions); dialog.Content = panel;
        var choice = await dialog.ShowDialog<Guid?>(this);
        if (!choice.HasValue) return null;
        var target = choice == Guid.Empty ? await AddServerDialog() : accounts.Find(x => x.Saved.Id == choice);
        if (target is { NeedsLogin: true }) target = await AddServerDialog(target);
        return target;
    }
    private async Task<DesktopAccount?> AddServerDialog(DesktopAccount? existing = null)
    {
        var dialog = new Window { Title = existing == null ? "添加服务器连接" : "重新登录服务器", Width = 460, Height = 470, MinHeight = 380, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new(26), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = dialog.Title, FontSize = 21, FontWeight = FontWeight.SemiBold });
        var address = new TextBox { Text = existing?.Saved.Server ?? "https://", IsReadOnly = existing != null }; var user = new TextBox { Text = existing?.Saved.Username ?? "", IsReadOnly = existing != null }; var secret = new TextBox { PasswordChar = '●' };
        foreach (var (label, field) in new[] { ("服务器地址", address), ("用户名", user), ("密码", secret) }) { panel.Children.Add(Caption(label)); panel.Children.Add(field); }
        panel.Children.Add(Caption("登录状态会加密保存，下次启动自动连接。")); var error = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap }; panel.Children.Add(error);
        var submit = new Button { Content = "登录并连接", HorizontalAlignment = HorizontalAlignment.Stretch }; submit.Classes.Add("accent"); panel.Children.Add(submit);
        submit.Click += async (_, _) =>
        {
            submit.IsEnabled = false;
            try { var item = await ConnectAccount(address.Text ?? "", user.Text ?? "", secret.Text ?? ""); secret.Text = ""; dialog.Close(item); }
            catch (Exception ex) { error.Text = ex.Message; } finally { submit.IsEnabled = true; }
        };
        dialog.Content = new ScrollViewer { Content = panel }; dialog.Opened += (_, _) => DesktopTheme.Fit(dialog);
        return await dialog.ShowDialog<DesktopAccount?>(this);
    }
    private async Task AddFolders(DesktopAccount target)
    {
        ActivateAccount(target);
        if (target.NeedsLogin) { if (await AddServerDialog(target) == null) return; }
        else if (target.Session == null) await RestoreAccount(target);
        if (target.Connection == null || target.Session == null) throw new IOException("此服务器尚未连接，请连接成功后再添加目录。");
        bool again;
        do
        {
            var wizard = new AddTaskWizard(target.Connection, target.Session, target.Saved.Server, preferences.Tasks);
            var profile = await wizard.ShowDialog<SyncTaskProfile?>(this);
            if (profile == null) return;
            await AddProfile(profile); again = wizard.ContinueAdding;
        } while (again);
    }
    private async Task SetConnectionTasks(DesktopAccount item, bool enabled)
    {
        foreach (var task in tasks.Where(t => Belongs(t, item))) { SetProfile(task, enabled); if (enabled) await StartTask(task); else await task.Stop(); }
        RefreshStates();
    }
    private Border ConnectionHeader(DesktopAccount item)
    {
        var row = new Grid { ColumnDefinitions = new("31,*,Auto"), MinHeight = 58, Margin = new(12, 0) };
        var symbol = DesktopGlyphs.Create("server", 24); symbol.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(symbol);
        var copy = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        copy.Children.Add(new TextBlock { Text = item.ToString(), FontWeight = FontWeight.SemiBold, FontSize = 13 }); copy.Children.Add(Caption(item.Saved.Server + " · " + item.Status)); Grid.SetColumn(copy, 1); row.Children.Add(copy);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(IconButton("plus", "添加同步文件夹", () => _ = Operate(() => AddFolders(item))));
        actions.Children.Add(IconButton("pause", "暂停此服务器的任务", () => _ = Operate(() => SetConnectionTasks(item, false))));
        actions.Children.Add(IconButton(item.NeedsLogin ? "lock" : "play", item.NeedsLogin ? "重新登录" : "继续此服务器的任务", () => _ = Operate(async () => { if (item.NeedsLogin) await AddServerDialog(item); else await SetConnectionTasks(item, true); })));
        actions.Children.Add(IconButton("settings", "管理此服务器连接", () => { ActivateAccount(item); NavigateTo("settings"); settingsTabs.SelectedIndex = 3; }));
        Grid.SetColumn(actions, 2); row.Children.Add(actions);
        var border = new Border { Background = Avalonia.Media.Brush.Parse("#EBF7F8"), Child = row, Margin = new(0, 4, 0, 0) };
        border.PointerPressed += (_, _) => ActivateAccount(item); return border;
    }
}

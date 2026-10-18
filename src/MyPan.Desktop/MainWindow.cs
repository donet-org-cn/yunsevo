using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MyPan.Contracts;
using MyPan.Sync;

namespace MyPan.Desktop;

internal sealed partial class MainWindow : Window
{
    private readonly string home = Environment.GetEnvironmentVariable("MYPAN_DESKTOP_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyPanDesktop");
    private readonly TextBox server = new() { Name = "Server", PlaceholderText = "https://你的服务器" }, username = new() { Name = "Username" }, password = new() { Name = "Password", PasswordChar = '●' };
    private readonly Button login = new() { Name = "Login", Content = "登录", HorizontalAlignment = HorizontalAlignment.Stretch }, add = new() { Name = "AddTask", Content = "＋ 添加同步任务" };
    private readonly TextBlock loginError = new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap }, headline = new() { FontSize = 23, FontWeight = FontWeight.SemiBold }, status = new() { Foreground = Brushes.SlateGray }, account = new(), notice = new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel loginPage = new() { Width = 380, Spacing = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid workspace = new() { ColumnDefinitions = new("170,*"), IsVisible = false };
    private readonly Grid taskPage = new(), activityPage = new() { IsVisible = false }, accountPage = new() { IsVisible = false };
    private readonly StackPanel cards = new();
    private readonly TextBlock empty = new() { Text = "还没有同步任务\n点击“添加同步任务”，选择云端文件夹与本地目录。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new(0, 18) };
    private readonly ObservableCollection<string> activities = [];
    private readonly TrayIcon tray = new();
    private readonly ActivityLog history;
    private readonly List<DesktopSyncTask> tasks = [];
    private readonly Dictionary<Guid, (TextBlock State, Button Pause)> taskControls = [];
    private readonly Dictionary<(Guid Task, string Path), DeletionRequest> pending = [];
    private readonly Dictionary<(Guid Task, string Path), DeletionChoice> decisions = [];
    private readonly Button review = new() { Content = "处理待确认的删除", IsEnabled = false };
    private readonly SemaphoreSlim lifecycle = new(1), deletionDialog = new(1);
    private ClientPreferences preferences;
    private CloudConnection? connection;
    private LoginResult? session;
    private bool closing;
    private int taskPageIndex = 1, taskPageSize = 6, activityPageIndex = 1;
    private readonly ListPager taskPager = new(), activityPager = new();
    private readonly TextBox taskSearch = new() { PlaceholderText = "搜索任务或文件夹", Width = 200 };
    private readonly Dictionary<string, Button> navigation = [];

    private string PreferencesFile => Path.Combine(home, "preferences.json");
    public MainWindow()
    {
        history = new(Path.Combine(home, "activity"));
        Title = ProductBrand.Desktop; Width = 1000; Height = 720; MinWidth = 680; MinHeight = 500; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        preferences = new("http://localhost:5080", "admin", []);
        try { preferences = SyncProfiles.Load(PreferencesFile); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
        { loginError.Text = "读取同步配置失败：" + ex.Message; login.IsEnabled = false; }
        server.Text = preferences.Server; username.Text = preferences.Username;
        BuildShell();
        InitializeTray();
        LoadRememberedAccounts();
        RefreshStates();
        Closing += (_, e) => { if (closing) return; e.Cancel = true; if (displaySettings.CloseToTray) Hide(); else _ = Operate(Exit); };
        if (Program.WorkflowProbe) Opened += async (_, _) => await RunWorkflowProbe();
        else Opened += async (_, _) => await Operate(RestoreAccounts);
        if (Program.RestoreProbe) Opened += async (_, _) => await RunRestoreProbe();
    }
    private async Task Operate(Func<Task> action)
    {
        if (!await lifecycle.WaitAsync(0)) return;
        try { notice.Text = ""; notice.IsVisible = false; await action(); }
        catch (Exception ex) { notice.Text = ex.Message; Log(ex.Message); }
        finally { lifecycle.Release(); notice.IsVisible = !string.IsNullOrEmpty(notice.Text); RefreshSelection(); }
    }
    private void ShowPanel() { trayHub?.Hide(); Show(); WindowState = WindowState.Normal; Activate(); }
    private static void OpenFolder(string path)
    {
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "explorer.exe" : OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false };
        start.ArgumentList.Add(path); Process.Start(start);
    }
    private void Log(string text)
    {
        history.Write(text);
        Dispatcher.UIThread.Post(() => { activities.Insert(0, DateTime.Now.ToString("HH:mm:ss") + "  " + text); while (activities.Count > 1000) activities.RemoveAt(activities.Count - 1); });
    }
    private void RefreshStates()
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(RefreshStates); return; }
        foreach (var task in tasks)
            if (taskControls.TryGetValue(task.Profile.Id, out var controls))
            { controls.State.Text = task.Message; controls.State.Foreground = task.State is "offline" or "error" ? Brushes.DarkOrange : task.State == "connected" ? Brushes.SeaGreen : Brushes.SlateGray; controls.Pause.Content = task.IsRunning ? "暂停" : "继续"; }
        var state = accounts.Count == 0 ? "signed-out" : accounts.All(x => x.NeedsLogin) ? "signed-out" : pending.Count > 0 || tasks.Any(x => x.State == "error") || accounts.Any(x => x.NeedsLogin) ? "error" : accounts.Any(x => x.Session == null) || tasks.Any(x => x.IsRunning && x.State == "offline") ? "offline" : tasks.Any(x => x.IsRunning && x.State == "connected") ? "connected" : "paused";
        headline.Text = accounts.Count == 0 ? "登录以连接云盘" : state == "offline" ? "连接已断开，正在重试" : tasks.Count == 0 ? "创建你的第一个同步任务" : state switch { "connected" => "文件正在保持同步", "error" => "有项目需要处理", "signed-out" => "登录已失效", _ => "同步已暂停" };
        status.Text = accounts.Count == 0 ? "尚未登录" : $"{accounts.Count} 个服务器连接  ·  {tasks.Count} 个同步任务  ·  {tasks.Count(x => x.IsRunning)} 个运行中";
        RefreshShellState(state);
        Icon = CloudIcons.Create(state); tray.Icon = Icon; tray.ToolTipText = ProductBrand.Name + " · " + headline.Text; empty.IsVisible = tasks.Count == 0; review.IsEnabled = pending.Count > 0;
        foreach (var item in accounts.Where(x => x.Session != null && !x.NeedsLogin && tasks.Any(t => Belongs(t, x) && t.IsRunning && t.State == "signed-out")))
        {
            var rejectedToken = item.Session!.AccessToken;
            item.NeedsLogin = true;
            Dispatcher.UIThread.Post(async () =>
            {
                await lifecycle.WaitAsync();
                try { if (item.NeedsLogin && item.Session?.AccessToken == rejectedToken) await ExpireAccount(item); }
                finally { lifecycle.Release(); RefreshStates(); }
            });
        }
    }
    private void Save()
    {
        preferences = preferences with { Server = server.Text!.Trim().TrimEnd('/'), Username = username.Text!.Trim() };
        SyncProfiles.Save(PreferencesFile, preferences);
    }
    private async Task Login()
    {
        login.IsEnabled = false; loginError.Text = "";
        try
        {
            await ConnectAccount(server.Text?.Trim() ?? "", username.Text?.Trim() ?? "", password.Text ?? "");
            password.Text = "";
        }
        catch (Exception ex) { loginError.Text = ex.Message; Log(ex.Message); }
        finally { login.IsEnabled = true; RefreshStates(); }
    }
    private void CreateRuntime(SyncTaskProfile profile) => tasks.Add(new(profile, home, (task, message) => Log("[" + task.Profile.Name + "] " + message), RefreshStates));
    private void BuildCards() => RenderTaskRows();
    private async Task AddTask()
    {
        var target = await ChooseConnection();
        if (target != null) await AddFolders(target);
    }
    private async Task AddProfile(SyncTaskProfile profile)
    {
        SyncProfiles.ValidateNew(profile, preferences.Tasks);
        preferences = preferences with { Tasks = [.. preferences.Tasks, profile] }; Save(); CreateRuntime(profile); BuildCards(); await StartTask(tasks[^1]);
    }
    private Task StartTask(DesktopSyncTask task) => accounts.Find(x => Belongs(task, x))?.Session is { } loginSession
        ? task.Start(loginSession, (request, ct) => Confirm(task, request, ct)) : Task.CompletedTask;
    private void SetProfile(DesktopSyncTask task, bool enabled)
    {
        task.Profile = task.Profile with { Enabled = enabled };
        preferences = preferences with { Tasks = preferences.Tasks.Select(x => x.Id == task.Profile.Id ? task.Profile : x).ToList() }; Save();
    }
    private async Task Toggle(DesktopSyncTask task)
    {
        if (task.IsRunning) { SetProfile(task, false); await task.Stop(); Log("[" + task.Profile.Name + "] 已暂停。"); }
        else { SetProfile(task, true); await StartTask(task); }
        RefreshStates();
    }
    private async Task SetAll(bool enabled)
    {
        foreach (var task in tasks) { SetProfile(task, enabled); if (enabled) await StartTask(task); else await task.Stop(); }
        RefreshStates();
    }
    private async Task Remove(DesktopSyncTask task)
    {
        var dialog = new Window { Title = "移除同步任务", Width = 490, Height = 250, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var content = new StackPanel { Margin = new(24), Spacing = 18 };
        content.Children.Add(new TextBlock { Text = "移除“" + task.Profile.Name + "”？\n将停止同步并保留本地目录、同步记录和云端文件。按需下载的占位文件需重新添加此任务后才能继续下载。", TextWrapping = TextWrapping.Wrap });
        var yes = new Button { Content = "移除任务" }; yes.Click += (_, _) => dialog.Close(true); content.Children.Add(yes); dialog.Content = content;
        if (!await dialog.ShowDialog<bool>(this)) return;
        await task.Stop(); preferences = preferences with { Tasks = preferences.Tasks.Where(x => x.Id != task.Profile.Id).ToList() }; Save(); tasks.Remove(task);
        foreach (var key in pending.Keys.Where(x => x.Task == task.Profile.Id).ToArray()) { pending.Remove(key); decisions.Remove(key); }
        BuildCards(); Log("已移除任务：" + task.Profile.Name);
    }
    private async Task Logout()
    {
        if (activeAccount != null) await DisconnectAccount(activeAccount);
    }
    private async Task<DeletionChoice> Confirm(DesktopSyncTask task, DeletionRequest request, CancellationToken ct)
    {
        if (request.DetectedWhileOnline) return DeletionChoice.DeleteCloud;
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var key = (task.Profile.Id, request.Path);
            if (decisions.Remove(key, out var choice)) return choice;
            if (pending.ContainsKey(key)) return DeletionChoice.Defer;
            pending[key] = request; RefreshStates();
            var result = await ShowDeletion(task, request, ct);
            if (result != DeletionChoice.Defer) pending.Remove(key);
            RefreshStates(); return result;
        });
    }
    private async Task Review()
    {
        review.IsEnabled = false;
        foreach (var (key, request) in pending.ToArray())
        {
            var task = tasks.Find(x => x.Profile.Id == key.Task); if (task == null) continue;
            var choice = await ShowDeletion(task, request, CancellationToken.None);
            if (choice != DeletionChoice.Defer) { decisions[key] = choice; pending.Remove(key); }
        }
        RefreshStates();
    }
    private async Task<DeletionChoice> ShowDeletion(DesktopSyncTask task, DeletionRequest request, CancellationToken ct)
    {
        try { await deletionDialog.WaitAsync(ct); } catch (OperationCanceledException) { return DeletionChoice.Defer; }
        try
        {
            if (ct.IsCancellationRequested || accounts.Find(x => Belongs(task, x))?.Session == null) return DeletionChoice.Defer;
            ShowPanel();
            var dialog = new Window { Title = "确认离线期间的本地删除", Width = 600, Height = 310, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var content = new StackPanel { Margin = new(24), Spacing = 18 };
            content.Children.Add(new TextBlock { Text = "任务：" + task.Profile.Name + "\n" + (request.DetectedAtStartup ? "启动任务后发现本地项目已删除：" : "断线期间发现本地项目已删除：") + "\n" + request.Path, FontSize = 16, TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = "也删除云端：其他设备同步删除，文件进入云端回收站。\n仅本地删除：保留云端文件，本设备暂不重新下载。", TextWrapping = TextWrapping.Wrap });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            foreach (var (label, choice) in new[] { ("也删除云端", DeletionChoice.DeleteCloud), ("仅本地删除", DeletionChoice.LocalOnly), ("稍后处理", DeletionChoice.Defer) })
            { var button = new Button { Content = label }; button.Click += (_, _) => dialog.Close(choice); buttons.Children.Add(button); }
            content.Children.Add(buttons); dialog.Content = content;
            using var registration = ct.Register(() => Dispatcher.UIThread.Post(() => dialog.Close(DeletionChoice.Defer)));
            var result = await dialog.ShowDialog<DeletionChoice?>(this) ?? DeletionChoice.Defer;
            Log("[" + task.Profile.Name + "] 本地删除确认：" + request.Path + " · " + result); return result;
        }
        finally { deletionDialog.Release(); }
    }
    private void RefreshActivities() => RenderActivityRows();
    private async Task ChangeLoginPassword()
    {
        if (connection == null) return;
        var dialog = new Window { Title = ProductBrand.Name + " · 账户安全", Width = 430, Height = 440, MinHeight = 340, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new(24), Spacing = 12 }; var current = new TextBox { PasswordChar = '●' }; var next = new TextBox { PasswordChar = '●' }; var confirm = new TextBox { PasswordChar = '●' }; var error = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(new TextBlock { Text = "修改登录密码", FontSize = 20, FontWeight = FontWeight.SemiBold });
        foreach (var (title, input) in new[] { ("当前密码", current), ("新密码（12–256 个字符）", next), ("确认新密码", confirm) }) { panel.Children.Add(new TextBlock { Text = title, FontSize = 12 }); panel.Children.Add(input); }
        panel.Children.Add(error); var save = new Button { Content = "保存并重新登录", HorizontalAlignment = HorizontalAlignment.Right }; save.Classes.Add("accent"); panel.Children.Add(save);
        save.Click += async (_, _) => { save.IsEnabled = false; try { if (next.Text != confirm.Text) throw new ArgumentException("两次新密码不一致。"); using var response = await connection.Http.PostAsJsonAsync("api/auth/password", new ChangePassword(current.Text ?? "", next.Text ?? ""), ApiJson.Default.ChangePassword); await CloudConnection.Check(response, default); dialog.Close(true); } catch (Exception ex) { error.Text = ex.Message; } finally { save.IsEnabled = true; } };
        dialog.Content = new ScrollViewer { Content = panel }; dialog.Opened += (_, _) => DesktopTheme.Fit(dialog);
        if (await dialog.ShowDialog<bool>(this)) { await Logout(); loginError.Text = "密码已修改，请重新登录。"; }
    }
    private async Task Exit()
    {
        foreach (var task in tasks) await task.Stop();
        reconnectTimer.Stop(); foreach (var item in accounts) item.Connection?.Dispose(); closing = true; trayHub?.Close(); tray.Dispose(); Close(); (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
    private async Task RunWorkflowProbe()
    {
        var checks = 0; var exitCode = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; Log("UI PASS: " + message); }
        async Task Until(Func<Task<bool>> condition)
        {
            var timeout = Stopwatch.StartNew();
            while (timeout.Elapsed < TimeSpan.FromSeconds(20)) { if (await condition()) return; await Task.Delay(100); }
            throw new TimeoutException("Desktop workflow did not complete.");
        }
        try
        {
            Directory.CreateDirectory(home);
            Check(loginPage.IsVisible && !workspace.IsVisible && tasks.Count == 0, "Only account login is shown before authentication");
            await DesktopScreenshots.Save(this, Path.Combine(home, "login.png"));
            server.Text = Environment.GetEnvironmentVariable("MYPAN_PROBE_SERVER"); username.Text = Environment.GetEnvironmentVariable("MYPAN_USERNAME"); password.Text = Environment.GetEnvironmentVariable("MYPAN_PASSWORD");
            await Login(); Check(session != null && workspace.IsVisible && !loginPage.IsVisible, "Successful login opens task management");
            var folders = await connection!.Http.GetFromJsonAsync("api/sync-folders", ApiJson.Default.ListSyncFolder) ?? [];
            Check(new[] { "private", "public", "assigned" }.All(category => folders.Any(x => x.Category == category)), "Cloud folder selector receives private, public and assigned categories");
            for (var index = 0; index < 2; index++)
            {
                var wizard = new AddTaskWizard(connection, session!, server.Text!, preferences.Tasks);
                var dialog = wizard.ShowDialog<SyncTaskProfile?>(this);
                var path = Environment.GetEnvironmentVariable(index == 0 ? "MYPAN_PROBE_PRIVATE" : "MYPAN_PROBE_PUBLIC")!;
                await wizard.Probe(path, Path.Combine(home, index == 0 ? "full-copy" : "on-demand"), index == 1 && OperatingSystem.IsWindows(), home);
                var profile = await dialog; Check(profile != null, "Wizard selects an existing cloud folder and completes all three steps");
                await AddProfile(profile!);
            }
            await Until(() => Task.FromResult(tasks.All(x => x.IsRunning && x.HasSynchronized)));
            Check(tasks.Count == 2 && tasks.All(x => x.IsRunning), "Two independent sync tasks run concurrently");
            Check(tasks[0].Profile.OnDemand == false && tasks[1].Profile.OnDemand == OperatingSystem.IsWindows(), "Download mode is configured independently per task");
            Check(await File.ReadAllTextAsync(Path.Combine(tasks[0].Profile.Local, "probe.txt")) == "UI-private" && await File.ReadAllTextAsync(Path.Combine(tasks[1].Profile.Local, "probe.txt")) == "UI-public", "Both task roots receive the correct cloud bytes including on-demand hydration");
            File.Delete(Path.Combine(tasks[0].Profile.Local, "probe.txt"));
            await Until(async () => !(await connection.Http.GetFromJsonAsync("api/entries", ApiJson.Default.Catalog))!.Entries.Any(x => x.Path == tasks[0].Profile.Remote + "/probe.txt"));
            Check(pending.Count == 0, "Online deletion propagates to cloud without opening a confirmation dialog");
            await Toggle(tasks[0]); Check(!tasks[0].IsRunning && tasks[1].IsRunning, "Pausing one task leaves the other running");
            var persisted = SyncProfiles.Load(PreferencesFile);
            Check(persisted.Tasks.Count == 2 && !persisted.Tasks[0].Enabled && persisted.Tasks[1].Enabled, "Task roots, modes and enabled states persist independently");
            await Logout(); Check(loginPage.IsVisible && tasks.Count == 0, "Logout returns to login and stops all tasks");
            password.Text = Environment.GetEnvironmentVariable("MYPAN_PASSWORD"); await Login();
            await Until(() => Task.FromResult(tasks.Count == 2 && !tasks[0].IsRunning && tasks[1].IsRunning && tasks[1].HasSynchronized));
            Check(tasks.Count == 2, "Relogin loads the account's saved tasks and resumes only enabled tasks");
            SelectTask(tasks[0].Profile.Id);
            Check(pauseSelected.IsEnabled && pauseSelected.Content?.ToString() == "继续" && removeSelected.IsEnabled, "Toolbar selection follows a paused task and enables the corresponding actions");
            var originalStatePath = SyncProfiles.StatePath(home, tasks[0].Profile);
            RenameTask(tasks[0], "个人工作资料"); RenameTask(tasks[1], "团队共享资料");
            Check(SyncProfiles.Load(PreferencesFile).Tasks.Any(x => x.Name == "个人工作资料") && SyncProfiles.StatePath(home, tasks[0].Profile) == originalStatePath, "Renaming a task persists its display name and preserves its synchronization baseline");
            SaveDisplay(displaySettings with { CompactRows = true, ShowFullRemotePath = true }); BuildCards();
            Check(DisplayPreferences.Load(DisplaySettingsFile).ShowFullRemotePath && SyncProfiles.StatePath(home, tasks[0].Profile) == originalStatePath, "Display preferences persist without changing synchronization identities");
            await DesktopScreenshots.Save(this, Path.Combine(home, "tasks.png"));
            NavigateTo("activity"); activityFilter.SelectedIndex = 1; RefreshActivities();
            Check(activityRows.Children.Count > 0 && activities.Select(ClientActivity.Parse).Any(x => x.IsFile), "File activity is rendered from actual synchronization records");
            await DesktopScreenshots.Save(this, Path.Combine(home, "activity.png"));
            NavigateTo("settings"); settingsTabs.SelectedIndex = 2; await RefreshUsage();
            Check(usageRows.Children.Count > 0 && usageStatus.Text?.StartsWith("已从服务器更新", StringComparison.Ordinal) == true, "Usage tab retrieves real authorized folder sizes from the server");
            await DesktopScreenshots.Save(this, Path.Combine(home, "usage.png"));
            NavigateTo("notifications"); Check(CurrentNotifications().Count == 0, "Healthy tasks do not generate fabricated alerts");
            await DesktopScreenshots.Save(this, Path.Combine(home, "notifications.png"));
            ToggleTrayHub(); Check(trayHub?.IsVisible == true, "Tray status panel opens independently of the main window");
            await DesktopScreenshots.Save(trayHub!, Path.Combine(home, "tray.png")); trayHub!.Hide();
            NavigateTo("tasks"); Width = 760; Height = 520; await Task.Delay(150);
            Check(taskSearch.IsVisible == false && taskPage.Bounds.Width > 450 && taskPager.Bounds.Width < taskPage.Bounds.Width, "Compact window retains task controls and pagination without a wide search box");
            await DesktopScreenshots.Save(this, Path.Combine(home, "tasks-compact.png"));
            Width = 980; Height = 660; SaveDisplay(displaySettings with { ShowFullRemotePath = false }); BuildCards();
            var secondAddress = Environment.GetEnvironmentVariable("MYPAN_PROBE_SECOND_SERVER");
            if (secondAddress != null)
            {
                var firstAccount = activeAccount!;
                var primaryRunning = tasks.Single(x => x.IsRunning);
                var secondAccount = await ConnectAccount(secondAddress, "admin", Environment.GetEnvironmentVariable("MYPAN_PROBE_SECOND_PASSWORD")!);
                var secondFolder = (await secondAccount.Connection!.Http.GetFromJsonAsync("api/sync-folders", ApiJson.Default.ListSyncFolder))!.Single(x => x.Path == "公共");
                var secondProfile = new SyncTaskProfile(Guid.NewGuid(), secondAddress, "admin", secondAccount.Session!.User.Id, "第二服务器", secondFolder.Path, Path.Combine(home, "second-server"), false, true, secondFolder.Id);
                await AddProfile(secondProfile);
                await Until(() => Task.FromResult(tasks.Count == 3 && tasks.Count(x => x.IsRunning && x.HasSynchronized) == 2));
                Check(accounts.Count == 2 && primaryRunning.IsRunning && await File.ReadAllTextAsync(Path.Combine(secondProfile.Local, "second-server.txt")) == "second-server", "Two independent servers synchronize concurrently using separate credentials and roots");
                await SetConnectionTasks(secondAccount, false);
                Check(primaryRunning.IsRunning && !tasks.Single(t => t.Profile.Id == secondProfile.Id).IsRunning, "Pausing one server leaves tasks on the other server running");
                await SetConnectionTasks(secondAccount, true);
                using (var revoked = await secondAccount.Connection.Http.PostAsync("api/auth/logout", null)) revoked.EnsureSuccessStatusCode();
                await RestoreAccount(secondAccount);
                Check(secondAccount.NeedsLogin && firstAccount.Session != null && primaryRunning.IsRunning, "A revoked server login stops only its own tasks and preserves the other server connection");
                await ConnectAccount(secondAddress, "admin", Environment.GetEnvironmentVariable("MYPAN_PROBE_SECOND_PASSWORD")!);
                await Until(() => Task.FromResult(tasks.Count(x => x.IsRunning && x.HasSynchronized) == 2));
                Check(RememberedAccounts.Load(home).Count == 2 && !File.ReadAllText(RememberedAccounts.FilePath(home)).Contains(session!.AccessToken, StringComparison.Ordinal), "Multiple remembered logins round-trip through the encrypted credential store without plaintext tokens");
                Check(cards.Children.OfType<Border>().Count() >= 5, "Task list displays separate server headers and folder rows");
                await DesktopScreenshots.Save(this, Path.Combine(home, "multi-server.png"));
            }
            AtomicFile.WriteText(Path.Combine(home, "workflow-verification.json"), System.Text.Json.JsonSerializer.Serialize(new ChangeNotice(checks), ApiJson.Default.ChangeNotice));
        }
        catch (Exception ex) { Log("UI FAIL: " + ex); AtomicFile.WriteText(Path.Combine(home, "workflow-error.txt"), ex.ToString()); exitCode = 1; }
        finally
        {
            foreach (var task in tasks)
            {
                await task.Stop();
#if WINDOWS
                if (task.Profile.OnDemand && Directory.Exists(task.Profile.Local)) MyPan.Client.ShellIntegration.Unregister(task.Profile.Local);
#endif
            }
            foreach (var item in accounts) item.Connection?.Dispose(); closing = true; trayHub?.Close(); tray.Dispose(); Close(); (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown(exitCode);
        }
    }
}

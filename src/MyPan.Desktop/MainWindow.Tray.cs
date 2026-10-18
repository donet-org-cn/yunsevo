using Avalonia.Controls;
using Avalonia.Threading;
using MyPan.Contracts;

namespace MyPan.Desktop;

internal sealed partial class MainWindow
{
    private void InitializeTray()
    {
        var menu = new NativeMenu();
        void Item(string title, Action action) { var item = new NativeMenuItem(title); item.Click += (_, _) => action(); menu.Add(item); }
        Item("查看同步状态", ToggleTrayHub); Item("打开 " + ProductBrand.Desktop, ShowPanel);
        Item("暂停全部任务", () => _ = Operate(() => SetAll(false))); Item("继续全部任务", () => _ = Operate(() => SetAll(true)));
        Item("全局设置", () => { ShowPanel(); NavigateTo("settings"); }); Item("退出沄序", () => _ = Operate(Exit));
        tray.Menu = menu; tray.IsVisible = true; tray.Clicked += (_, _) => ToggleTrayHub(); TrayIcon.SetIcons(Avalonia.Application.Current!, new TrayIcons { tray });
    }
    private void ToggleTrayHub()
    {
        if (trayHub?.IsVisible == true) { trayHub.Hide(); return; }
        if (trayHub != null && DateTimeOffset.UtcNow - trayHub.LastDeactivated < TimeSpan.FromMilliseconds(250)) return;
        trayHub ??= new TrayHubWindow(ShowPanel, () => { ShowPanel(); NavigateTo("settings"); }, () =>
        {
            var task = SelectedTask ?? tasks.FirstOrDefault(); if (task != null && Directory.Exists(task.Profile.Local)) OpenFolder(task.Profile.Local); else ShowPanel();
        }, OpenServerWeb, () => { ShowPanel(); NavigateTo("activity"); });
        UpdateTrayHub(); trayHub.ShowNearTray();
    }
    private void UpdateTrayHub()
    {
        if (trayHub == null) return;
        var state = currentState;
        var title = accounts.Count == 0 ? "尚未登录" : state is "signed-out" or "offline" ? state == "signed-out" ? "登录已失效" : "部分服务器未连接" : tasks.Count == 0 ? "尚未创建同步任务" : state switch
        {
            "error" => "有项目需要处理", "offline" => "连接已断开", "signed-out" => "登录已失效", "paused" => "同步已暂停", _ => tasks.Any(x => !x.IsRunning) ? "部分任务已暂停" : tasks.All(x => x.HasSynchronized) ? "已为最新" : "正在同步"
        };
        if (tasks.Any(x => !x.IsRunning) && state == "connected") state = "paused";
        var detail = accounts.Count == 0 ? "点击下方品牌名称打开登录窗口" : accounts.Count + " 个服务器连接 · " + (lastSynchronized.HasValue ? "上次同步 " + lastSynchronized.Value.ToLocalTime().ToString("HH:mm") : "正在等待同步");
        trayHub.Update(state, title, detail, session?.User.DisplayName ?? "", accounts.Count == 0 ? [] : activities.Select(ClientActivity.Parse).Where(x => x.IsFile).DistinctBy(x => (x.Task, x.Kind, x.Path)).Take(12).ToList(), CurrentNotifications(), displaySettings.ShowRecentInTray);
    }
}

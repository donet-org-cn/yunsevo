using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
using Yunsevo.Contracts;
using Yunsevo.Mobile.Core;

namespace Yunsevo.Mobile.Android;

[Activity(Label = "沄序", MainLauncher = true, Exported = true, LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
[IntentFilter([Intent.ActionSend, Intent.ActionSendMultiple], Categories = [Intent.CategoryDefault], DataMimeType = "*/*")]
public sealed partial class MainActivity : Activity
{
    private CancellationTokenSource stop = new();
    private int requestEpoch;
    private LinearLayout root = null!, content = null!;
    private TextView status = null!;
    private FrameLayout workspaceBody = null!;
    private ScrollView workspaceScroll = null!;
    private string tab = "files", folder = "", query = "";
    private int page = 1;
    private bool busy, loggedInView;
    private TaskCompletionSource<bool>? permissionReply;
    private long lastProgress;
    private MobileClient Client => AppServices.Client;
    protected override void OnCreate(Bundle? state)
    {
        base.OnCreate(state);
        AppServices.Changed += OnServicesChanged;
        if (Client.Account == null) ShowLogin();
        else { if (Intent?.GetStringExtra("tab") == "backup") tab = "backup"; ShowWorkspace(); _ = Run(tab == "backup" ? RenderCloudAlbum : RenderFiles); AppServices.Kick(); }
        if (state == null) Receive(Intent);
    }
    protected override void OnNewIntent(Intent? intent) { base.OnNewIntent(intent); Intent = intent; if (!NavigateIntent(intent)) Receive(intent); }
    private bool NavigateIntent(Intent? intent)
    {
        if (intent?.GetStringExtra("tab") != "backup" || Client.Account == null) return false;
        tab = "backup"; ShowWorkspace(); _ = Run(RenderCloudAlbum); return true;
    }
    protected override void OnResume() { base.OnResume(); if (loggedInView) AppServices.Kick(); }
    protected override void OnDestroy() { AppServices.Changed -= OnServicesChanged; stop.Cancel(); ClearAlbumImages(); base.OnDestroy(); }
    private async Task Run(Func<Task> operation)
    {
        if (busy) return;
        busy = true; var epoch = requestEpoch;
        try { await operation(); }
        catch (OperationCanceledException) { }
        catch (HttpRequestException ex) when (epoch == requestEpoch && ex.StatusCode == System.Net.HttpStatusCode.Unauthorized) { ShowLogin("登录已失效，请重新登录原账号；待上传文件已保留。"); }
        catch (Exception ex) { if (epoch == requestEpoch && !IsFinishing) Ui.Alert(this, "操作未完成", ex.Message); }
        finally { if (epoch == requestEpoch) busy = false; }
    }
    private void ShowLogin(string? error = null)
    {
        ClearAlbumImages();
        loggedInView = false;
        var body = Ui.Column(this, 28); body.SetBackgroundColor(Ui.Background);
        body.AddView(Ui.Text(this, "沄序", 34, true));
        body.AddView(Ui.Text(this, "文件有序，现场记录从容", 17));
        var intro = Ui.Text(this, "工程水印拍照 · 相册自动备份 · 文件随身可用", 14); intro.SetTextColor(Ui.Muted); body.AddView(intro);
        var address = Ui.Input(this, "服务器地址（HTTPS / 局域网 HTTP）", Client.Account?.Server ?? Intent?.GetStringExtra("server") ?? "https://");
        address.ContentDescription = "服务器地址"; body.AddView(address);
        var username = Ui.Input(this, "用户名", Client.Account?.Login.User.Username ?? Intent?.GetStringExtra("username")); username.ContentDescription = "用户名"; body.AddView(username);
        var password = Ui.Input(this, "密码", password: true); password.ContentDescription = "密码"; body.AddView(password);
        var message = Ui.Text(this, error ?? "登录后自动记住此设备，无需每次输入密码。", 13); body.AddView(message);
        Button? submit = null;
        submit = Ui.Button(this, "登录", () => _ = Run(async () =>
        {
            submit!.Enabled = false;
            try
            {
                await Client.Login(address.Text!.Trim(), username.Text!.Trim(), password.Text ?? "", stop.Token);
                password.Text = ""; folder = ""; query = ""; page = 1; tab = "files";
                ShowWorkspace(); await RenderFiles(); AppServices.Kick();
                if (ShareInbox.Read().Count > 0) await SaveInbox();
            }
            finally { password.Text = ""; submit.Enabled = true; }
        }));
        body.AddView(submit);
        if (ShareInbox.Read().Count > 0) body.AddView(Ui.Text(this, "已接收 " + ShareInbox.Read().Count + " 个分享文件，登录后选择保存目录。", 14));
        body.AddView(Ui.Button(this, "关闭应用", CloseApplication, false));
        var scroll = new ScrollView(this) { FillViewport = true }; scroll.SetFitsSystemWindows(true); scroll.AddView(body); SetContentView(scroll);
    }
    private void ShowWorkspace()
    {
        loggedInView = true;
        root = new(this) { Orientation = Orientation.Vertical }; root.SetFitsSystemWindows(true); root.SetBackgroundColor(Ui.Background);
        var header = Ui.Column(this, 16);
        header.AddView(Ui.Text(this, "沄序 · " + Client.Account!.Login.User.DisplayName, 21, true));
        status = Ui.Text(this, AppServices.Status + " · " + AppServices.NetworkLabel, 12); status.SetTextColor(Ui.Muted); header.AddView(status); root.AddView(header);
        content = Ui.Column(this, 16);
        workspaceScroll = new ScrollView(this) { FillViewport = true }; workspaceScroll.AddView(content);
        workspaceBody = new FrameLayout(this); workspaceBody.AddView(workspaceScroll);
        root.AddView(workspaceBody, new LinearLayout.LayoutParams(-1, 0, 1));
        var navigation = new LinearLayout(this) { Orientation = Orientation.Horizontal }; navigation.SetBackgroundColor(global::Android.Graphics.Color.White);
        foreach (var (key, title) in new[] { ("files", "文件"), ("camera", "工程拍照"), ("backup", "相册"), ("transfers", "传输"), ("me", "我的") })
        {
            var button = new Button(this) { Text = title, TextSize = 12 }; button.SetAllCaps(false);
            button.SetTextColor(key == tab ? Ui.Teal : Ui.Muted); button.SetBackgroundColor(global::Android.Graphics.Color.Transparent);
            button.LayoutParameters = new LinearLayout.LayoutParams(0, Ui.Dp(this, 58), 1);
            button.Click += (_, _) => { CancelPageRequests(); if (key != "backup") ClearAlbumImages(); tab = key; ShowWorkspace(); _ = Run(RenderTab); };
            navigation.AddView(button);
        }
        root.AddView(navigation); SetContentView(root);
    }
    private Task RenderTab()
    {
        content.RemoveAllViews();
        return tab switch { "files" => RenderFiles(), "camera" => RenderCamera(), "backup" => RenderCloudAlbum(), "transfers" => RenderTransfers(), _ => RenderMe() };
    }
    private void OnServicesChanged()
    {
        if (System.Environment.TickCount64 - lastProgress < 400) return;
        lastProgress = System.Environment.TickCount64;
        RunOnUiThread(() =>
        {
            if (!loggedInView || IsFinishing) return;
            status.Text = AppServices.Status + " · " + AppServices.NetworkLabel;
            if (tab == "transfers" && !busy) _ = RenderTransfers();
        });
    }
    private async Task RenderFiles()
    {
        var epoch = requestEpoch;
        content.RemoveAllViews();
        if (ShareInbox.Read().Count > 0) content.AddView(Ui.Button(this, "收到 " + ShareInbox.Read().Count + " 个文件 · 选择保存目录", () => _ = Run(SaveInbox)));
        content.AddView(Ui.Text(this, folder.Length == 0 ? "我的文件" : Ui.PathLabel(folder), 23, true));
        var search = Ui.Input(this, "搜索当前目录及子目录", query);
        search.ImeOptions = global::Android.Views.InputMethods.ImeAction.Search;
        search.EditorAction += (_, _) => { query = search.Text!.Trim(); page = 1; _ = Run(RenderFiles); }; content.AddView(search);
        if (folder.Length == 0 && query.Length == 0)
        {
            var folders = await Client.Folders(stop.Token); if (epoch != requestEpoch) return;
            var roots = folders.Where(x => (x.Access & 1) != 0).Where(x => !folders.Any(p => p.Id != x.Id && (p.Access & 1) != 0 && CloudPath.IsUnder(x.Path, p.Path))).ToList();
            foreach (var item in roots)
                AddRow("▣  " + Ui.PathLabel(item.Path), Ui.Category(item.Category) + ((item.Access & 2) != 0 ? " · 可读写" : " · 只读"),
                    () => { folder = item.Path; page = 1; _ = Run(RenderFiles); });
            if (roots.Count == 0) content.AddView(Ui.Text(this, "暂时没有可访问的目录，请联系管理员。"));
        }
        else
        {
            content.AddView(Ui.Button(this, "返回上级", () => { folder = CloudPath.Parent(folder) ?? ""; query = ""; page = 1; _ = Run(RenderFiles); }, false));
            var result = await Client.Browse(folder, page, query, stop.Token); if (epoch != requestEpoch) return;
            foreach (var row in result.Items)
            {
                var file = row.Entry;
                var detail = file.IsDirectory ? "文件夹" : Ui.Size(file.Size) + (file.Origin?.Photographer is { } person ? " · 拍摄：" + person.DisplayName : "");
                AddRow((file.IsDirectory ? "▣  " : "▤  ") + Path.GetFileName(file.Path), detail, () =>
                {
                    if (file.IsDirectory) { folder = file.Path; query = ""; page = 1; _ = Run(RenderFiles); }
                    else StartActivity(new Intent(this, typeof(PreviewActivity)).PutExtra("id", file.Id.ToString()).PutExtra("path", file.Path));
                }, () => Ui.Alert(this, Path.GetFileName(file.Path), Ui.Description(file)));
            }
            content.AddView(Ui.Text(this, result.Total + " 项 · " + result.Page + "/" + Math.Max(1, (result.Total + 29) / 30) + " 页", 12));
            if (page > 1) content.AddView(Ui.Button(this, "上一页", () => { page--; _ = Run(RenderFiles); }, false));
            if (page * 30 < result.Total) content.AddView(Ui.Button(this, "下一页", () => { page++; _ = Run(RenderFiles); }, false));
        }
        content.AddView(Ui.Button(this, "本机已有 3D 模型", () => StartActivity(new Intent(this, typeof(SavedModelsActivity))), false));
        content.AddView(Ui.Button(this, "上传手机文件", () =>
        {
            var picker = new Intent(Intent.ActionOpenDocument).AddCategory(Intent.CategoryOpenable)!.SetType("*/*")!.PutExtra(Intent.ExtraAllowMultiple, true);
            StartActivityForResult(picker, 801);
        }));
        content.AddView(Ui.Button(this, "新建文件夹", () => _ = Run(async () =>
        {
            var parent = await PickFolder("选择新文件夹的父目录"); if (parent == null) return;
            var name = await Prompt("新建文件夹", "文件夹名称"); if (name == null) return;
            await Client.CreateFolder(parent, name, stop.Token); await RenderFiles();
        }), false));
    }
    private void AddRow(string title, string subtitle, Action click, Action? longClick = null)
    {
        var row = Ui.Column(this, 14); row.Background = Ui.Shape(global::Android.Graphics.Color.White);
        row.LayoutParameters = new LinearLayout.LayoutParams(-1, -2) { BottomMargin = Ui.Dp(this, 8) };
        row.AddView(Ui.Text(this, title, 16, true)); var secondary = Ui.Text(this, subtitle, 12); secondary.SetTextColor(Ui.Muted); row.AddView(secondary);
        row.Click += (_, _) => click();
        if (longClick != null) row.LongClick += (_, _) => longClick();
        content.AddView(row);
    }
    private Task RenderCamera()
    {
        content.RemoveAllViews(); content.AddView(Ui.Text(this, "工程水印相机", 25, true));
        var project = Client.Options.Project;
        content.AddView(Ui.Text(this, project == null ? "先选择工程，再开始拍摄" : "当前工程：" + Ui.PathLabel(project.Path), 17));
        content.AddView(Ui.Text(this, "照片自动添加工程、拍摄人、时间与备注水印，并保存到选定的工程目录。弱网时先保存在手机上传队列。", 14));
        content.AddView(Ui.Button(this, "选择工程目录", () => _ = Run(async () => { var chosen = await PickFolder("选择工程保存目录"); if (chosen != null) { SaveOptions(Client.Options with { Project = chosen }); await RenderCamera(); } }), false));
        content.AddView(Ui.Button(this, "创建新工程", () => _ = Run(async () =>
        {
            var parent = await PickFolder("选择工程所在目录"); if (parent == null) return;
            var name = await Prompt("创建工程", "例如：xxx工程"); if (name == null) return;
            var created = await Client.CreateFolder(parent, name, stop.Token); SaveOptions(Client.Options with { Project = created }); await RenderCamera();
        }), false));
        var place = Ui.Input(this, "施工部位（可选）", Client.Options.Place); content.AddView(place);
        var note = Ui.Input(this, "水印备注（可选）", Client.Options.Note); content.AddView(note);
        Toggle("在水印中添加定位", Client.Options.IncludeLocation, value => SaveOptions(Client.Options with { IncludeLocation = value }));
        Toggle("显示详细地址", Client.Options.IncludeAddress, value => SaveOptions(Client.Options with { IncludeAddress = value }));
        Toggle("显示天气", Client.Options.IncludeWeather, value => SaveOptions(Client.Options with { IncludeWeather = value }));
        content.AddView(Ui.Text(this, "地址、天气按需通过服务器查询，会向所配置的服务商发送定位坐标。无定位或断网时明确显示不可用；天气包含来源与数据时间。", 12));
        content.AddView(Ui.Text(this, "拍摄保存方式", 14, true));
        var saveMode = new Spinner(this);
        saveMode.Adapter = new ArrayAdapter<string>(this, global::Android.Resource.Layout.SimpleSpinnerDropDownItem, new[] { "云端 + 本机相册", "仅云端（上传成功后清理临时文件）" });
        saveMode.SetSelection(Client.Options.SaveCameraToGallery ? 0 : 1);
        saveMode.ItemSelected += (_, args) => SaveOptions(Client.Options with { SaveCameraToGallery = args.Position == 0 }); content.AddView(saveMode);
        Toggle("录像录制声音", Client.Options.RecordAudio, value => SaveOptions(Client.Options with { RecordAudio = value }));
        content.AddView(Ui.Button(this, "水印样式与颜色", () => WatermarkPainter.Settings(this, Client.Options, SaveOptions), false));
        content.AddView(Ui.Button(this, "开始拍照", () => _ = Run(async () =>
        {
            if (Client.Options.Project == null) { Ui.Alert(this, "先选择工程", "请先选择或创建工程目录。"); return; }
            SaveOptions(Client.Options with { Place = (place.Text ?? "").Trim()[..Math.Min((place.Text ?? "").Trim().Length, 150)], Note = (note.Text ?? "").Trim()[..Math.Min((note.Text ?? "").Trim().Length, 300)] });
            if (CheckSelfPermission(Manifest.Permission.Camera) != Permission.Granted)
                await Permissions([Manifest.Permission.Camera]);
            if (CheckSelfPermission(Manifest.Permission.Camera) != Permission.Granted) return;
            if ((Client.Options.IncludeLocation || Client.Options.IncludeAddress || Client.Options.IncludeWeather) && CheckSelfPermission(Manifest.Permission.AccessCoarseLocation) != Permission.Granted)
                await Permissions([Manifest.Permission.AccessFineLocation, Manifest.Permission.AccessCoarseLocation]);
            if (Client.Options.SaveCameraToGallery && !OperatingSystem.IsAndroidVersionAtLeast(29) && CheckSelfPermission(Manifest.Permission.WriteExternalStorage) != Permission.Granted)
                await Permissions([Manifest.Permission.WriteExternalStorage]);
            StartActivity(new Intent(this, typeof(CameraActivity)));
        })));
        return Task.CompletedTask;
    }
    private Task RenderBackup()
    {
        workspaceBody.RemoveAllViews(); workspaceBody.AddView(workspaceScroll);
        ClearAlbumImages(); content.RemoveAllViews(); content.AddView(Ui.Button(this, "返回云端相册", () => _ = Run(RenderCloudAlbum), false)); content.AddView(Ui.Text(this, "相册自动备份", 25, true));
        content.AddView(Ui.Text(this, "原图与视频保存在你选择的云端目录。清理手机相册后，已备份的云端文件仍保留。", 14));
        content.AddView(Ui.Text(this, "备份目录：" + (Client.Options.BackupFolder == null ? "尚未设置" : Ui.PathLabel(Client.Options.BackupFolder.Path)), 15));
        content.AddView(Ui.Button(this, "选择备份目录", () => _ = Run(async () =>
        {
            var selected = await PickFolder("选择相册备份目录");
            if (selected != null) { SaveOptions(Client.Options with { BackupFolder = selected }); await RenderBackup(); }
        }), false));
        Toggle("开启自动备份", Client.Options.AutoBackup, value => _ = Run(async () =>
        {
            if (value && Client.Options.BackupFolder == null)
            { var selected = await PickFolder("选择相册备份目录"); if (selected == null) { await RenderBackup(); return; } SaveOptions(Client.Options with { BackupFolder = selected }); }
            if (value)
            {
                var permissions = OperatingSystem.IsAndroidVersionAtLeast(34) ? new[] { Manifest.Permission.ReadMediaImages, Manifest.Permission.ReadMediaVideo, "android.permission.READ_MEDIA_VISUAL_USER_SELECTED" } :
                    OperatingSystem.IsAndroidVersionAtLeast(33) ? [Manifest.Permission.ReadMediaImages, Manifest.Permission.ReadMediaVideo] : new[] { Manifest.Permission.ReadExternalStorage };
                await Permissions(permissions);
                if (!AndroidAlbumSource.HasPermission(this)) { Ui.Alert(this, "需要相册访问权限", "允许访问照片后才能自动备份；Android 14 也可只授权选择的照片。"); await RenderBackup(); return; }
            }
            SaveOptions(Client.Options with { AutoBackup = value }); AppServices.Kick(); await RenderBackup();
        }));
        Toggle("包含视频", Client.Options.IncludeVideos, value => SaveOptions(Client.Options with { IncludeVideos = value }));
        Toggle("仅 Wi-Fi 上传（含工程照片和分享文件）", Client.Options.WifiOnly, value => { SaveOptions(Client.Options with { WifiOnly = value }); AppServices.Kick(); });
        Toggle("仅充电时上传", Client.Options.ChargingOnly, value => SaveOptions(Client.Options with { ChargingOnly = value }));
        content.AddView(Ui.Text(this, "自动备份由 Android 调度，后台时机受系统省电策略影响。仅授权部分照片时，只备份已授权内容。", 12));
        content.AddView(Ui.Button(this, "立即备份", () => { AppServices.Repository.Retry(Client.AccountKey); AppServices.Kick(); status.Text = "正在检查相册与上传队列…"; }));
        return Task.CompletedTask;
    }
    private Task RenderTransfers()
    {
        content.RemoveAllViews(); content.AddView(Ui.Text(this, "上传记录", 25, true));
        content.AddView(Ui.Text(this, AppServices.Status, 14));
        content.AddView(Ui.Button(this, "立即重试", () => { AppServices.Repository.Retry(Client.AccountKey); AppServices.Kick(); }, false));
        Toggle("暂停全部上传", Client.Options.PauseUploads, value => { SaveOptions(Client.Options with { PauseUploads = value }); if (!value) AppServices.Kick(); });
        var jobs = AppServices.Repository.Jobs(Client.AccountKey);
        foreach (var job in jobs.Take(100))
        {
            var state = job.State switch { "completed" => "已上传", "uploading" => "正在上传", "failed" => "等待重试", "cancelled" => "已跳过", _ => "待上传" };
            AddRow(job.Name, state + " · " + Ui.Size(job.Length) + "\n" + Ui.PathLabel(job.Completed?.Path ?? job.Folder.Path) + (job.Error == null ? "" : "\n" + job.Error), () =>
            {
                if (job.Completed != null) StartActivity(new Intent(this, typeof(PreviewActivity)).PutExtra("id", job.Completed.Id.ToString()).PutExtra("path", job.Completed.Path));
                else Ui.Alert(this, "待上传文件", job.Name + "\n" + (job.Error ?? "文件已保存在手机中，联网且满足上传条件后自动上传。"));
            });
        }
        if (jobs.Count == 0) content.AddView(Ui.Text(this, "暂无上传记录。工程照片、相册备份和分享文件会在这里显示。"));
        return Task.CompletedTask;
    }
    private Task RenderMe()
    {
        content.RemoveAllViews(); content.AddView(Ui.Text(this, Client.Account!.Login.User.DisplayName, 25, true));
        content.AddView(Ui.Text(this, Client.Account.Login.User.Username + "\n" + Client.Account.Server, 14));
        content.AddView(Ui.Text(this, "登录凭据由 Android Keystore 加密保存。拍摄人由服务端按当前登录身份记录。", 14));
        content.AddView(Ui.Button(this, "系统权限与后台设置", () => StartActivity(new Intent(global::Android.Provider.Settings.ActionApplicationDetailsSettings,
            global::Android.Net.Uri.Parse("package:" + PackageName))), false));
        content.AddView(Ui.Button(this, "查看接收的分享文件", () => _ = Run(SaveInbox), false));
        content.AddView(Ui.Button(this, "退出登录", () => _ = SignOut(), false));
        content.AddView(Ui.Button(this, "关闭应用", CloseApplication, false));
        content.AddView(Ui.Text(this, "关闭界面后，已启用的后台备份仍可继续。退出登录会停止此账号的同步，待上传文件会保留。", 12));
        return Task.CompletedTask;
    }
    private void CancelPageRequests()
    {
        requestEpoch++; var previous = stop; stop = new(); busy = false;
        previous.Cancel(); previous.Dispose();
    }
    private async Task SignOut()
    {
        CancelPageRequests(); ClearAlbumImages();
        try
        {
            var revoke = Client.Logout(CancellationToken.None);
            if (Client.Account != null) { await revoke; return; }
            AppServices.Schedule(); ShowLogin(); await revoke;
        }
        catch (Exception ex) { if (!IsFinishing) Ui.Alert(this, "退出登录未完成", ex.Message); }
    }
    private void CloseApplication()
    {
        CancelPageRequests(); ClearAlbumImages(); FinishAndRemoveTask();
    }
    private void Toggle(string title, bool selected, Action<bool> change)
    {
        var toggle = new CheckBox(this) { Text = title, Checked = selected, TextSize = 14 };
        toggle.SetTextColor(Ui.Ink); toggle.CheckedChange += (_, e) => change(e.IsChecked); content.AddView(toggle);
    }
    private void SaveOptions(MobileOptions options) { AppServices.Repository.SetOptions(Client.AccountKey, options); AppServices.Schedule(); }
    private async Task<FolderSelection?> PickFolder(string title)
    {
        var folders = (await Client.Folders(stop.Token)).Where(x => (x.Access & 2) != 0).ToList();
        if (folders.Count == 0) throw new InvalidOperationException("没有可写入的目录，请联系管理员分配权限。");
        var reply = new TaskCompletionSource<FolderSelection?>();
        var dialog = new AlertDialog.Builder(this)!.SetTitle(title)!
            .SetItems(folders.Select(x => Ui.PathLabel(x.Path) + " · " + Ui.Category(x.Category)).ToArray(), (_, args) =>
            { var selected = folders[args.Which]; reply.TrySetResult(new(selected.Id, selected.Path, selected.Category)); })!
            .SetNegativeButton("取消", (_, _) => reply.TrySetResult(null))!.Create();
        dialog!.CancelEvent += (_, _) => reply.TrySetResult(null); dialog.Show(); return await reply.Task;
    }
    private Task<string?> Prompt(string title, string hint)
    {
        var reply = new TaskCompletionSource<string?>(); var edit = Ui.Input(this, hint);
        var dialog = new AlertDialog.Builder(this)!.SetTitle(title)!.SetView(edit)!
            .SetPositiveButton("创建", (_, _) => reply.TrySetResult(edit.Text?.Trim()))!
            .SetNegativeButton("取消", (_, _) => reply.TrySetResult(null))!.Create();
        dialog!.CancelEvent += (_, _) => reply.TrySetResult(null); dialog.Show(); return reply.Task;
    }
    private Task<bool> Permissions(string[] permissions)
    {
        permissionReply = new(); RequestPermissions(permissions, 802); return permissionReply.Task;
    }
    public override void OnRequestPermissionsResult(int code, string[] permissions, Permission[] grantResults)
    { base.OnRequestPermissionsResult(code, permissions, grantResults); if (code == 802) permissionReply?.TrySetResult(grantResults.Any(x => x == Permission.Granted)); }
    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    { base.OnActivityResult(requestCode, resultCode, data); if (requestCode == 801 && resultCode == Result.Ok) ImportIntent(data, true); }
    private void Receive(Intent? intent) { if (intent?.Action is Intent.ActionSend or Intent.ActionSendMultiple) ImportIntent(intent, false); }
    private async void ImportIntent(Intent? intent, bool picker)
    {
        if (intent == null) return;
        try
        {
            var uris = new List<global::Android.Net.Uri>();
            if (intent.ClipData is { } clips) for (var i = 0; i < clips.ItemCount; i++) if (clips.GetItemAt(i)?.Uri is { } uri) uris.Add(uri);
            if (picker && intent.Data is { } data) uris.Add(data);
#pragma warning disable CS0618
            if (!picker && OperatingSystem.IsAndroidVersionAtLeast(33))
            {
                var uriType = Java.Lang.Class.FromType(typeof(global::Android.Net.Uri));
                if (intent.GetParcelableExtra(Intent.ExtraStream, uriType) is global::Android.Net.Uri single) uris.Add(single);
                if (intent.GetParcelableArrayListExtra(Intent.ExtraStream, uriType) is { } multiple) foreach (var item in multiple) if (item is global::Android.Net.Uri uri) uris.Add(uri);
            }
            else if (!picker)
            {
                if (intent.GetParcelableExtra(Intent.ExtraStream) is global::Android.Net.Uri single) uris.Add(single);
                if (intent.GetParcelableArrayListExtra(Intent.ExtraStream) is { } multiple) foreach (var item in multiple) if (item is global::Android.Net.Uri uri) uris.Add(uri);
            }
#pragma warning restore CS0618
            if (uris.Count == 0) return;
            await ShareInbox.Import(this, uris.DistinctBy(x => x.ToString()), stop.Token);
            if (Client.Account == null) ShowLogin(); else await Run(SaveInbox);
        }
        catch (Exception ex) { Ui.Alert(this, "文件接收失败", ex.Message); }
    }
    private async Task SaveInbox()
    {
        var files = ShareInbox.Read();
        if (files.Count == 0) { Ui.Alert(this, "接收文件", "在其他应用的分享菜单中选择“沄序”，或从文件页面选择手机文件。"); return; }
        var selected = await PickFolder("将 " + files.Count + " 个文件保存到…"); if (selected == null) return;
        foreach (var file in files)
        {
            await using var input = File.OpenRead(ShareInbox.FilePath(file));
            await Client.Stage(input, file.Name, selected, new("shared", DeviceName: AppServices.DeviceName), "", stop.Token);
            ShareInbox.Remove(file);
        }
        AppServices.Kick(); tab = "transfers"; ShowWorkspace(); await RenderTransfers();
    }
}

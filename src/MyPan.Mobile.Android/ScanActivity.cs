using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Widget;
using MyPan.Contracts;
using MyPan.Mobile.Core;
using MyPan.Sync;
using System.Net.Http.Json;

namespace MyPan.Mobile.Android;

[Activity(Label = "三维扫描", Exported = false)]
public sealed class ScanActivity : Activity
{
    private LinearLayout body = null!;
    private Guid? selected, importing;
    private bool busy;
    private string rendered = "";
    private readonly CancellationTokenSource stop = new();
    private readonly Handler timer = new(Looper.MainLooper!);
    private ReconstructionCapabilities? caps;
    protected override void OnCreate(Bundle? state)
    {
        base.OnCreate(state); if (AppServices.Client.Account == null) { Finish(); return; }
        if (Guid.TryParse(state?.GetString("selected"), out var id)) selected = id;
        if (Guid.TryParse(state?.GetString("importing"), out var pending)) importing = pending;
        var scroll = new ScrollView(this); scroll.SetFitsSystemWindows(true); body = Ui.Column(this); scroll.AddView(body); SetContentView(scroll);
        _ = Task.Run(ReconstructionNative.Capabilities).ContinueWith(t => RunOnUiThread(() => { if (!IsDestroyed && t.IsCompletedSuccessfully) { caps = t.Result; Render(); } }));
    }
    protected override void OnResume() { base.OnResume();
        if (!ReconstructionService.IsActive) foreach (var scan in ScanStore.List().Where(x => x.State == "running")) { ScanStore.RecoverPreviousModel(scan.Id); ScanStore.Edit(scan.Id, x => x with { State = File.Exists(ScanStore.Model(scan.Id)) ? "ready" : "failed", Message = "上次生成已中断，原片与可用的旧模型已保留，可重新生成。" }); }
        if (!busy) foreach (var scan in ScanStore.List().Where(x => x.State == "staging")) ScanStore.Edit(scan.Id, x => x with { State = "failed", Message = "原片准备已中断，可继续提交，已经入队的文件会保留。" });
        Render(); timer.PostDelayed(Tick, 1200); }
    protected override void OnPause() { timer.RemoveCallbacksAndMessages(null); base.OnPause(); }
    protected override void OnSaveInstanceState(Bundle state) { state.PutString("selected", selected?.ToString()); state.PutString("importing", importing?.ToString()); base.OnSaveInstanceState(state); }
    protected override void OnDestroy() { stop.Cancel(); timer.Dispose(); stop.Dispose(); base.OnDestroy(); }
    private void Tick()
    {
        if (IsDestroyed || IsFinishing) return;
        if (!busy)
        {
            try
            {
                if (selected.HasValue && ScanStore.Get(selected.Value).State is "remote" or "uploading") _ = Run(async () => { await Poll(ScanStore.Get(selected.Value)); if (Signature() != rendered) Render(); }, false);
                else if (Signature() != rendered) Render();
            }
            catch (InvalidOperationException) { Finish(); return; }
        }
        timer.PostDelayed(Tick, 1600);
    }
    private string Signature()
    {
        if (!selected.HasValue) return string.Join(";", ScanStore.List().Select(x => x.Id + ":" + x.State));
        var scan = ScanStore.Get(selected.Value);
        var upload = AppServices.Repository.Jobs(scan.Account).FirstOrDefault(x => x.Id == scan.ModelUpload);
        return string.Join("|", scan.State, scan.Progress, scan.Message, scan.Backend, ScanStore.Files(scan.Id).Length, upload?.State);
    }
    private void Render()
    {
        rendered = Signature();
        body.RemoveAllViews(); body.AddView(Ui.Text(this, "三维扫描", 26, true));
        body.AddView(Ui.Text(this, "多角度照片 → 真实三维网格 · 试验功能", 14));
        body.AddView(Ui.Text(this, caps == null ? "正在检测本机计算能力…" : caps.Vulkan ? "Vulkan GPU：" + caps.Device + " · CPU 可用" : caps.Cpu ? "本机 CPU 可用 · Vulkan 不可用" : caps.Error ?? "本机引擎不可用", 12));
        if (!selected.HasValue)
        {
            body.AddView(Ui.Button(this, "新建扫描", () => _ = Run(Create)));
            foreach (var value in ScanStore.List())
            {
                var copy = value; body.AddView(Ui.Button(this, value.Name + (value.DerivedFrom.HasValue ? " · 重选 " + value.CreatedUtc.ToLocalTime().ToString("MM-dd HH:mm") : "") + " · " + State(value.State) + " · " + ScanStore.Files(value.Id).Length + " 张", () => { selected = copy.Id; Render(); }, false));
            }
            body.AddView(Ui.Text(this, "先支持有纹理的静止物体／设备。透明、反光、纯色表面难以重建；模型为相对尺寸，不作为工程测量依据。", 13)); return;
        }
        var scan = ScanStore.Get(selected.Value); var files = ScanStore.Files(scan.Id);
        body.AddView(Ui.Button(this, "‹ 所有扫描", () => { selected = null; Render(); }, false));
        body.AddView(Ui.Text(this, scan.Name, 22, true)); body.AddView(Ui.Text(this, Ui.PathLabel(scan.Folder.Path) + " · " + (scan.Subject == "object" ? "物体／设备" : "房间／现场（试验）"), 13));
        body.AddView(Ui.Text(this, files.Length + "/" + ScanStore.MaxPhotos + " 张原始照片 · " + State(scan.State), 16, true));
        if (scan.DerivedFrom.HasValue) body.AddView(Ui.Text(this, "重新选帧的独立版本 · 原扫描和旧模型仍在「所有扫描」中", 12));
        var progress = new ProgressBar(this, null, global::Android.Resource.Attribute.ProgressBarStyleHorizontal) { Max = 100, Progress = scan.Progress }; body.AddView(progress);
        var message = Ui.Text(this, ReconstructionMessages.Error(scan.Message), 13); message.SetTextIsSelectable(true); body.AddView(message);
        var working = scan.State is "running" or "remote" or "uploading" or "staging";
        if (!working && !File.Exists(ScanStore.Model(scan.Id)) && !scan.RemoteJob.HasValue && scan.Inputs == null)
        {
            body.AddView(Ui.Text(this, "绕物体慢走，保持同一镜头和方向，每移动约 10–15° 拍一张。先平视一圈，再略高／略低补拍；建议 36–72 张、相邻画面重叠约 70%。物体和光照保持不动。", 14));
            body.AddView(Ui.Button(this, "引导拍摄 · 无水印原片", () => Camera(scan.Id)));
            body.AddView(Ui.Button(this, "环绕录像 · 自动选帧", () => Camera(scan.Id, true), false));
            body.AddView(Ui.Button(this, "从视频自动选帧", () => { if (busy) return; importing = scan.Id; StartActivityForResult(new Intent(Intent.ActionOpenDocument).AddCategory(Intent.CategoryOpenable)!.SetType("video/*"), 974); }, false));
            body.AddView(Ui.Button(this, "从系统相册添加照片", () => { if (busy) return; importing = scan.Id; StartActivityForResult(new Intent(Intent.ActionOpenDocument).AddCategory(Intent.CategoryOpenable)!.SetType("image/*")!.PutExtra(Intent.ExtraAllowMultiple, true), 971); }, false));
            if (files.Length >= 6)
            {
                body.AddView(Ui.Button(this, "手机本机生成 · " + (scan.Quality == 1 ? "精细" : "快速"), () => PickBackend(scan.Id)));
                body.AddView(Ui.Text(this, "本机精细：更密集的深度计算、多视角校验和小缺口修复；支持 Vulkan／CPU，无需上传。", 12));
                body.AddView(Ui.Button(this, "上传原片和视频 · 服务端生成", () => _ = Run(() => Submit(scan.Id)), false));
            }
        }
        if (!working)
        {
            body.AddView(Ui.Button(this, "本机生成质量：" + (scan.Quality == 1 ? "精细" : "快速"), () =>
                new AlertDialog.Builder(this)!.SetTitle("本机三维生成质量")!.SetItems(new[] { "快速 · 较低计算量，2K 贴图上限", "精细 · 多视角校验与小缺口修复，4K 贴图上限" }, (_, choice) => { ScanStore.Edit(scan.Id, x => x with { Quality = choice.Which }); Render(); })!.Show(), false));
            foreach (var video in Directory.GetFiles(ScanStore.Home(scan.Id), "video-*.mp4"))
            {
                var copy = video; body.AddView(Ui.Button(this, "从原视频重新提取关键照片 · " + Path.GetFileName(video)[..14], () => Reextract(scan.Id, copy), false));
            }
        }
        if (!working && scan.Inputs != null && !scan.RemoteJob.HasValue) body.AddView(Ui.Button(this, "继续上传原片并提交", () => _ = Run(() => Submit(scan.Id)), false));
        if (!working) body.AddView(Ui.Button(this, "清理本机扫描", () =>
        {
            if (busy) return;
            new AlertDialog.Builder(this)!.SetTitle("清理本机扫描？")!.SetMessage("将删除此扫描的本机原片、源视频、模型与记录。云端文件和已经加入传输队列的文件会保留；未上传的本机原片和模型将无法恢复。")!.SetPositiveButton("清理本机", (_, _) => { ScanStore.Delete(scan.Id); selected = null; Render(); })!.SetNegativeButton("取消", (_, _) => { })!.Show();
        }, false));
        if (scan.State == "running") body.AddView(Ui.Button(this, "停止本机生成", () => StartService(new Intent(this, typeof(ReconstructionService)).SetAction("cancel")), false));
        if (scan.State == "remote") body.AddView(Ui.Button(this, "取消服务端任务", () => _ = Run(async () => { using var r = await AppServices.Client.Connection!.Http.DeleteAsync("api/reconstruction/jobs/" + scan.RemoteJob, stop.Token); await CloudConnection.Check(r, stop.Token); await Poll(scan); Render(); }), false));
        if (File.Exists(ScanStore.Model(scan.Id)))
        {
            if (!working && files.Length >= 6 && !scan.RemoteJob.HasValue) body.AddView(Ui.Button(this, "用原片提交服务端精细重建", () => _ = Run(async () => { ScanStore.Edit(scan.Id, x => x with { Quality = 1 }); await Submit(scan.Id); }), false));
            if (!working && files.Length >= 6) body.AddView(Ui.Button(this, "用原片重新生成 · 保留旧模型副本", () => PickBackend(scan.Id), false));
            body.AddView(Ui.Button(this, "旋转／缩放查看模型", () => StartActivity(new Intent(this, typeof(LocalModelActivity)).PutExtra("scan", scan.Id.ToString()))));
            if (!scan.ModelUpload.HasValue) body.AddView(Ui.Button(this, "上传模型到云端", () => _ = Run(async () =>
            {
                await using var input = File.OpenRead(ScanStore.Model(scan.Id)); var job = await AppServices.Client.Stage(input, scan.Name + ".glb", scan.Folder,
                    new("model", ProjectFolderId: scan.Folder.Id, Note: "本机三维重建 · " + scan.Backend + " · 扫描 " + scan.Id, DeviceName: AppServices.DeviceName), "", stop.Token, expectedAccount: scan.Account);
                ScanStore.Edit(scan.Id, x => x with { ModelUpload = job.Id, Message = "模型已加入上传队列，可在传输页查看。" }); AppServices.Kick(); Render();
            }), false));
        }
        if (scan.State == "failed" && scan.RemoteJob.HasValue) body.AddView(Ui.Button(this, "重新提交服务端", () => _ = Run(async () =>
        {
            ScanStore.Edit(scan.Id, x => x with { State = "collecting", RemoteJob = null, RemoteRequestId = Guid.NewGuid() }); await Submit(scan.Id);
        }), false));
        if (scan.ModelUpload.HasValue)
        {
            var job = AppServices.Repository.Jobs(scan.Account).FirstOrDefault(x => x.Id == scan.ModelUpload);
            body.AddView(Ui.Text(this, job?.State == "completed" ? "云端模型：" + job.Completed?.Path : "模型上传：" + (job?.State ?? "请查看传输记录"), 12));
        }
        if (scan.RemoteJob.HasValue && scan.State == "ready") body.AddView(Ui.Button(this, "打开云端模型", () => _ = Run(async () =>
        {
            var jobs = await RemoteJobs(); var model = jobs.FirstOrDefault(x => x.Id == scan.RemoteJob)?.Model; if (model != null) StartActivity(new Intent(this, typeof(PreviewActivity)).PutExtra("id", model.Id.ToString()).PutExtra("path", model.Path));
        }), false));
    }
    private static string State(string value) => value switch { "collecting" => "采集中", "running" => "本机生成中", "remote" => "服务端生成中", "uploading" => "原片上传中", "staging" => "准备原片", "ready" => "模型已就绪", "failed" => "需要处理", _ => value };
    private async Task Run(Func<Task> action, bool alert = true)
    {
        if (busy) return; busy = true;
        try { await action(); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) { if (alert && !IsDestroyed) Ui.Alert(this, "暂未完成", ex.Message); }
        finally { busy = false; }
    }
    private async Task Create()
    {
        var folders = (await AppServices.Client.Folders(stop.Token)).Where(x => (x.Access & 2) != 0).ToList();
        if (folders.Count == 0) throw new IOException("当前账号没有可写的云端目录。");
        var name = Ui.Input(this, "例如：水泵设备扫描"); var picker = new Spinner(this); picker.Adapter = new ArrayAdapter<string>(this, global::Android.Resource.Layout.SimpleSpinnerDropDownItem, folders.Select(x => Ui.PathLabel(x.Path)).ToArray());
        var subject = new Spinner(this); subject.Adapter = new ArrayAdapter<string>(this, global::Android.Resource.Layout.SimpleSpinnerDropDownItem, new[] { "物体／设备（优先）", "房间／现场（试验，建议服务端生成）" });
        var panel = Ui.Column(this); panel.AddView(name); panel.AddView(Ui.Text(this, "云端保存目录", 14)); panel.AddView(picker); panel.AddView(subject);
        var dialog = new AlertDialog.Builder(this)!.SetTitle("新建三维扫描")!.SetView(panel)!.SetPositiveButton("创建", (_, _) => { })!.SetNegativeButton("取消", (_, _) => { })!.Create()!; dialog.Show();
        dialog.GetButton((int)DialogButtonType.Positive)!.Click += (_, _) =>
        {
            try
            {
                var text = CloudPath.Normalize((name.Text ?? "").Trim()); if (text.Contains('/') || text.Length > 80) throw new ArgumentException("请输入不超过 80 字符的名称。");
                var folder = folders[picker.SelectedItemPosition]; var value = new ScanSession(Guid.NewGuid(), AppServices.Client.AccountKey, text, new(folder.Id, folder.Path, folder.Category), DateTimeOffset.UtcNow, subject.SelectedItemPosition == 0 ? "object" : "scene");
                ScanStore.Save(value); selected = value.Id; dialog.Dismiss(); Render();
            }
            catch (Exception ex) { Ui.Alert(this, "名称无效", ex.Message); }
        };
    }
    private void Camera(Guid id, bool video = false)
    {
        if (busy) return;
        try { ScanStore.EnsureCollecting(id); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { Ui.Alert(this, "无法继续采集", ex.Message); return; }
        if (CheckSelfPermission(global::Android.Manifest.Permission.Camera) != Permission.Granted) { selected = id; importing = video ? id : null; RequestPermissions([global::Android.Manifest.Permission.Camera], 972); return; }
        importing = id; StartActivityForResult(new Intent(this, typeof(CameraActivity)).PutExtra("scan", id.ToString()).PutExtra("scanVideo", video), 975);
    }
    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    { base.OnRequestPermissionsResult(requestCode, permissions, grantResults); if (requestCode == 972 && grantResults.Length > 0 && grantResults[0] == Permission.Granted && selected.HasValue) Camera(selected.Value, importing.HasValue); }
    private void PickBackend(Guid id)
    {
        if (busy) return;
        if (caps?.Cpu != true) { Ui.Alert(this, "本机生成不可用", caps?.Error ?? "请等待能力检测完成。"); return; }
        var choices = caps.Vulkan ? new[] { "自动（优先 Vulkan，失败回退 CPU）", "Vulkan GPU", "CPU" } : new[] { "CPU" };
        new AlertDialog.Builder(this)!.SetTitle("选择本机计算方式")!.SetItems(choices, (_, args) =>
        {
            var backend = caps.Vulkan ? new[] { "auto", "vulkan", "cpu" }[args.Which] : "cpu";
            new AlertDialog.Builder(this)!.SetTitle("开始三维重建？")!.SetMessage("这是试验功能，可能需要几分钟并导致手机发热。请保持通风，可从通知栏停止；原片会保留，已有本机模型会保存副本；生成失败会恢复旧模型。")!.SetPositiveButton("开始", (_, _) =>
            {
                ScanStore.Edit(id, x => x with { Backend = backend, State = "collecting" });
                if (OperatingSystem.IsAndroidVersionAtLeast(33) && CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) != Permission.Granted) RequestPermissions([global::Android.Manifest.Permission.PostNotifications], 973);
                StartForegroundService(new Intent(this, typeof(ReconstructionService)).PutExtra("scan", id.ToString()));
            })!.SetNegativeButton("取消", (_, _) => { })!.Show();
        })!.Show();
    }
    protected override async void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode is 974 or 975 && resultCode == Result.Ok && importing.HasValue && data != null)
        {
            var target = importing.Value;
            await Run(async () =>
            {
                if (requestCode == 975)
                {
                    var name = data.GetStringExtra("scanVideo") ?? throw new IOException("没有录像文件。");
                    if (Path.GetFileName(name) != name || !name.StartsWith("video-", StringComparison.Ordinal) || !name.EndsWith(".mp4", StringComparison.Ordinal)) throw new IOException("录像路径无效。");
                    await ExtractVideo(target, Path.Combine(ScanStore.Home(target), name));
                }
                else if (data.Data is { } uri)
                {
                    ScanStore.EnsureCollecting(target);
                    var path = Path.Combine(ScanStore.Home(target), "video-" + Guid.NewGuid().ToString("N") + ".mp4");
                    try
                    {
                        await using var source = ContentResolver!.OpenInputStream(uri) ?? throw new IOException("视频不可读。");
                        await using (var output = File.Create(path + ".partial"))
                        {
                            var buffer = new byte[65536]; long size = 0; int n;
                            while ((n = await source.ReadAsync(buffer, stop.Token)) > 0) { size += n; if (size > 512L * 1024 * 1024) throw new IOException("视频不能超过 512 MB。"); await output.WriteAsync(buffer.AsMemory(0, n), stop.Token); }
                        }
                        File.Move(path + ".partial", path); await ExtractVideo(target, path);
                    }
                    finally { if (File.Exists(path + ".partial")) File.Delete(path + ".partial"); }
                }
            }); return;
        }
        if (requestCode != 971 || resultCode != Result.Ok || !importing.HasValue || data == null) return;
        var id = importing.Value; await Run(async () =>
        {
            var uris = new List<global::Android.Net.Uri>(); if (data.ClipData != null) for (var i = 0; i < data.ClipData.ItemCount; i++) { var uri = data.ClipData.GetItemAt(i)?.Uri; if (uri != null) uris.Add(uri); } else if (data.Data != null) uris.Add(data.Data);
            var added = 0;
            foreach (var uri in uris)
            {
                ScanStore.EnsureCollecting(id); var kind = ContentResolver!.GetType(uri); var extension = kind == "image/png" ? ".png" : ".jpg";
                var path = Path.Combine(ScanStore.Photos(id), "import-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "-" + Guid.NewGuid().ToString("N") + extension); var partial = path + ".partial";
                try
                {
                    await using var source = ContentResolver.OpenInputStream(uri) ?? throw new IOException("照片不可读。");
                    await using (var output = File.Create(partial)) { var buffer = new byte[65536]; long size = 0; int n; while ((n = await source.ReadAsync(buffer, stop.Token)) > 0) { size += n; if (size > 40 * 1024 * 1024) throw new IOException("单张照片不能超过 40 MB。"); await output.WriteAsync(buffer.AsMemory(0, n), stop.Token); } }
                    if (kind is not ("image/jpeg" or "image/png"))
                    {
                        using var bitmap = await CloudMedia.DecodeImage(partial, 4096) ?? throw new IOException("无法解码此照片。");
                        using var output = File.Create(path); bitmap.Compress(global::Android.Graphics.Bitmap.CompressFormat.Jpeg!, 94, output);
                    }
                    else File.Move(partial, path);
                    added++;
                }
                finally { if (File.Exists(partial)) File.Delete(partial); }
            }
            ScanStore.Edit(id, x => x with { Message = "已添加 " + added + " 张原片。请按拍摄顺序选择，避免不同物体混在一起。" }); Render();
        });
    }
    private async Task ExtractVideo(Guid id, string path)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        var progress = Ui.Text(this, "正在检查视频…", 15); progress.SetPadding(Ui.Dp(this, 22), Ui.Dp(this, 20), Ui.Dp(this, 22), Ui.Dp(this, 20));
        var dialog = new AlertDialog.Builder(this)!.SetTitle("自动筛选重建照片")!.SetView(progress)!.SetNegativeButton("停止选帧", (_, _) => cancel.Cancel())!.Create()!;
        dialog.SetCanceledOnTouchOutside(false); dialog.CancelEvent += (_, _) => cancel.Cancel(); dialog.Show();
        try
        {
            var summary = await Task.Run(() => ScanVideo.Extract(id, path, text => RunOnUiThread(() => { if (!IsDestroyed) progress.Text = text; }), cancel.Token));
            ScanStore.Edit(id, x => x with { Message = summary });
        }
        catch (OperationCanceledException) { if (!stop.IsCancellationRequested) ScanStore.Edit(id, x => x with { Message = "已停止选帧，视频和原有照片已保留。" }); }
        finally { if (!IsDestroyed) { dialog.Dismiss(); Render(); } }
    }
    private void Reextract(Guid id, string video)
    {
        if (busy) return;
        new AlertDialog.Builder(this)!.SetTitle("重新提取关键照片")!.SetItems(new[] { "精细选帧 · 最多 96 张", "快速选帧 · 最多 48 张" }, (_, choice) => _ = Run(async () =>
        {
            var revision = await Task.Run(() => ScanStore.CreateVideoRevision(id, video, choice.Which == 0 ? 1 : 0, stop.Token));
            selected = revision.Session.Id; Render(); await ExtractVideo(revision.Session.Id, revision.Video);
        }))!.SetNegativeButton("取消", (_, _) => { })!.Show();
    }
    private async Task<List<ReconstructionJob>> RemoteJobs()
    { using var r = await AppServices.Client.Connection!.Http.GetAsync("api/reconstruction/jobs", stop.Token); await CloudConnection.Check(r, stop.Token); return await r.Content.ReadFromJsonAsync(ApiJson.Default.ListReconstructionJob, stop.Token) ?? []; }
    private async Task Submit(Guid id)
    {
        var scan = ScanStore.Get(id); using var capsResponse = await AppServices.Client.Connection!.Http.GetAsync("api/reconstruction/capabilities", stop.Token); await CloudConnection.Check(capsResponse, stop.Token);
        var available = await capsResponse.Content.ReadFromJsonAsync(ApiJson.Default.ReconstructionCapabilities, stop.Token); if (available?.Cpu != true) throw new IOException(available?.Error ?? "服务端引擎不可用。");
        scan = ScanStore.Edit(id, x => x with { Inputs = x.Inputs ?? ScanStore.Files(id).Select(Path.GetFileName).Select(x => x!).ToList(), State = "staging", Message = "正在准备原片上传队列" });
        var staged = scan.UploadJobs?.ToList() ?? []; var files = scan.Inputs!.Select(x => Path.Combine(ScanStore.Photos(id), x)).ToArray();
        try
        {
        if (staged.Count > files.Length) throw new IOException("原片发生变化，请新建扫描。");
        for (var i = staged.Count; i < files.Length; i++)
        {
            await using var input = File.OpenRead(files[i]); var captured = Path.GetFileName(files[i]).StartsWith("capture-", StringComparison.Ordinal);
            var job = await AppServices.Client.Stage(input, Path.GetFileName(files[i]), scan.Folder, new(captured ? "scan" : "album", CapturedUtc: captured ? File.GetCreationTimeUtc(files[i]) : null,
                ProjectFolderId: scan.Folder.Id, Note: "三维扫描原片 · " + scan.Id, DeviceName: AppServices.DeviceName, CaptureId: Guid.NewGuid()), scan.Name + "-" + scan.Id.ToString("N")[..8] + "/原始照片", stop.Token, expectedAccount: scan.Account);
            staged.Add(job.Id); ScanStore.Edit(id, x => x with { UploadJobs = staged.ToList() });
        }
        var videos = Directory.GetFiles(ScanStore.Home(id), "video-*.mp4").Order(StringComparer.Ordinal).ToArray();
        var videoJobs = ScanStore.Get(id).SourceVideoJobs?.ToList() ?? [];
        if (videoJobs.Count > videos.Length) throw new IOException("源视频发生变化，请新建扫描。");
        for (var i = videoJobs.Count; i < videos.Length; i++)
        {
            await using var input = File.OpenRead(videos[i]);
            var job = await AppServices.Client.Stage(input, Path.GetFileName(videos[i]), scan.Folder,
                new("album", ProjectFolderId: scan.Folder.Id, Note: "三维扫描原视频 · " + scan.Id, DeviceName: AppServices.DeviceName, CaptureId: Guid.NewGuid()),
                scan.Name + "-" + scan.Id.ToString("N")[..8] + "/原始视频", stop.Token, expectedAccount: scan.Account);
            videoJobs.Add(job.Id); ScanStore.Edit(id, x => x with { SourceVideoJobs = videoJobs.ToList() });
        }
        ScanStore.Edit(id, x => x with { State = "uploading", Message = "原片和源视频进入持久上传队列，上传完成后自动提交服务端。" }); AppServices.Kick(); Render();
        }
        catch (Exception ex) { ScanStore.Edit(id, x => x with { State = "failed", Message = "原片准备未完成，可继续提交：" + ex.Message }); throw; }
    }
    private Task Poll(ScanSession scan) => ScanRemote.Poll(scan, stop.Token);

}

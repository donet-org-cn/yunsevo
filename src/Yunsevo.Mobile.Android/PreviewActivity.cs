using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Database;
using Android.Graphics;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Webkit;
using Android.Widget;
using Yunsevo.Contracts;

namespace Yunsevo.Mobile.Android;

[Activity(Label = "照片与文件", Exported = false, ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
public sealed class PreviewActivity : Activity
{
    private readonly CancellationTokenSource stop = new();
    private CancellationTokenSource? loading;
    private ZoomImageView? zoomImage;
    private VideoView? video;
    private ModelView? modelView;
    private FrameLayout scene = null!;
    private TextView caption = null!, status = null!;
    private Button save = null!, share = null!, details = null!, previous = null!, next = null!;
    private string? savedFile, savedName, savedMime;
    private string albumFolder = "";
    private bool exporting;
    private int generation;
    private CloudEntry? entry;
    private MediaNeighbors? neighbors;
    protected override async void OnCreate(Bundle? state)
    {
        base.OnCreate(state);
        var body = new LinearLayout(this) { Orientation = Orientation.Vertical }; body.SetFitsSystemWindows(true); body.SetBackgroundColor(Color.Black);
        scene = new GalleryFrame(this, () => (zoomImage?.IsAtBaseScale ?? true) && neighbors != null, direction => _ = Navigate(direction)); scene.SetBackgroundColor(Color.Rgb(9, 12, 17)); body.AddView(scene, new LinearLayout.LayoutParams(-1, 0, 1));
        var panel = Ui.Column(this, 12); panel.SetBackgroundColor(Color.White);
        caption = Ui.Text(this, "", 15, true); caption.SetSingleLine(true); caption.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End; panel.AddView(caption);
        status = Ui.Text(this, "", 11); status.SetSingleLine(true); status.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End; panel.AddView(status);
        var actions = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        Button Action(string text, string description, System.Action callback, float weight)
        {
            var button = Ui.Button(this, text, callback, false); button.ContentDescription = description; button.TextSize = 13;
            button.LayoutParameters = new LinearLayout.LayoutParams(0, Ui.Dp(this, 42), weight) { LeftMargin = Ui.Dp(this, 3), RightMargin = Ui.Dp(this, 3), TopMargin = Ui.Dp(this, 7) };
            actions.AddView(button); return button;
        }
        previous = Action("‹", "上一张照片", () => _ = Navigate(-1), .65f);
        save = Action("保存", "保存到手机", () => _ = SaveToPhone(), 1);
        share = Action("分享", "分享照片或文件", () => { if (savedFile != null) CloudMedia.Share(this, savedFile, savedName!, savedMime!); }, 1);
        details = Action("信息", "查看拍摄与上传详情", ShowDetails, 1);
        next = Action("›", "下一张照片", () => _ = Navigate(1), .65f);
        panel.AddView(actions); body.AddView(panel); SetContentView(body);
        var remote = state?.GetString("path") ?? Intent!.GetStringExtra("path")!;
        var id = Guid.Parse(state?.GetString("id") ?? Intent!.GetStringExtra("id")!);
        albumFolder = state?.GetString("albumFolder") ?? Intent!.GetStringExtra("albumFolder") ?? CloudPath.Parent(remote) ?? "";
        await Open(id, remote);
    }
    private async Task Open(Guid id, string remote)
    {
        loading?.Cancel(); loading?.Dispose(); loading = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        var ct = loading.Token; var opening = ++generation; ClearMedia(); entry = null; neighbors = null; savedFile = null; savedName = Path.GetFileName(remote); savedMime = null;
        previous.Enabled = next.Enabled = save.Enabled = share.Enabled = details.Enabled = false; caption.Text = savedName; status.Text = "正在读取…";
        var progress = Ui.Text(this, "正在加载…", 14); progress.SetTextColor(Color.LightGray); progress.Gravity = GravityFlags.Center; scene.AddView(progress, new FrameLayout.LayoutParams(-1, -1));
        _ = LoadNeighbors(id, opening, ct);
        try
        {
            long updated = 0;
            var download = await Task.Run(() => CloudMedia.Download(id, remote, ct, (bytes, total) =>
            {
                var now = System.Environment.TickCount64; if (now - updated < 250) return; updated = now;
                RunOnUiThread(() => { if (opening == generation) progress.Text = "正在下载 · " + Ui.Size(bytes) + "/" + Ui.Size(total); });
            }), ct);
            ct.ThrowIfCancellationRequested(); if (opening != generation) return;
            entry = download.Entry; savedFile = download.File; savedName = Path.GetFileName(entry.Path); savedMime = CloudMedia.Mime(entry.Path); caption.Text = savedName;
            status.Text = (entry.Origin?.Photographer is { } who ? "拍摄人：" + who.DisplayName + " · " : "") + (entry.Origin?.CapturedUtc ?? entry.ModifiedUtc).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            scene.RemoveAllViews();
            if (savedMime == "model/gltf-binary") { modelView = new(this, download.File); scene.AddView(modelView, new FrameLayout.LayoutParams(-1, -1)); }
            else if (savedMime.StartsWith("image/", StringComparison.Ordinal))
            {
                var bitmap = await CloudMedia.DecodeImage(download.File, 4096);
                if (ct.IsCancellationRequested || opening != generation) { bitmap?.Dispose(); return; }
                if (bitmap == null) Message("系统暂不支持此图片格式，可保存或使用其他应用打开。");
                else { zoomImage = new(this, bitmap) { Navigate = direction => _ = Navigate(direction) }; scene.AddView(zoomImage, new FrameLayout.LayoutParams(-1, -1)); }
            }
            else if (savedMime.StartsWith("video/", StringComparison.Ordinal) || savedMime.StartsWith("audio/", StringComparison.Ordinal))
            {
                video = new(this); video.SetVideoURI(AppFileProvider.UriFor(download.File)); var controller = new MediaController(this); controller.SetAnchorView(video); video.SetMediaController(controller);
                scene.AddView(video, new FrameLayout.LayoutParams(-1, -1, GravityFlags.Center)); video.Start();
            }
            else if (new[] { ".txt", ".md", ".log", ".csv", ".json", ".xml", ".cs", ".yaml", ".yml" }.Contains(Path.GetExtension(entry.Path).ToLowerInvariant()))
            {
                using var input = File.OpenRead(download.File); var bytes = new byte[Math.Min(1048576L, input.Length)]; await input.ReadExactlyAsync(bytes, ct);
                var text = Ui.Text(this, System.Text.Encoding.UTF8.GetString(bytes), 14); text.SetTextColor(Color.White); text.SetTextIsSelectable(true); text.SetPadding(24, 24, 24, 24);
                var scroll = new ScrollView(this); scroll.AddView(text); scene.AddView(scroll, new FrameLayout.LayoutParams(-1, -1));
            }
            else Message("此格式可在“信息”中选择其他应用打开，或保存到手机。");
            save.Enabled = share.Enabled = details.Enabled = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (opening == generation) { Message("无法打开文件\n" + ex.Message); status.Text = "加载未完成"; } }
    }
    private void Message(string value)
    { scene.RemoveAllViews(); var text = Ui.Text(this, value, 14); text.SetTextColor(Color.White); text.Gravity = GravityFlags.Center; text.SetPadding(30, 30, 30, 30); scene.AddView(text, new FrameLayout.LayoutParams(-1, -1)); }
    private async Task LoadNeighbors(Guid id, int opening, CancellationToken ct)
    {
        try
        {
            var result = await AppServices.Client.MediaNeighbors(id, albumFolder, ct); if (opening != generation || ct.IsCancellationRequested) return;
            neighbors = result; previous.Enabled = result.Previous != null; next.Enabled = result.Next != null;
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }
    private async Task Navigate(int direction)
    {
        var target = direction < 0 ? neighbors?.Previous : neighbors?.Next;
        if (target == null) { Toast.MakeText(this, direction < 0 ? "已经是第一项" : "已经是最后一项", ToastLength.Short)!.Show(); return; }
        await Open(target.Id, target.Path);
    }
    private void ClearMedia()
    { modelView?.Release(); modelView = null; zoomImage?.ReleaseImage(); zoomImage = null; try { video?.StopPlayback(); } catch (Java.Lang.Exception) { } video = null; scene?.RemoveAllViews(); }
    private void ShowDetails()
    {
        if (entry == null || savedFile == null) return;
        var body = Ui.Column(this, 18); body.Background = Ui.Shape(Color.White, Ui.Dp(this, 18)); body.AddView(Ui.Text(this, savedName!, 17, true)); body.AddView(Ui.Text(this, Ui.Description(entry), 13));
        if (savedMime!.StartsWith("image/", StringComparison.Ordinal))
        {
            try
            {
                using var exif = new global::Android.Media.ExifInterface(savedFile); var values = new List<string>();
                foreach (var (tag, label) in new[] { ("Make", "制造商"), ("Model", "机型"), ("FNumber", "光圈"), ("ExposureTime", "曝光时间"), ("ISOSpeedRatings", "ISO"), ("FocalLength", "焦距"), ("DateTimeOriginal", "原始拍摄时间") })
                    if (exif.GetAttribute(tag) is { Length: > 0 } value) values.Add(label + "：" + value);
                if (values.Count > 0) body.AddView(Ui.Text(this, string.Join("\n", values), 13));
            }
            catch (Java.IO.IOException) { }
        }
        var file = savedFile; var mime = savedMime; var name = savedName;
        body.AddView(Ui.Button(this, "使用其他应用打开", () =>
        {
            try { StartActivity(new Intent(Intent.ActionView).SetDataAndType(AppFileProvider.UriFor(file, name), mime)!.AddFlags(ActivityFlags.GrantReadUriPermission)); }
            catch (ActivityNotFoundException) { Ui.Alert(this, "没有可用应用", "请安装能打开此文件类型的应用。"); }
        }, false));
        body.AddView(Ui.Button(this, "另存为文件", SaveAs, false));
        var scroll = new ScrollView(this); scroll.AddView(body); var dialog = new AlertDialog.Builder(this)!.SetView(scroll)!.SetPositiveButton("完成", (_, _) => { })!.Create()!;
        dialog.Show(); dialog.Window!.SetGravity(GravityFlags.Bottom); dialog.Window.SetLayout(-1, (int)(Resources!.DisplayMetrics!.HeightPixels * .72f));
    }
    protected override void OnSaveInstanceState(Bundle state)
    { if (entry != null) { state.PutString("id", entry.Id.ToString()); state.PutString("path", entry.Path); state.PutString("albumFolder", albumFolder); } base.OnSaveInstanceState(state); }
    private async Task SaveToPhone()
    {
        if (exporting || savedFile == null) return;
        if (!savedMime!.StartsWith("image/", StringComparison.Ordinal) && !savedMime.StartsWith("video/", StringComparison.Ordinal)) { SaveAs(); return; }
        if (!OperatingSystem.IsAndroidVersionAtLeast(29) && CheckSelfPermission(global::Android.Manifest.Permission.WriteExternalStorage) != global::Android.Content.PM.Permission.Granted)
        { RequestPermissions([global::Android.Manifest.Permission.WriteExternalStorage], 903); return; }
        exporting = true;
        try { await CloudMedia.SaveToGallery(this, savedFile, savedName!, savedMime!, stop.Token); Ui.Alert(this, "保存成功", "已保存到系统相册的 Yunsevo 目录。"); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Ui.Alert(this, "保存未完成", ex.Message); }
        finally { exporting = false; }
    }
    private void SaveAs() => StartActivityForResult(new Intent(Intent.ActionCreateDocument).AddCategory(Intent.CategoryOpenable)!.SetType(savedMime)!.PutExtra(Intent.ExtraTitle, savedName), 904);
    public override void OnRequestPermissionsResult(int code, string[] permissions, global::Android.Content.PM.Permission[] grants)
    {
        base.OnRequestPermissionsResult(code, permissions, grants);
        if (code == 903 && grants.All(x => x == global::Android.Content.PM.Permission.Granted)) _ = SaveToPhone();
        else if (code == 903) SaveAs();
    }
    protected override async void OnActivityResult(int request, Result result, Intent? data)
    {
        base.OnActivityResult(request, result, data);
        if (request != 904 || result != Result.Ok || data?.Data == null || savedFile == null) return;
        try
        {
            await using var input = File.OpenRead(savedFile); await using var output = ContentResolver!.OpenOutputStream(data.Data) ?? throw new IOException("无法写入所选位置。");
            await input.CopyToAsync(output, stop.Token); Ui.Alert(this, "保存成功", savedName!);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Ui.Alert(this, "保存未完成", ex.Message); }
    }
    protected override void OnDestroy() { stop.Cancel(); loading?.Cancel(); ClearMedia(); base.OnDestroy(); }
}

[ContentProvider(["app.yunsevo.drive.files"], Exported = false, GrantUriPermissions = true)]
public sealed class AppFileProvider : ContentProvider
{
    public static global::Android.Net.Uri UriFor(string file, string? displayName = null) => global::Android.Net.Uri.Parse("content://app.yunsevo.drive.files/" + Uri.EscapeDataString(Path.GetFileName(file)))!.BuildUpon()!.AppendQueryParameter("name", displayName ?? Path.GetFileName(file))!.Build()!;
    private string Resolve(global::Android.Net.Uri? uri)
    {
        var name = uri?.LastPathSegment ?? "";
        if (name.Length < 32 || !Guid.TryParseExact(name[..32], "N", out _) || name != Path.GetFileName(name) || name.Contains('\\'))
            throw new Java.Lang.SecurityException("Invalid preview file");
        return Path.Combine(Context!.FilesDir!.AbsolutePath, "previews", name);
    }
    public override bool OnCreate() => true;
    public override string? GetType(global::Android.Net.Uri uri) => MimeTypeMap.Singleton!.GetMimeTypeFromExtension(Path.GetExtension(Resolve(uri)).TrimStart('.')) ?? "application/octet-stream";
    public override ParcelFileDescriptor? OpenFile(global::Android.Net.Uri uri, string mode)
    {
        if (mode != "r") throw new Java.Lang.SecurityException("Read-only preview");
        return ParcelFileDescriptor.Open(new Java.IO.File(Resolve(uri)), ParcelFileMode.ReadOnly);
    }
    public override ICursor? Query(global::Android.Net.Uri uri, string[]? projection, string? selection, string[]? selectionArgs, string? sortOrder)
    {
        var file = new FileInfo(Resolve(uri)); var cursor = new MatrixCursor([IOpenableColumns.DisplayName, IOpenableColumns.Size]);
        cursor.AddRow(new Java.Lang.Object[] { new Java.Lang.String(Yunsevo.Mobile.Core.MobileRepository.FileName(uri.GetQueryParameter("name") ?? file.Name)), Java.Lang.Long.ValueOf(file.Length) }); return cursor;
    }
    public override global::Android.Net.Uri? Insert(global::Android.Net.Uri uri, ContentValues? values) => throw new NotSupportedException();
    public override int Delete(global::Android.Net.Uri uri, string? selection, string[]? selectionArgs) => throw new NotSupportedException();
    public override int Update(global::Android.Net.Uri uri, ContentValues? values, string? selection, string[]? selectionArgs) => throw new NotSupportedException();
}

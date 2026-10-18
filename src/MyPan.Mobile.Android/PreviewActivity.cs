using Android.App;
using Android.Content;
using Android.Database;
using Android.Graphics;
using Android.OS;
using Android.Provider;
using Android.Webkit;
using Android.Widget;
using System.Net.Http.Json;
using MyPan.Contracts;
using MyPan.Sync;

namespace MyPan.Mobile.Android;

[Activity(Label = "文件预览", Exported = false)]
public sealed class PreviewActivity : Activity
{
    private readonly CancellationTokenSource stop = new();
    protected override async void OnCreate(Bundle? state)
    {
        base.OnCreate(state);
        var body = Ui.Column(this); body.SetFitsSystemWindows(true); body.SetBackgroundColor(Ui.Background);
        var caption = Ui.Text(this, "正在读取文件…", 18, true); body.AddView(caption);
        var status = Ui.Text(this, ""); body.AddView(status); SetContentView(body);
        try
        {
            var id = Guid.Parse(Intent!.GetStringExtra("id")!);
            var remote = Intent.GetStringExtra("path")!;
            var connection = AppServices.Client.Connection ?? throw new InvalidOperationException("请先登录。");
            using var info = await connection.Http.GetAsync("api/browser/entry?path=" + Uri.EscapeDataString(remote), stop.Token);
            await CloudConnection.Check(info, stop.Token);
            var entry = (await info.Content.ReadFromJsonAsync(ApiJson.Default.EntryLookup, stop.Token))?.Entry ?? throw new IOException("文件不存在或无权访问。");
            if (entry.Id != id) throw new IOException("文件已更新，请刷新后重试。");
            caption.Text = Path.GetFileName(entry.Path);
            status.Text = string.Join("\n", Ui.Description(entry).Split('\n').Take(entry.Origin?.Photographer == null ? 2 : 3));
            body.AddView(Ui.Button(this, "查看拍摄与上传详情", () => Ui.Alert(this, Path.GetFileName(entry.Path), Ui.Description(entry)), false));
            var extension = Path.GetExtension(entry.Path).ToLowerInvariant();
            var mime = MimeTypeMap.Singleton!.GetMimeTypeFromExtension(extension.TrimStart('.')) ?? "application/octet-stream";
            var cached = Path.Combine(FilesDir!.AbsolutePath, "previews", id.ToString("N") + "-" + entry.Version + extension);
            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            if (!File.Exists(cached) || new FileInfo(cached).Length != entry.Size)
            {
                using var response = await connection.Http.GetAsync("api/content/" + id + "?version=" + Uri.EscapeDataString(entry.Version), HttpCompletionOption.ResponseHeadersRead, stop.Token);
                await CloudConnection.Check(response, stop.Token);
                if (entry.Size > new StatFs(FilesDir.AbsolutePath).AvailableBytes - 64L * 1024 * 1024) throw new IOException("没有足够空间预览此文件。");
                var partial = cached + ".partial";
                try
                {
                    await using var input = await response.Content.ReadAsStreamAsync(stop.Token);
                    await using var output = File.Create(partial); var buffer = new byte[131072]; long total = 0; int count;
                    while ((count = await input.ReadAsync(buffer, stop.Token)) != 0)
                    { await output.WriteAsync(buffer.AsMemory(0, count), stop.Token); total += count; caption.Text = Path.GetFileName(entry.Path) + " · " + Ui.Size(total) + "/" + Ui.Size(entry.Size); }
                    output.Close(); if (total != entry.Size) throw new IOException("下载不完整，请重试。");
                    File.Move(partial, cached, true);
                }
                finally { if (File.Exists(partial)) File.Delete(partial); }
            }
            caption.Text = Path.GetFileName(entry.Path);
            if (mime.StartsWith("image/", StringComparison.Ordinal))
            {
                var options = new BitmapFactory.Options { InJustDecodeBounds = true };
                await BitmapFactory.DecodeFileAsync(cached, options);
                var sample = 1; while (Math.Max(options.OutWidth, options.OutHeight) / sample > 2048) sample *= 2;
                options.InJustDecodeBounds = false; options.InSampleSize = sample;
                var image = new ImageView(this); image.SetImageBitmap(await BitmapFactory.DecodeFileAsync(cached, options));
                image.SetScaleType(ImageView.ScaleType.FitCenter);
                body.AddView(image, new LinearLayout.LayoutParams(-1, 0, 1));
            }
            else if (mime.StartsWith("video/", StringComparison.Ordinal) || mime.StartsWith("audio/", StringComparison.Ordinal))
            {
                var video = new VideoView(this); video.SetVideoURI(AppFileProvider.UriFor(cached));
                var controller = new MediaController(this); controller.SetAnchorView(video); video.SetMediaController(controller);
                body.AddView(video, new LinearLayout.LayoutParams(-1, 0, 1)); video.Start();
            }
            else if (new[] { ".txt", ".md", ".log", ".csv", ".json", ".xml", ".cs", ".yaml", ".yml" }.Contains(extension))
            {
                using var stream = File.OpenRead(cached); var bytes = new byte[Math.Min(1048576L, stream.Length)]; await stream.ReadExactlyAsync(bytes, stop.Token);
                var text = Ui.Text(this, System.Text.Encoding.UTF8.GetString(bytes), 14); text.SetTextIsSelectable(true);
                var scroll = new ScrollView(this); scroll.AddView(text); body.AddView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));
            }
            body.AddView(Ui.Button(this, "使用其他应用打开", () =>
            {
                try { StartActivity(new Intent(Intent.ActionView).SetDataAndType(AppFileProvider.UriFor(cached), mime)!.AddFlags(ActivityFlags.GrantReadUriPermission)); }
                catch (ActivityNotFoundException) { Ui.Alert(this, "没有可用应用", "请安装能打开此文件类型的应用。"); }
            }, false));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { caption.Text = "无法预览"; status.Text = ex.Message; }
    }
    protected override void OnDestroy() { stop.Cancel(); base.OnDestroy(); }
}

[ContentProvider(["app.yunsevo.drive.files"], Exported = false, GrantUriPermissions = true)]
public sealed class AppFileProvider : ContentProvider
{
    public static global::Android.Net.Uri UriFor(string file) => global::Android.Net.Uri.Parse("content://app.yunsevo.drive.files/" + Uri.EscapeDataString(Path.GetFileName(file)))!;
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
        cursor.AddRow(new Java.Lang.Object[] { new Java.Lang.String(file.Name), Java.Lang.Long.ValueOf(file.Length) }); return cursor;
    }
    public override global::Android.Net.Uri? Insert(global::Android.Net.Uri uri, ContentValues? values) => throw new NotSupportedException();
    public override int Delete(global::Android.Net.Uri uri, string? selection, string[]? selectionArgs) => throw new NotSupportedException();
    public override int Update(global::Android.Net.Uri uri, ContentValues? values, string? selection, string[]? selectionArgs) => throw new NotSupportedException();
}

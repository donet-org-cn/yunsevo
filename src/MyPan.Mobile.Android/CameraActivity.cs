using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.Locations;
using Android.Media;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;
using MyPan.Contracts;
using MyPan.Mobile.Core;

namespace MyPan.Mobile.Android;

[Activity(Label = "工程水印相机", Theme = "@style/Yunsevo.Camera", Exported = false, ScreenOrientation = ScreenOrientation.Portrait)]
public sealed class CameraActivity : Activity, TextureView.ISurfaceTextureListener
{
    private TextureView texture = null!;
    private TextView overlay = null!, message = null!;
    private CameraDevice? camera;
    private CameraCaptureSession? session;
    private CaptureRequest.Builder? preview;
    private ImageReader? reader;
    private HandlerThread? thread;
    private Handler? handler;
    private Surface? surface;
    private bool opened, capturing, flash, front;
    private int sensorOrientation = 90;
    private MobileOptions options = null!;
    private FolderSelection project = null!;
    private string account = "", photographer = "";
    private CaptureContext? current;
    private LocationUpdates? locationUpdates;
    private sealed record CaptureContext(DateTimeOffset Time, Guid Id, MobileOptions Options, string Watermark, double? Latitude, double? Longitude, string? Location);
    protected override void OnCreate(Bundle? state)
    {
        base.OnCreate(state);
        var client = AppServices.Client;
        if (client.Account == null || client.Options.Project == null) { Finish(); return; }
        options = client.Options; project = options.Project; account = client.AccountKey; photographer = client.Account.Login.User.DisplayName;
        var body = new LinearLayout(this) { Orientation = Orientation.Vertical }; body.SetFitsSystemWindows(true);
        body.SetBackgroundColor(Color.Rgb(14, 27, 35));
        var title = Ui.Text(this, "工程：" + Path.GetFileName(project.Path), 18, true); title.SetTextColor(Color.White); title.SetPadding(20, 14, 20, 14); body.AddView(title);
        var frame = new FrameLayout(this);
        texture = new(this) { SurfaceTextureListener = this }; frame.AddView(texture, new FrameLayout.LayoutParams(-1, -1));
        overlay = Ui.Text(this, "", 15); overlay.SetTextColor(Color.White); overlay.SetPadding(24, 16, 24, 16);
        overlay.SetBackgroundColor(Color.Argb(150, 13, 33, 41));
        frame.AddView(overlay, new FrameLayout.LayoutParams(-1, -2, GravityFlags.Bottom));
        body.AddView(frame, new LinearLayout.LayoutParams(-1, 0, 1));
        message = Ui.Text(this, "选择的工程目录：" + Ui.PathLabel(project.Path), 12); message.SetTextColor(Color.White); message.SetPadding(20, 8, 20, 8); body.AddView(message);
        var buttons = Ui.Column(this, 14);
        buttons.AddView(Ui.Button(this, "●  拍照并上传", Capture));
        var tools = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var switcher = Ui.Button(this, "切换镜头", () => { front = !front; CloseCamera(); OpenCamera(); }, false);
        var torch = Ui.Button(this, "补光灯", () => { flash = !flash; UpdatePreview(); }, false);
        switcher.LayoutParameters = new LinearLayout.LayoutParams(0, Ui.Dp(this, 46), 1);
        torch.LayoutParameters = new LinearLayout.LayoutParams(0, Ui.Dp(this, 46), 1) { LeftMargin = Ui.Dp(this, 12) };
        tools.AddView(switcher); tools.AddView(torch); buttons.AddView(tools); body.AddView(buttons); SetContentView(body);
        UpdateOverlay();
    }
    protected override void OnResume()
    {
        base.OnResume(); if (texture?.IsAvailable == true) OpenCamera();
        if (options?.IncludeLocation == true && CheckSelfPermission(global::Android.Manifest.Permission.AccessCoarseLocation) == Permission.Granted)
        {
            try
            {
                var manager = (LocationManager)GetSystemService(LocationService)!;
                var precise = CheckSelfPermission(global::Android.Manifest.Permission.AccessFineLocation) == Permission.Granted;
                var provider = precise && manager.IsProviderEnabled(LocationManager.GpsProvider) ? LocationManager.GpsProvider : LocationManager.NetworkProvider;
                if (manager.IsProviderEnabled(provider)) { locationUpdates = new(); manager.RequestLocationUpdates(provider, 3000, 3, locationUpdates, Looper.MainLooper); }
            }
            catch (Java.Lang.SecurityException) { }
        }
    }
    protected override void OnPause()
    {
        if (locationUpdates != null) { ((LocationManager)GetSystemService(LocationService)!).RemoveUpdates(locationUpdates); locationUpdates.Dispose(); locationUpdates = null; }
        CloseCamera(); base.OnPause();
    }
    public void OnSurfaceTextureAvailable(SurfaceTexture surfaceTexture, int width, int height) => OpenCamera();
    public bool OnSurfaceTextureDestroyed(SurfaceTexture surfaceTexture) { CloseCamera(); return true; }
    public void OnSurfaceTextureSizeChanged(SurfaceTexture surfaceTexture, int width, int height) { }
    public void OnSurfaceTextureUpdated(SurfaceTexture surfaceTexture) { }
    private CaptureContext ContextForCapture()
    {
        double? latitude = null, longitude = null; string? location = null;
        if (options.IncludeLocation && CheckSelfPermission(global::Android.Manifest.Permission.AccessCoarseLocation) == Permission.Granted)
        {
            try
            {
                var manager = (LocationManager)GetSystemService(LocationService)!;
                var sample = manager.GetProviders(true)!.Select(x => manager.GetLastKnownLocation(x)).Where(x => x != null)
                    .OrderByDescending(x => x!.Time).FirstOrDefault();
                if (sample != null && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - sample.Time < 5 * 60 * 1000)
                {
                    latitude = sample.Latitude; longitude = sample.Longitude;
                    location = latitude.Value.ToString("F6", System.Globalization.CultureInfo.InvariantCulture) + ", " + longitude.Value.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            catch (Java.Lang.SecurityException) { }
        }
        var time = DateTimeOffset.UtcNow;
        if (options.IncludeLocation && location == null) location = "定位暂不可用";
        return new(time, Guid.NewGuid(), options,
            MobileRepository.Watermark(Path.GetFileName(project.Path), photographer, time, options, location), latitude, longitude, location);
    }
    private void UpdateOverlay()
    {
        if (IsFinishing || overlay == null) return;
        overlay.Text = ContextForCapture().Watermark;
        overlay.PostDelayed(UpdateOverlay, 1000);
    }
    private void OpenCamera()
    {
        if (opened || !texture.IsAvailable || CheckSelfPermission(global::Android.Manifest.Permission.Camera) != Permission.Granted) return;
        try
        {
            opened = true; thread = new("YunsevoCamera"); thread.Start(); handler = new(thread.Looper!);
            var manager = (CameraManager)GetSystemService(CameraService)!;
            var id = manager.GetCameraIdList()!.FirstOrDefault(id =>
                ((Java.Lang.Integer?)manager.GetCameraCharacteristics(id).Get(CameraCharacteristics.LensFacing))?.IntValue() == (int)(front ? LensFacing.Front : LensFacing.Back))
                ?? manager.GetCameraIdList()!.First();
            var characteristics = manager.GetCameraCharacteristics(id);
            sensorOrientation = ((Java.Lang.Integer?)characteristics.Get(CameraCharacteristics.SensorOrientation))?.IntValue() ?? 90;
            var map = (StreamConfigurationMap?)characteristics.Get(CameraCharacteristics.ScalerStreamConfigurationMap);
            var sizes = map?.GetOutputSizes((int)ImageFormatType.Jpeg);
            var size = sizes?.Where(x => Math.Max(x.Width, x.Height) <= 4096).OrderByDescending(x => (long)x.Width * x.Height).FirstOrDefault() ?? new global::Android.Util.Size(1920, 1080);
            reader = ImageReader.NewInstance(size.Width, size.Height, ImageFormatType.Jpeg, 2);
            reader.SetOnImageAvailableListener(new ImageListener(this), handler);
            texture.SurfaceTexture!.SetDefaultBufferSize(1280, 720);
            surface = new(texture.SurfaceTexture);
            manager.OpenCamera(id, new DeviceCallback(this), handler);
        }
        catch (Exception ex) { opened = false; message.Text = "相机不可用：" + ex.Message; }
    }
    private void Configure(CameraDevice device)
    {
        camera = device;
        preview = device.CreateCaptureRequest(CameraTemplate.Preview); preview.AddTarget(surface!);
#pragma warning disable CS0618, CA1422 // Legacy session creation is retained for Android 8/9 camera drivers.
        device.CreateCaptureSession(new List<Surface> { surface!, reader!.Surface! }, new SessionCallback(this), handler);
#pragma warning restore CS0618, CA1422
    }
    private void UpdatePreview()
    {
        if (preview == null || session == null) return;
        try
        {
            preview.Set(CaptureRequest.ControlAfMode!, Java.Lang.Integer.ValueOf((int)ControlAFMode.ContinuousPicture));
            preview.Set(CaptureRequest.FlashMode!, Java.Lang.Integer.ValueOf((int)(flash ? FlashMode.Torch : FlashMode.Off)));
            session.SetRepeatingRequest(preview.Build(), null, handler);
        }
        catch (Exception ex) { RunOnUiThread(() => message.Text = ex.Message); }
    }
    private void Capture()
    {
        if (camera == null || session == null || capturing) return;
        if (AppServices.Client.AccountKey != account) { Ui.Alert(this, "账号已变更", "请返回后重新选择工程。"); return; }
        try
        {
            capturing = true; current = ContextForCapture();
            var request = camera.CreateCaptureRequest(CameraTemplate.StillCapture); request.AddTarget(reader!.Surface!);
            request.Set(CaptureRequest.ControlAfMode!, Java.Lang.Integer.ValueOf((int)ControlAFMode.ContinuousPicture));
            request.Set(CaptureRequest.JpegOrientation!, Java.Lang.Integer.ValueOf(sensorOrientation));
            if (flash) request.Set(CaptureRequest.FlashMode!, Java.Lang.Integer.ValueOf((int)FlashMode.Torch));
            session.Capture(request.Build(), null, handler); message.Text = "正在生成水印照片…";
        }
        catch (Exception ex) { capturing = false; message.Text = ex.Message; }
    }
    private async Task Save(byte[] bytes)
    {
        var capture = current;
        if (capture == null) return;
        string? output = null; var queued = false;
        try
        {
            if (AppServices.Client.AccountKey != account) throw new InvalidOperationException("账号已变更，请使用原账号上传。");
            var metadata = new MobileUploadMetadata("camera", capture.Time, project.Id, Path.GetFileName(project.Path),
                options.Place, options.Note, capture.Watermark, capture.Latitude, capture.Longitude, capture.Location, AppServices.DeviceName, capture.Id);
            output = AppServices.Client.PrepareCameraPhoto("YNS_" + capture.Time.ToLocalTime().ToString("yyyyMMdd_HHmmss") + ".jpg", project, metadata);
            await Task.Run(() => WatermarkPhoto.Save(bytes, capture.Watermark, output));
            // Export before adopting the private file into the durable queue.
            string? galleryError = null;
            if (options.SaveCameraToGallery)
            {
                try { await SaveGallery(output, capture); } catch (Exception ex) { galleryError = ex.Message; }
            }
            await AppServices.Client.CompleteCameraPhoto(capture.Id, CancellationToken.None); queued = true;
            AppServices.Kick();
            RunOnUiThread(() => message.Text = galleryError == null ? "已保存水印照片 · 将上传到 " + Ui.PathLabel(project.Path) : "照片已进入上传队列；保存系统相册失败：" + galleryError);
        }
        catch (Exception ex) { RunOnUiThread(() => Ui.Alert(this, "拍照保存未完成", ex.Message)); }
        finally { capturing = false; current = null; if (queued && output != null && File.Exists(output)) File.Delete(output); }
    }
    private async Task SaveGallery(string file, CaptureContext capture)
    {
        var values = new ContentValues(); values.Put(MediaStore.IMediaColumns.DisplayName, "YNS_" + capture.Id.ToString("N") + ".jpg");
        values.Put(MediaStore.IMediaColumns.MimeType, "image/jpeg");
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        { values.Put(MediaStore.IMediaColumns.RelativePath, "Pictures/Yunsevo/"); values.Put(MediaStore.IMediaColumns.IsPending, 1); }
        else
        {
            var directory = Path.Combine(global::Android.OS.Environment.GetExternalStoragePublicDirectory(global::Android.OS.Environment.DirectoryPictures)!.AbsolutePath, "Yunsevo");
            Directory.CreateDirectory(directory);
            values.Put(MediaStore.IMediaColumns.Data, Path.Combine(directory, "YNS_" + capture.Id.ToString("N") + ".jpg"));
        }
        var uri = ContentResolver!.Insert(MediaStore.Images.Media.ExternalContentUri!, values) ?? throw new IOException("无法创建相册照片。");
        try
        {
            await using var input = File.OpenRead(file);
            await using (var output = ContentResolver.OpenOutputStream(uri) ?? throw new IOException("相册写入被拒绝。")) await input.CopyToAsync(output);
            if (OperatingSystem.IsAndroidVersionAtLeast(29))
            { var ready = new ContentValues(); ready.Put(MediaStore.IMediaColumns.IsPending, 0); ContentResolver.Update(uri, ready, null, null); }
        }
        catch { ContentResolver.Delete(uri, null, null); throw; }
    }
    private void CloseCamera()
    {
        opened = false; session?.Close(); session?.Dispose(); session = null; camera?.Close(); camera?.Dispose(); camera = null;
        preview?.Dispose(); preview = null; reader?.Close(); reader?.Dispose(); reader = null; surface?.Dispose(); surface = null;
        thread?.QuitSafely(); thread?.Dispose(); thread = null; handler?.Dispose(); handler = null;
    }
    private sealed class DeviceCallback(CameraActivity owner) : CameraDevice.StateCallback
    {
        public override void OnOpened(CameraDevice device) => owner.Configure(device);
        public override void OnDisconnected(CameraDevice device) { device.Close(); owner.opened = false; }
        public override void OnError(CameraDevice device, CameraError error)
        { device.Close(); owner.opened = false; owner.RunOnUiThread(() => owner.message.Text = "相机连接失败：" + error); }
    }
    private sealed class SessionCallback(CameraActivity owner) : CameraCaptureSession.StateCallback
    {
        public override void OnConfigured(CameraCaptureSession session) { owner.session = session; owner.UpdatePreview(); }
        public override void OnConfigureFailed(CameraCaptureSession session) => owner.RunOnUiThread(() => owner.message.Text = "设备不支持当前相机配置。");
    }
    private sealed class ImageListener(CameraActivity owner) : Java.Lang.Object, ImageReader.IOnImageAvailableListener
    {
        public void OnImageAvailable(ImageReader? reader)
        {
            using var image = reader?.AcquireNextImage(); if (image == null) return;
            var buffer = image.GetPlanes()![0].Buffer!; var bytes = new byte[buffer.Remaining()]; buffer.Get(bytes);
            _ = owner.Save(bytes);
        }
    }
    private sealed class LocationUpdates : Java.Lang.Object, ILocationListener
    {
        public void OnLocationChanged(Location location) { }
        public void OnProviderDisabled(string provider) { }
        public void OnProviderEnabled(string provider) { }
        public void OnStatusChanged(string? provider, Availability status, Bundle? extras) { }
    }
}

internal static class WatermarkPhoto
{
    public static void Save(byte[] jpeg, string watermark, string path)
    {
        var raw = path + ".raw"; File.WriteAllBytes(raw, jpeg);
        try
        {
            using var bounds = new BitmapFactory.Options { InJustDecodeBounds = true }; BitmapFactory.DecodeFile(raw, bounds);
            if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0) throw new IOException("相机没有返回有效图像。");
            var sample = 1; while (Math.Max(bounds.OutWidth, bounds.OutHeight) / sample > 4096) sample *= 2;
            using var decoded = BitmapFactory.DecodeFile(raw, new BitmapFactory.Options { InSampleSize = sample }) ?? throw new IOException("照片解码失败。");
            using var exif = new ExifInterface(raw); var orientation = exif.GetAttributeInt(ExifInterface.TagOrientation, 1);
            using var matrix = new Matrix();
            if (orientation is 3 or 4) matrix.PostRotate(180);
            else if (orientation is 5 or 6) matrix.PostRotate(90);
            else if (orientation is 7 or 8) matrix.PostRotate(270);
            if (orientation is 2 or 4 or 5 or 7) matrix.PostScale(-1, 1);
            using var oriented = Bitmap.CreateBitmap(decoded, 0, 0, decoded.Width, decoded.Height, matrix, true)!;
            using var bitmap = oriented.Copy(Bitmap.Config.Argb8888!, true)!;
            using var canvas = new Canvas(bitmap);
            using var paint = new Paint(PaintFlags.AntiAlias) { Color = Color.White, TextSize = Math.Max(20, bitmap.Width * .026f) };
            var margin = bitmap.Width * .035f;
            List<string> Wrap()
            {
                var wrapped = new List<string>();
                foreach (var paragraph in watermark.Split('\n'))
                {
                    var remaining = paragraph;
                    while (remaining.Length > 0)
                    {
                        var take = Math.Max(1, paint.BreakText(remaining, true, bitmap.Width - margin * 2.6f, null));
                        if (take < remaining.Length && char.IsHighSurrogate(remaining[take - 1])) take = take > 1 ? take - 1 : Math.Min(2, remaining.Length);
                        wrapped.Add(remaining[..take]); remaining = remaining[take..];
                    }
                }
                return wrapped;
            }
            var lines = Wrap();
            while (lines.Count * paint.TextSize * 1.5f + margin * 2 > bitmap.Height * .55f && paint.TextSize > 12)
            { paint.TextSize *= .9f; lines = Wrap(); }
            var spacing = paint.TextSize * 1.5f;
            var top = bitmap.Height - margin * 2 - spacing * lines.Count;
            using var panel = new Paint { Color = Color.Argb(178, 10, 32, 42) };
            canvas.DrawRect(0, Math.Max(0, top), bitmap.Width, bitmap.Height, panel);
            using var accent = new Paint { Color = Ui.Teal }; canvas.DrawRect(margin, top + margin, margin + Math.Max(5, bitmap.Width * .006f), bitmap.Height - margin, accent);
            var y = top + margin + paint.TextSize;
            foreach (var line in lines) { canvas.DrawText(line, margin * 1.6f, y, paint); y += spacing; }
            var partial = path + ".partial";
            using (var output = File.Create(partial))
            {
                if (!bitmap.Compress(Bitmap.CompressFormat.Jpeg!, 92, output)) throw new IOException("水印照片编码失败。");
                output.Flush(true);
            }
            File.Move(partial, path, true);
        }
        finally { File.Delete(raw); }
    }
}

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

[Activity(Label = "工程水印相机", Theme = "@style/Yunsevo.Camera", Exported = false, ScreenOrientation = ScreenOrientation.FullSensor,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
public sealed partial class CameraActivity : Activity, TextureView.ISurfaceTextureListener
{
    private TextureView texture = null!;
    private TextView message = null!;
    private CameraDevice? camera;
    private CameraCaptureSession? session;
    private CaptureRequest.Builder? preview;
    private ImageReader? reader;
    private HandlerThread? thread;
    private Handler? handler;
    private Surface? surface;
    private bool opened, capturing, flash, front;
    private Guid? scanId;
    private bool scanVideo;
    private CameraGlPipe? gl;
    private Task cameraCleanup = Task.CompletedTask;
    private Rect activeArray = new(0, 0, 1, 1);
    private float zoom = 1, maxZoom = 1;
    private int afRegions, aeRegions, cameraWidth = 1280, cameraHeight = 720, generation;
    private bool flashAvailable, paused;
    private int sensorOrientation = 90;
    private double exposureStep;
    private double sensorDiagonal;
    private TaskCompletionSource<Dictionary<string, string>>? photoProperties;
    private MobileOptions options = null!;
    private FolderSelection project = null!;
    private string account = "", photographer = "";
    private CaptureContext? current;
    private LocationUpdates? locationUpdates;
    private sealed record CaptureContext(DateTimeOffset Time, Guid Id, MobileOptions Options, string Watermark, double? Latitude, double? Longitude, string? Location, CaptureEnvironment? Environment = null)
    { public FolderSelection Project => Options.Project ?? throw new InvalidOperationException("请先选择工程目录。"); }
    protected override void OnCreate(Bundle? state)
    {
        base.OnCreate(state);
        var client = AppServices.Client;
        if (Guid.TryParse(Intent?.GetStringExtra("scan"), out var scan)) scanId = scan;
        if (client.Account == null || !scanId.HasValue && client.Options.Project == null) { Finish(); return; }
        scanVideo = scanId.HasValue && Intent?.GetBooleanExtra("scanVideo", false) == true;
        options = client.Options; if (scanId.HasValue) { var scanning = ScanStore.Get(scanId.Value); ScanStore.EnsureCollecting(scanId.Value); options = options with { Project = scanning.Folder, IncludeLocation = false, IncludeAddress = false, IncludeWeather = false, RecordAudio = false, SaveCameraToGallery = false }; }
        project = options.Project!; account = client.AccountKey; photographer = client.Account.Login.User.DisplayName;
        BuildCameraUi();
        UpdateOverlay();
    }
    protected override void OnResume()
    {
        base.OnResume(); paused = false; if (environmentValue == null) environmentAttempt = DateTimeOffset.MinValue;
        if (texture?.IsAvailable == true && !videoStopping) OpenCamera();
        if (NeedsLocation && CheckSelfPermission(global::Android.Manifest.Permission.AccessCoarseLocation) == Permission.Granted)
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
        paused = true; environmentRequest?.Cancel();
        if (recording || videoStarting || videoStopping) _ = StopVideo(); else CloseCamera();
        base.OnPause();
    }
    public void OnSurfaceTextureAvailable(SurfaceTexture surfaceTexture, int width, int height) { if (texture.SurfaceTexture?.Handle == surfaceTexture.Handle) OpenCamera(); }
    public bool OnSurfaceTextureDestroyed(SurfaceTexture surfaceTexture) { if (texture.SurfaceTexture?.Handle == surfaceTexture.Handle && !videoStopping) CloseCamera(); return true; }
    public void OnSurfaceTextureSizeChanged(SurfaceTexture surfaceTexture, int width, int height) => gl?.Resize(width, height, RotationDegrees, front);
    public void OnSurfaceTextureUpdated(SurfaceTexture surfaceTexture) { }
    private CaptureContext ContextForCapture()
    {
        double? latitude = null, longitude = null; string? location = null;
        if (NeedsLocation && CheckSelfPermission(global::Android.Manifest.Permission.AccessCoarseLocation) == Permission.Granted)
        {
            try
            {
                var manager = (LocationManager)GetSystemService(LocationService)!;
                var sample = manager.GetProviders(true)!.Select(x => manager.GetLastKnownLocation(x)).Where(x => x != null)
                    .OrderByDescending(x => x!.Time).FirstOrDefault();
                if (sample != null && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - sample.Time is >= 0 and < 5 * 60 * 1000)
                {
                    latitude = sample.Latitude; longitude = sample.Longitude;
                    location = latitude.Value.ToString("F6", System.Globalization.CultureInfo.InvariantCulture) + ", " + longitude.Value.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            catch (Java.Lang.SecurityException) { }
        }
        var time = DateTimeOffset.UtcNow;
        if (options.IncludeLocation && location == null) location = "定位暂不可用";
        return EnrichContext(time, latitude, longitude, location);
    }
    private void UpdateOverlay()
    {
        if (IsFinishing || IsDestroyed) return;
        if (shutterView != null) shutterView.Recording = recording || videoStarting;
        gl?.Watermark(scanId.HasValue ? "" : ContextForCapture().Watermark, options);
        if (scanId.HasValue) { var guidance = ScanGuidance(); if (message.Text != guidance) message.Text = guidance; }
        if (recording && scanVideo && SystemClock.ElapsedRealtime() - videoBegan >= 90_000) _ = StopVideo();
        if (recording) message.Text = (scanVideo ? "● 绕物体慢走 · " : "● 录像中 ") + TimeSpan.FromMilliseconds(SystemClock.ElapsedRealtime() - videoBegan).ToString(@"mm\:ss") + (scanVideo ? " · 再点结束" : " · 松开结束");
        message.PostDelayed(UpdateOverlay, 1000);
    }
    private async void OpenCamera()
    {
        if (opened || !texture.IsAvailable || CheckSelfPermission(global::Android.Manifest.Permission.Camera) != Permission.Granted) return;
        try
        {
            opened = true; var opening = ++generation;
            await cameraCleanup;
            if (generation != opening || paused) return;
            thread = new("YunsevoCamera"); thread.Start(); handler = new(thread.Looper!);
            var manager = (CameraManager)GetSystemService(CameraService)!;
            var id = manager.GetCameraIdList()!.FirstOrDefault(id =>
                ((Java.Lang.Integer?)manager.GetCameraCharacteristics(id).Get(CameraCharacteristics.LensFacing))?.IntValue() == (int)(front ? LensFacing.Front : LensFacing.Back))
                ?? manager.GetCameraIdList()!.First();
            var characteristics = manager.GetCameraCharacteristics(id);
            sensorOrientation = ((Java.Lang.Integer?)characteristics.Get(CameraCharacteristics.SensorOrientation))?.IntValue() ?? 90;
            if (characteristics.Get(CameraCharacteristics.SensorInfoPhysicalSize) is global::Android.Util.SizeF physical) sensorDiagonal = Math.Sqrt(physical.Width * physical.Width + physical.Height * physical.Height);
            activeArray = (Rect?)characteristics.Get(CameraCharacteristics.SensorInfoActiveArraySize) ?? new Rect(0, 0, 1, 1);
            selectedMetering = null;
            maxZoom = Math.Clamp(((Java.Lang.Float?)characteristics.Get(CameraCharacteristics.ScalerAvailableMaxDigitalZoom))?.FloatValue() ?? 1, 1, 20);
            afRegions = ((Java.Lang.Integer?)characteristics.Get(CameraCharacteristics.ControlMaxRegionsAf))?.IntValue() ?? 0;
            aeRegions = ((Java.Lang.Integer?)characteristics.Get(CameraCharacteristics.ControlMaxRegionsAe))?.IntValue() ?? 0;
            flashAvailable = ((Java.Lang.Boolean?)characteristics.Get(CameraCharacteristics.FlashInfoAvailable))?.BooleanValue() == true;
            exposureStep = ((global::Android.Util.Rational?)characteristics.Get(CameraCharacteristics.ControlAeCompensationStep))?.DoubleValue() ?? 1;
            zoom = Math.Clamp(zoom, 1, maxZoom); if (!flashAvailable) flash = false; UpdateZoomLabel();
            if (flashIcon != null) flashIcon.Active = flash;
            var map = (StreamConfigurationMap?)characteristics.Get(CameraCharacteristics.ScalerStreamConfigurationMap);
            var sizes = map?.GetOutputSizes((int)ImageFormatType.Jpeg);
            var size = sizes?.Where(x => Math.Max(x.Width, x.Height) <= 4096).OrderByDescending(x => (long)x.Width * x.Height).FirstOrDefault() ?? new global::Android.Util.Size(1920, 1080);
            reader = ImageReader.NewInstance(size.Width, size.Height, ImageFormatType.Jpeg, 2);
            reader.SetOnImageAvailableListener(new ImageListener(this), handler);
            var previewSizes = map?.GetOutputSizes(Java.Lang.Class.FromType(typeof(SurfaceTexture))!);
            var previewSize = previewSizes?.Where(x => Math.Max(x.Width, x.Height) <= 1920).OrderBy(x => Math.Abs(x.Width / (float)x.Height - 16f / 9)).ThenBy(x => Math.Abs(x.Width - (scanVideo ? 1920 : 1280))).FirstOrDefault() ?? new global::Android.Util.Size(1280, 720);
            cameraWidth = previewSize.Width; cameraHeight = previewSize.Height;
            var pipe = new CameraGlPipe(error => RunOnUiThread(() => message.Text = error)); gl = pipe;
            surface = await pipe.Initialize(texture.SurfaceTexture!, texture.Width, texture.Height, cameraWidth, cameraHeight, RotationDegrees, front);
            var landscape = Resources!.Configuration!.Orientation == global::Android.Content.Res.Orientation.Landscape;
            pipe.PreviewInset(Ui.Dp(this, landscape ? 12 : 200), landscape ? Ui.Dp(this, 150) : 0);
            if (generation != opening || paused) { pipe.Dispose(); return; }
            gl.Watermark(scanId.HasValue ? "" : ContextForCapture().Watermark, options);
            manager.OpenCamera(id, new DeviceCallback(this, opening), handler);
        }
        catch (Exception ex) { CloseCamera(); message.Text = "相机不可用：" + ex.Message; }
    }
    private void Configure(CameraDevice device)
    {
        camera = device;
        preview = device.CreateCaptureRequest(CameraTemplate.Preview); preview.AddTarget(surface!);
#pragma warning disable CS0618, CA1422 // Legacy session creation is retained for Android 8/9 camera drivers.
        device.CreateCaptureSession(new List<Surface> { surface!, reader!.Surface! }, new SessionCallback(this, generation), handler);
#pragma warning restore CS0618, CA1422
    }
    private void UpdatePreview()
    {
        handler?.Post(() =>
        {
            if (preview == null || session == null) return;
            try { ApplyControls(preview); session.SetRepeatingRequest(preview.Build(), null, handler); }
            catch (Exception ex) { RunOnUiThread(() => message.Text = ex.Message); }
        });
    }
    private void Capture()
    {
        if (camera == null || session == null || capturing || recording || videoStarting || videoStopping) return;
        if (AppServices.Client.AccountKey != account) { Ui.Alert(this, "账号已变更", "请返回后重新选择工程。"); return; }
        try
        {
            capturing = true; current = ContextForCapture();
            photoProperties = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var request = camera.CreateCaptureRequest(CameraTemplate.StillCapture); request.AddTarget(reader!.Surface!);
            ApplyControls(request);
            request.Set(CaptureRequest.JpegOrientation!, Java.Lang.Integer.ValueOf(RotationDegrees));
            session.Capture(request.Build(), new PhotoResult(this, photoProperties, zoom, exposureStep), handler); message.Text = "正在生成水印照片…";
        }
        catch (Exception ex) { capturing = false; message.Text = ex.Message; }
    }
    private async Task Save(byte[] bytes)
    {
        var capture = current;
        if (capture == null) return;
        if (scanId.HasValue)
        {
            try
            {
                ScanStore.EnsureCollecting(scanId.Value); var path = Path.Combine(ScanStore.Photos(scanId.Value), "capture-" + capture.Time.ToUnixTimeMilliseconds() + "-" + capture.Id.ToString("N") + ".jpg");
                await File.WriteAllBytesAsync(path + ".partial", bytes);
                Dictionary<string, string>? parameters = null;
                if (photoProperties != null) try { parameters = await photoProperties.Task.WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
                using (var exif = new ExifInterface(path + ".partial"))
                {
                    if (parameters != null) foreach (var pair in parameters) exif.SetAttribute(pair.Key, pair.Value);
                    if (sensorDiagonal > 0 && parameters?.GetValueOrDefault("FocalLength") is { } focal && double.TryParse(focal, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var millimeters))
                        exif.SetAttribute("FocalLengthIn35mmFilm", Math.Round(millimeters * 43.2666153 / sensorDiagonal).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    exif.SetAttribute("Make", Build.Manufacturer); exif.SetAttribute("Model", Build.Model); exif.SetAttribute("Artist", photographer); exif.SaveAttributes();
                }
                File.Move(path + ".partial", path);
                RunOnUiThread(() => message.Text = ScanGuidance());
            }
            catch (Exception ex) { RunOnUiThread(() => Ui.Alert(this, "原片保存未完成", ex.Message)); }
            finally { capturing = false; current = null; }
            return;
        }
        string? output = null; var queued = false;
        try
        {
            if (AppServices.Client.AccountKey != account) throw new InvalidOperationException("账号已变更，请使用原账号上传。");
            var metadata = Metadata(capture);
            output = AppServices.Client.PrepareCameraPhoto("YNS_" + capture.Time.ToLocalTime().ToString("yyyyMMdd_HHmmss") + ".jpg", capture.Project, metadata);
            Dictionary<string, string>? parameters = null;
            if (photoProperties != null)
                try { parameters = await photoProperties.Task.WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
            await Task.Run(() => WatermarkPhoto.Save(bytes, capture.Watermark, output, capture.Options, capture.Time, photographer, capture.Id, parameters, capture.Latitude, capture.Longitude,
                string.Join("\n", new[] { capture.Environment?.AddressAttribution, capture.Environment?.Weather?.Attribution }.Where(x => x != null))));
            // Export before adopting the private file into the durable queue.
            string? galleryError = null;
            if (capture.Options.SaveCameraToGallery)
            {
                try { await SaveGallery(output, capture); } catch (Exception ex) { galleryError = ex.Message; }
            }
            await AppServices.Client.CompleteCameraPhoto(capture.Id, CancellationToken.None); queued = true;
            AppServices.Kick();
            RunOnUiThread(() => message.Text = galleryError != null ? "照片已进入上传队列；保存系统相册失败：" + galleryError :
                AppServices.Client.Options.WifiOnly && AppServices.WifiUnavailable ? "已保存 · 等待 Wi-Fi，可在拍摄设置调整" : "已保存水印照片 · 将上传到 " + Ui.PathLabel(capture.Project.Path));
        }
        catch (Exception ex) { RunOnUiThread(() => { if (!IsFinishing && !IsDestroyed) Ui.Alert(this, "拍照保存未完成", ex.Message); }); }
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
    private string ScanGuidance()
    {
        var count = scanId.HasValue ? ScanStore.Files(scanId.Value).Length : 0;
        if (scanVideo) return "点击开始／结束 · 移动绕拍，别原地转手机 · 最长 90 秒";
        return count + " 张 · " + (count < 24 ? "平视绕拍一圈" : count < 48 ? "略高角度补拍一圈" : "略低角度补拍") + " · 建议步骤，不代表已验证覆盖";
    }
    private void CloseCamera()
    {
        opened = false; ++generation; session?.Close(); session?.Dispose(); session = null; camera?.Close(); camera?.Dispose(); camera = null;
        preview?.Dispose(); preview = null; reader?.Close(); reader?.Dispose(); reader = null; surface?.Dispose(); surface = null;
        thread?.QuitSafely(); thread?.Dispose(); thread = null; handler?.Dispose(); handler = null;
        if (gl != null) { var closing = gl; closing.Dispose(); cameraCleanup = Task.WhenAll(cameraCleanup, closing.Completion); gl = null; }
    }
    private sealed class DeviceCallback(CameraActivity owner, int generation) : CameraDevice.StateCallback
    {
        public override void OnOpened(CameraDevice device) { if (owner.generation == generation && !owner.paused) owner.Configure(device); else device.Close(); }
        public override void OnDisconnected(CameraDevice device) { device.Close(); if (owner.generation == generation) owner.opened = false; }
        public override void OnError(CameraDevice device, CameraError error)
        { device.Close(); if (owner.generation != generation) return; owner.opened = false; owner.RunOnUiThread(() => owner.message.Text = "相机连接失败：" + error); }
    }
    private sealed class SessionCallback(CameraActivity owner, int generation) : CameraCaptureSession.StateCallback
    {
        public override void OnConfigured(CameraCaptureSession session)
        { if (owner.generation != generation || owner.paused) { session.Close(); return; } owner.session = session; owner.UpdatePreview(); }
        public override void OnConfigureFailed(CameraCaptureSession session)
        { session.Close(); if (owner.generation == generation) owner.RunOnUiThread(() => owner.message.Text = "设备不支持当前相机配置。"); }
    }
    private sealed class ImageListener(CameraActivity owner) : Java.Lang.Object, ImageReader.IOnImageAvailableListener
    {
        public void OnImageAvailable(ImageReader? reader)
        {
            try
            {
                byte[] bytes;
                using (var image = reader?.AcquireNextImage())
                {
                    if (image == null) return;
                    try
                    {
                        using var buffer = image.GetPlanes()![0].Buffer!;
                        bytes = new byte[buffer.Remaining()]; buffer.Get(bytes);
                    }
                    finally { image.Close(); } // Java peer disposal alone does not release the ImageReader slot.
                }
                _ = owner.Save(bytes);
            }
            catch (Exception ex)
            {
                owner.RunOnUiThread(() =>
                {
                    owner.capturing = false;
                    if (!owner.paused && !owner.IsFinishing) owner.message.Text = "相机取帧未完成，请重试：" + ex.Message;
                });
            }
        }
    }
    private sealed class PhotoResult(CameraActivity owner, TaskCompletionSource<Dictionary<string, string>> completion, float zoom, double step) : CameraCaptureSession.CaptureCallback
    {
        public override void OnCaptureCompleted(CameraCaptureSession session, CaptureRequest request, TotalCaptureResult result)
        { try { completion.TrySetResult(CameraExif.FromResult(result, zoom, step)); } catch (Exception) { completion.TrySetResult([]); } }
        public override void OnCaptureFailed(CameraCaptureSession session, CaptureRequest request, CaptureFailure failure)
        { completion.TrySetResult([]); owner.RunOnUiThread(() => { owner.capturing = false; owner.message.Text = "相机未能完成本次拍摄，请重试。"; }); }
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
    public static void Save(byte[] jpeg, string watermark, string path, MobileOptions? options = null, DateTimeOffset? captured = null,
        string photographer = "", Guid? captureId = null, IReadOnlyDictionary<string, string>? camera = null, double? latitude = null, double? longitude = null, string? attribution = null)
    {
        var raw = path + ".raw"; File.WriteAllBytes(raw, jpeg); var completed = false;
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
            WatermarkPainter.Draw(canvas, bitmap.Width, bitmap.Height, watermark, options ?? new());
            var partial = path + ".partial";
            using (var output = File.Create(partial))
            {
                if (!bitmap.Compress(Bitmap.CompressFormat.Jpeg!, 92, output)) throw new IOException("水印照片编码失败。");
                output.Flush(true);
            }
            CameraExif.Write(exif, partial, bitmap.Width, bitmap.Height, captured ?? DateTimeOffset.UtcNow, photographer, watermark,
                captureId ?? Guid.NewGuid(), camera, latitude, longitude, attribution);
            File.Move(partial, path, true);
            completed = true;
        }
        finally { if (completed) File.Delete(raw); }
    }
}

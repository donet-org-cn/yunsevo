using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;

namespace MyPan.Mobile.Android;

public sealed partial class CameraActivity
{
    private TextView zoomLabel = null!;
    private FocusIndicator focus = null!;
    private ScaleGestureDetector? scaleDetector;
    private GestureDetector? cameraGestures;
    private Handler? touchHandler;
    private Java.Lang.Runnable? hold;
    private bool holding, longPressed;
    private MeteringRectangle? selectedMetering;
    private int RotationDegrees => (sensorOrientation + (front ? DisplayDegrees : -DisplayDegrees) + 360) % 360;
    private int DisplayDegrees
    {
        get
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30)) return (int)(Display?.Rotation ?? SurfaceOrientation.Rotation0) * 90;
#pragma warning disable CS0618, CA1422 // Display fallback for Android 8-10.
            return (int)(WindowManager?.DefaultDisplay?.Rotation ?? SurfaceOrientation.Rotation0) * 90;
#pragma warning restore CS0618, CA1422
        }
    }
    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        if (recording || videoStarting || videoStopping) return;
        CloseCamera(); BuildCameraUi(); if (texture.IsAvailable) OpenCamera();
    }
    private TextView projectCaption = null!;
    private CameraShutter shutterView = null!;
    private CameraGlyph flashIcon = null!;
    private void BuildCameraUi()
    {
        var landscape = Resources!.Configuration!.Orientation == global::Android.Content.Res.Orientation.Landscape;
        var root = new FrameLayout(this); root.SetFitsSystemWindows(true); root.SetBackgroundColor(Color.Black);
        var frame = new FrameLayout(this); texture = new(this) { SurfaceTextureListener = this, ContentDescription = "相机取景，点击对焦，双指缩放" };
        frame.AddView(texture, new FrameLayout.LayoutParams(-1, -1)); if (scanId.HasValue) frame.AddView(new ScanGuide(this), new FrameLayout.LayoutParams(-1, -1)); focus = new(this); frame.AddView(focus, new FrameLayout.LayoutParams(-1, -1)); root.AddView(frame, new FrameLayout.LayoutParams(-1, -1));
        scaleDetector?.Dispose(); cameraGestures?.Dispose(); scaleDetector = new(this, new CameraScale(this)); cameraGestures = new(this, new CameraGestures(this));
        texture.Touch += (_, args) => { if (args.Event == null) return; scaleDetector.OnTouchEvent(args.Event); cameraGestures.OnTouchEvent(args.Event); args.Handled = true; };
        CameraGlyph Icon(string kind, string description, System.Action action, int size = 44)
        { var icon = new CameraGlyph(this, kind) { ContentDescription = description }; icon.Click += (_, _) => action(); icon.LayoutParameters = new LinearLayout.LayoutParams(Ui.Dp(this, size), Ui.Dp(this, size)); return icon; }
        var header = new LinearLayout(this) { Orientation = Orientation.Horizontal }; header.SetGravity(GravityFlags.CenterVertical);
        header.Background = new global::Android.Graphics.Drawables.GradientDrawable(global::Android.Graphics.Drawables.GradientDrawable.Orientation.TopBottom, new[] { Color.Argb(210, 0, 0, 0).ToArgb(), Color.Transparent.ToArgb() });
        header.SetPadding(Ui.Dp(this, 12), Ui.Dp(this, 8), Ui.Dp(this, 12), Ui.Dp(this, 8));
        header.AddView(Icon("close", "关闭相机", () => { if (recording || videoStarting) _ = StopVideo(); else Finish(); }));
        projectCaption = Ui.Text(this, scanId.HasValue ? "三维采集 · 完成" : Path.GetFileName(project.Path) + "  ⌄", 15, true); projectCaption.SetTextColor(Color.White); projectCaption.Gravity = GravityFlags.Center; projectCaption.SetSingleLine(true); projectCaption.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
        projectCaption.Background = Ui.Shape(Color.Argb(115, 15, 24, 30), Ui.Dp(this, 20)); projectCaption.ContentDescription = "切换工程"; projectCaption.Click += (_, _) => _ = PickCameraProject();
        header.AddView(projectCaption, new LinearLayout.LayoutParams(0, Ui.Dp(this, 40), 1) { LeftMargin = Ui.Dp(this, 10), RightMargin = Ui.Dp(this, 10) });
        flashIcon = Icon("flash", "补光灯", () => { if (!flashAvailable) { Toast.MakeText(this, "当前镜头不支持补光灯", ToastLength.Short)!.Show(); return; } flash = !flash; flashIcon.Active = flash; UpdatePreview(); }); header.AddView(flashIcon);
        header.AddView(Icon("more", "水印与拍摄设置", CameraSettings)); root.AddView(header, new FrameLayout.LayoutParams(-1, -2, GravityFlags.Top) { RightMargin = landscape ? Ui.Dp(this, 150) : 0 });
        var bottom = Ui.Column(this, 16); bottom.Background = new global::Android.Graphics.Drawables.GradientDrawable(global::Android.Graphics.Drawables.GradientDrawable.Orientation.TopBottom, new[] { Color.Transparent.ToArgb(), Color.Argb(230, 0, 0, 0).ToArgb() });
        if (landscape) bottom.Background = new global::Android.Graphics.Drawables.GradientDrawable(global::Android.Graphics.Drawables.GradientDrawable.Orientation.LeftRight, new[] { Color.Transparent.ToArgb(), Color.Argb(230, 0, 0, 0).ToArgb() });
        zoomLabel = Ui.Text(this, "1×", 13, true); zoomLabel.SetTextColor(Color.White); zoomLabel.Gravity = GravityFlags.Center; zoomLabel.Background = Ui.Shape(Color.Argb(150, 25, 34, 40), Ui.Dp(this, 18));
        zoomLabel.ContentDescription = "切换焦距"; zoomLabel.Click += (_, _) => SetZoom(zoom > 1.5f ? 1 : Math.Min(2, maxZoom));
        var zoomHolder = new LinearLayout(this); zoomHolder.SetGravity(GravityFlags.Center); zoomHolder.AddView(zoomLabel, new LinearLayout.LayoutParams(Ui.Dp(this, 56), Ui.Dp(this, 32))); bottom.AddView(zoomHolder);
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal }; row.SetGravity(GravityFlags.CenterVertical);
        if (landscape) { row.Orientation = Orientation.Vertical; row.SetGravity(GravityFlags.CenterHorizontal); }
        void Side(View icon) { var cell = new FrameLayout(this); cell.AddView(icon, new FrameLayout.LayoutParams(Ui.Dp(this, 52), Ui.Dp(this, 52), GravityFlags.Center)); row.AddView(cell, landscape ? new LinearLayout.LayoutParams(-1, 0, 1) : new LinearLayout.LayoutParams(0, Ui.Dp(this, 100), 1)); }
        Side(Icon("album", "打开云端相册", () => { if (!scanId.HasValue) StartActivity(new Intent(this, typeof(MainActivity)).AddFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop).PutExtra("tab", "backup")); Finish(); }, 52));
        shutterView = new(this) { ContentDescription = "拍摄，点击拍照，长按录像" }; shutterView.Click += (_, _) => { if (scanVideo) { if (recording || videoStarting) _ = StopVideo(); else _ = StartVideo(); } else Capture(); };
        row.AddView(shutterView, new LinearLayout.LayoutParams(Ui.Dp(this, 96), Ui.Dp(this, 96)));
        Side(Icon("flip", "翻转摄像头", () => { if (capturing || recording || videoStarting || videoStopping) return; if (scanId.HasValue) { Ui.Alert(this, "三维采集", "为保持相机标定，采集中固定使用后置主摄。"); return; } front = !front; zoom = 1; CloseCamera(); OpenCamera(); }, 52)); bottom.AddView(row, landscape ? new LinearLayout.LayoutParams(-1, 0, 1) : new LinearLayout.LayoutParams(-1, -2));
        touchHandler ??= new(Looper.MainLooper!); hold ??= new Java.Lang.Runnable(() => { if (holding && !capturing && !scanId.HasValue) { longPressed = true; _ = StartVideo(); } });
        float startY = 0, startZoom = 1;
        shutterView.Touch += (_, args) =>
        {
            if (args.Event == null) return;
            if (args.Event.ActionMasked == MotionEventActions.Down) { holding = true; longPressed = false; startY = args.Event.GetY(); startZoom = zoom; shutterView.Pressed = true; shutterView.Animate()!.ScaleX(.94f)!.ScaleY(.94f)!.SetDuration(90)!.Start(); if (!scanVideo) touchHandler.PostDelayed(hold, 450); }
            else if (args.Event.ActionMasked == MotionEventActions.Move && recording) SetZoom(startZoom * MathF.Pow(2, (startY - args.Event.GetY()) / Ui.Dp(this, 160)));
            else if (args.Event.ActionMasked is MotionEventActions.Up or MotionEventActions.Cancel)
            {
                holding = false; touchHandler.RemoveCallbacks(hold); shutterView.Pressed = false; shutterView.Animate()!.ScaleX(1)!.ScaleY(1)!.SetDuration(110)!.Start();
                if (scanVideo) { if (args.Event.ActionMasked == MotionEventActions.Up) shutterView.PerformClick(); }
                else if (recording || videoStarting) _ = StopVideo(); else if (args.Event.ActionMasked == MotionEventActions.Up && !longPressed) shutterView.PerformClick();
            }
            shutterView.Invalidate(); args.Handled = true;
        };
        message = Ui.Text(this, scanId.HasValue ? ScanGuidance() : landscape ? "轻触拍照\n长按录像" : "轻触拍照 · 长按录像", 11); message.SetTextColor(Color.Rgb(204, 214, 221)); message.Gravity = GravityFlags.Center; message.SetMaxLines(landscape ? 2 : 1); message.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End; bottom.AddView(message);
        root.AddView(bottom, landscape ? new FrameLayout.LayoutParams(Ui.Dp(this, 150), -1, GravityFlags.Right) : new FrameLayout.LayoutParams(-1, -2, GravityFlags.Bottom)); SetContentView(root); UpdateZoomLabel();
    }
    private void CameraSettings()
    {
        if (scanId.HasValue) { Ui.Alert(this, "三维采集", "保持同一后置镜头、不缩放、不移动物体，缓慢绕拍。至少 6 张，建议 36–72 张；平视、高低角度分圈补拍。网格仅帮助构图，不检测水平或三维覆盖。录像时带着手机绕静止物体慢走，结束后自动筛选清晰且有变化的帧；原地旋转无法恢复深度。点左上角返回扫描页生成模型。原片不会添加水印。"); return; }
        if (capturing || recording || videoStarting || videoStopping) return;
        var choices = new[] { "水印样式与颜色", "录像声音：" + (options.RecordAudio ? "开启" : "关闭"), "保存方式：" + (options.SaveCameraToGallery ? "云端 + 本机相册" : "仅云端"), "上传网络：" + (options.WifiOnly ? "仅 Wi-Fi" : "允许移动网络") };
        var dialog = new global::Android.App.AlertDialog.Builder(this)!.SetTitle("拍摄设置")!.SetItems(choices, (_, args) =>
        {
            if (args.Which == 0) WatermarkPainter.Settings(this, options, SaveCameraOptions);
            else if (args.Which == 1) SaveCameraOptions(options with { RecordAudio = !options.RecordAudio });
            else if (args.Which == 3) { SaveCameraOptions(options with { WifiOnly = !options.WifiOnly }); AppServices.Kick(); }
            else new global::Android.App.AlertDialog.Builder(this)!.SetTitle("拍摄保存方式")!.SetSingleChoiceItems(new[] { "云端 + 本机相册", "仅云端" }, options.SaveCameraToGallery ? 0 : 1, (sender, choice) =>
            {
                SaveCameraOptions(options with { SaveCameraToGallery = choice.Which == 0 }); ((global::Android.App.AlertDialog)sender!).Dismiss();
                if (options.SaveCameraToGallery && !OperatingSystem.IsAndroidVersionAtLeast(29) && CheckSelfPermission(global::Android.Manifest.Permission.WriteExternalStorage) != Permission.Granted)
                    RequestPermissions(new[] { global::Android.Manifest.Permission.WriteExternalStorage }, 911);
            })!.Show();
        })!.Create()!; dialog.Show(); dialog.Window!.SetGravity(GravityFlags.Bottom); dialog.Window.SetLayout(-1, -2);
    }
    private void SaveCameraOptions(MyPan.Mobile.Core.MobileOptions value)
    { options = value; AppServices.Repository.SetOptions(account, value); gl?.Watermark(ContextForCapture().Watermark, value); }
    private async Task PickCameraProject()
    {
        if (scanId.HasValue) { if (recording || videoStarting) _ = StopVideo(); else Finish(); return; }
        if (capturing || recording || videoStarting || videoStopping) { Toast.MakeText(this, "请先结束当前拍摄", ToastLength.Short)!.Show(); return; }
        try
        {
            var folders = (await AppServices.Client.Folders(CancellationToken.None)).Where(x => (x.Access & 2) != 0).OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList();
            if (AppServices.Client.AccountKey != account) return;
            var visible = folders.ToList(); var body = Ui.Column(this, 14); var search = Ui.Input(this, "搜索工程文件夹"); body.AddView(search);
            var list = new ListView(this); body.AddView(list, new LinearLayout.LayoutParams(-1, Math.Min(Ui.Dp(this, 300), (int)(Resources!.DisplayMetrics!.HeightPixels * .45f))));
            void Refresh() { var text = (search.Text ?? "").Trim(); visible = folders.Where(x => x.Path.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList(); list.Adapter = new ArrayAdapter<string>(this, global::Android.Resource.Layout.SimpleListItem1, visible.Select(x => Ui.PathLabel(x.Path) + " · " + Ui.Category(x.Category)).ToArray()); }
            Refresh(); search.TextChanged += (_, _) => Refresh();
            var dialog = new global::Android.App.AlertDialog.Builder(this)!.SetTitle("切换工程目录")!.SetView(body)!.SetNegativeButton("取消", (_, _) => { })!.Create()!;
            list.ItemClick += (_, args) => { var selected = visible[args.Position]; project = new(selected.Id, selected.Path, selected.Category); SaveCameraOptions(options with { Project = project }); projectCaption.Text = Path.GetFileName(project.Path) + "  ⌄"; dialog.Dismiss(); };
            dialog.Show(); dialog.Window!.SetGravity(GravityFlags.Bottom); dialog.Window.SetLayout(-1, -2);
        }
        catch (Exception ex) { Ui.Alert(this, "无法切换工程", ex.Message); }
    }
    private void SetZoom(float value)
    {
        if (scanId.HasValue) return;
        zoom = Math.Clamp(value, 1, maxZoom); selectedMetering = null; UpdateZoomLabel(); UpdatePreview();
    }
    private void UpdateZoomLabel() { if (zoomLabel != null) zoomLabel.Text = zoom.ToString("0.0") + "×"; }
    private Rect Crop()
    {
        var width = Math.Max(1, (int)(activeArray.Width() / zoom)); var height = Math.Max(1, (int)(activeArray.Height() / zoom));
        var left = activeArray.Left + (activeArray.Width() - width) / 2; var top = activeArray.Top + (activeArray.Height() - height) / 2;
        return new(left, top, left + width, top + height);
    }
    private void ApplyControls(CaptureRequest.Builder request)
    {
        request.Set(CaptureRequest.ControlAfMode!, Java.Lang.Integer.ValueOf((int)(afRegions > 0 ? recording ? ControlAFMode.ContinuousVideo : ControlAFMode.ContinuousPicture : ControlAFMode.Off)));
        request.Set(CaptureRequest.ScalerCropRegion!, Crop());
        request.Set(CaptureRequest.FlashMode!, Java.Lang.Integer.ValueOf((int)(flash && flashAvailable ? FlashMode.Torch : FlashMode.Off)));
        var metering = selectedMetering;
        if (metering == null && scanId.HasValue) { var crop = Crop(); var w = Math.Max(2, crop.Width() / 3); var h = Math.Max(2, crop.Height() / 3); metering = new MeteringRectangle(new Rect(crop.CenterX()-w/2, crop.CenterY()-h/2, crop.CenterX()+w/2, crop.CenterY()+h/2), 900); }
        if (metering != null)
        {
            using var regions = Java.Lang.Object.FromArray(new[] { metering });
            if (aeRegions > 0) request.Set(CaptureRequest.ControlAeRegions!, regions);
            if (afRegions > 0) request.Set(CaptureRequest.ControlAfRegions!, regions);
        }
    }
    private void Focus(float x, float y)
    {
        if (camera == null || session == null || capturing) return;
        focus.ShowAt(x, y); var crop = Crop();
        var u = x / texture.Width - .5f; var v = y / texture.Height - .5f;
        var sourceAspect = RotationDegrees % 180 == 0 ? cameraWidth / (float)cameraHeight : cameraHeight / (float)cameraWidth;
        var viewAspect = texture.Width / (float)texture.Height;
        if (sourceAspect > viewAspect) u *= viewAspect / sourceAspect; else v *= sourceAspect / viewAspect;
        if (front) u = -u;
        (u, v) = RotationDegrees switch { 90 => (v, -u), 180 => (-u, -v), 270 => (-v, u), _ => (u, v) };
        var streamAspect = cameraWidth / (float)cameraHeight;
        var readWidth = Math.Min(crop.Width(), crop.Height() * streamAspect); var readHeight = Math.Min(crop.Height(), crop.Width() / streamAspect);
        var centerX = crop.CenterX() + u * readWidth; var centerY = crop.CenterY() + v * readHeight;
        var w = Math.Max(2, crop.Width() / 9); var h = Math.Max(2, crop.Height() / 9);
        var left = Math.Clamp((int)(centerX - w / 2), crop.Left, Math.Max(crop.Left, crop.Right - w));
        var top = Math.Clamp((int)(centerY - h / 2), crop.Top, Math.Max(crop.Top, crop.Bottom - h));
        handler?.Post(() =>
        {
            if (preview == null || session == null) return;
            try
            {
                selectedMetering = new MeteringRectangle(left, top, w, h, 1000); ApplyControls(preview);
                using var regions = Java.Lang.Object.FromArray(new[] { new MeteringRectangle(left, top, w, h, 1000) });
                if (aeRegions > 0) preview.Set(CaptureRequest.ControlAeRegions!, regions);
                if (afRegions > 0)
                {
                    preview.Set(CaptureRequest.ControlAfMode!, Java.Lang.Integer.ValueOf((int)ControlAFMode.Auto));
                    preview.Set(CaptureRequest.ControlAfRegions!, regions);
                    preview.Set(CaptureRequest.ControlAfTrigger!, Java.Lang.Integer.ValueOf((int)ControlAFTrigger.Start));
                    session.Capture(preview.Build(), null, handler);
                    preview.Set(CaptureRequest.ControlAfTrigger!, Java.Lang.Integer.ValueOf((int)ControlAFTrigger.Idle));
                }
                session.SetRepeatingRequest(preview.Build(), scanVideo ? new PreviewCalibration(this) : null, handler);
                RunOnUiThread(() => message.Text = afRegions > 0 ? "已选择对焦区域" : "已选择测光区域 · 当前镜头为固定焦距");
                handler.PostDelayed(UpdatePreview, 2500);
            }
            catch (Exception ex) { RunOnUiThread(() => message.Text = "无法调整对焦：" + ex.Message); }
        });
    }
    private sealed class CameraScale(CameraActivity owner) : ScaleGestureDetector.SimpleOnScaleGestureListener
    { public override bool OnScale(ScaleGestureDetector detector) { owner.SetZoom(owner.zoom * detector.ScaleFactor); return true; } }
    private sealed class CameraGestures(CameraActivity owner) : GestureDetector.SimpleOnGestureListener
    {
        public override bool OnDown(MotionEvent e) => true;
        public override bool OnSingleTapUp(MotionEvent e) { owner.Focus(e.GetX(), e.GetY()); return true; }
        public override bool OnDoubleTap(MotionEvent e) { owner.SetZoom(owner.zoom > 1.1f ? 1 : Math.Min(2, owner.maxZoom)); return true; }
    }
    private sealed class FocusIndicator(Context context) : View(context)
    {
        private float x, y; private long until;
        public void ShowAt(float px, float py) { x = px; y = py; until = System.Environment.TickCount64 + 1300; Invalidate(); PostDelayed(Invalidate, 1350); }
        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas); if (System.Environment.TickCount64 > until) return;
            using var paint = new Paint(PaintFlags.AntiAlias) { Color = Color.Rgb(255, 213, 79), StrokeWidth = Ui.Dp(Context!, 2) };
            paint.SetStyle(Paint.Style.Stroke); var size = Ui.Dp(Context!, 32);
            canvas.DrawRoundRect(x - size, y - size, x + size, y + size, Ui.Dp(Context!, 6), Ui.Dp(Context!, 6), paint);
        }
    }
}

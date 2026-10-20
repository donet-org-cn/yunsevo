using Android.Content.PM;
using Android.Media;
using Android.OS;
using Android.Views;
using Yunsevo.Contracts;
using Yunsevo.Mobile.Core;

namespace Yunsevo.Mobile.Android;

public sealed partial class CameraActivity
{
    private MediaRecorder? movie;
    private Surface? movieSurface;
    private bool recording, videoStarting, videoStopping;
    private Task? videoPreparation;
    private string? movieOutput;
    private CaptureContext? videoContext;
    private long videoBegan;
    private Task StartVideo()
    {
        if (camera == null || session == null || gl == null || capturing || recording || videoStarting || videoStopping) return Task.CompletedTask;
        if (AppServices.Client.AccountKey != account) { message.Text = "账号已变更，请返回后重新选择工程。"; return Task.CompletedTask; }
        if (options.RecordAudio && CheckSelfPermission(global::Android.Manifest.Permission.RecordAudio) != Permission.Granted)
        { RequestPermissions([global::Android.Manifest.Permission.RecordAudio], 910); return Task.CompletedTask; }
        videoStarting = true; videoPreparation = PrepareVideo(); return videoPreparation;
    }
    public override void OnRequestPermissionsResult(int code, string[] permissions, Permission[] grants)
    {
        base.OnRequestPermissionsResult(code, permissions, grants);
        if (code == 911) { SaveCameraOptions(options with { SaveCameraToGallery = grants.Length > 0 && grants.All(x => x == Permission.Granted) }); return; }
        if (code != 910) return;
        var allowed = grants.Length > 0 && grants.All(x => x == Permission.Granted);
        options = options with { RecordAudio = allowed }; AppServices.Repository.SetOptions(account, options);
        message.Text = allowed ? "麦克风已开启，请再次长按开始录像。" : "麦克风未开启，长按将录制无声视频。";
    }
    private async Task PrepareVideo()
    {
        try
        {
            var available = new StatFs(FilesDir!.AbsolutePath).AvailableBytes - 64L * 1024 * 1024;
            if (available < 64L * 1024 * 1024) throw new IOException("手机剩余空间不足，无法录像。");
            RequestedOrientation = ScreenOrientation.Locked;
            videoContext = ContextForCapture();
            var metadata = Metadata(videoContext);
            movieOutput = AppServices.Client.PrepareCameraPhoto("YNS_" + videoContext.Time.ToLocalTime().ToString("yyyyMMdd_HHmmss") + ".mp4", videoContext.Project, metadata);
            var landscape = texture.Width > texture.Height;
            var longEdge = 1280; var shortEdge = 720;
            var width = landscape ? longEdge : shortEdge; var height = landscape ? shortEdge : longEdge;
            if (OperatingSystem.IsAndroidVersionAtLeast(31)) movie = new(this);
            else
            {
#pragma warning disable CS0618, CA1422 // MediaRecorder(Context) was introduced in Android 12.
                movie = new MediaRecorder();
#pragma warning restore CS0618, CA1422
            }
            var recorder = movie;
            recorder.Info += (_, args) => { if (args.What == MediaRecorderInfo.MaxFilesizeReached || args.What == MediaRecorderInfo.MaxDurationReached) RunOnUiThread(() => _ = StopVideo()); };
            recorder.Error += (_, _) => RunOnUiThread(() => _ = StopVideo());
            await Task.Run(() =>
            {
                if (options.RecordAudio) recorder.SetAudioSource(AudioSource.Mic);
                recorder.SetVideoSource(VideoSource.Surface); recorder.SetOutputFormat(OutputFormat.Mpeg4);
                recorder.SetVideoEncoder(VideoEncoder.H264); recorder.SetVideoSize(width, height); recorder.SetVideoFrameRate(30); recorder.SetVideoEncodingBitRate(6_000_000);
                if (options.RecordAudio) { recorder.SetAudioEncoder(AudioEncoder.Aac); recorder.SetAudioEncodingBitRate(128_000); recorder.SetAudioSamplingRate(44_100); }
                recorder.SetMaxFileSize(Math.Min(available, 20L * 1024 * 1024 * 1024));
                recorder.SetOutputFile(movieOutput + ".partial"); recorder.Prepare(); recorder.Start();
            });
            movieSurface = recorder.Surface ?? throw new IOException("无法获取视频编码输出。");
            await gl!.AttachEncoder(movieSurface, width, height);
            recording = true; videoBegan = SystemClock.ElapsedRealtime(); UpdatePreview();
            message.Text = options.RecordAudio ? "● 录像中 · 松开结束" : "● 无声录像中 · 松开结束";
            Window!.AddFlags(WindowManagerFlags.KeepScreenOn);
        }
        catch (Exception ex)
        {
            if (gl != null) try { await gl.DetachEncoder(); } catch (Exception) { }
            try { movie?.Reset(); } catch (Java.Lang.Exception) { }
            movie?.Release(); movie?.Dispose(); movie = null; movieSurface?.Dispose(); movieSurface = null;
            message.Text = "录像未开始：" + ex.Message; RequestedOrientation = ScreenOrientation.FullSensor;
        }
        finally { videoStarting = false; }
    }
    private async Task StopVideo()
    {
        if (videoStopping) return;
        videoStopping = true;
        try
        {
            if (videoStarting && videoPreparation != null) await videoPreparation;
            if (movie == null || videoContext == null || movieOutput == null) return;
            var elapsed = SystemClock.ElapsedRealtime() - videoBegan;
            recording = false; message.Text = "正在保存水印视频…";
            // Detach on the GL thread before stopping the consumer; the MP4 already contains the watermark.
            if (gl != null) await gl.DetachEncoder();
            var recorder = movie;
            await Task.Run(() => recorder.Stop());
            recorder.Release(); recorder.Dispose(); movie = null; movieSurface?.Dispose(); movieSurface = null;
            var partial = movieOutput + ".partial";
            using (var inspect = new MediaMetadataRetriever())
            {
                await inspect.SetDataSourceAsync(partial);
                if (!long.TryParse(inspect.ExtractMetadata(MetadataKey.Duration), out var duration) || duration < 400)
                    throw new IOException("录像时间过短，请长按拍摄更长的视频。");
                if (duration > elapsed + Math.Max(3000, elapsed / 10)) throw new IOException("视频时间轴异常，录制文件已保留，请重试。");
            }
            File.Move(partial, movieOutput, true);
            string? galleryError = null;
            if (videoContext.Options.SaveCameraToGallery)
            {
                try { await CloudMedia.SaveToGallery(this, movieOutput, Path.GetFileName(movieOutput), "video/mp4", CancellationToken.None); }
                catch (Exception ex) { galleryError = ex.Message; }
            }
            await AppServices.Client.CompleteCameraPhoto(videoContext.Id, CancellationToken.None); AppServices.Kick();
            message.Text = galleryError == null ? "水印视频已保存 · 将上传到 " + Ui.PathLabel(project.Path) : "视频已进入上传队列；系统相册保存失败：" + galleryError;
        }
        catch (Exception ex) { if (!IsFinishing) message.Text = "录像保存未完成：" + ex.Message + "；本机文件已保留。"; }
        finally
        {
            try { movie?.Reset(); } catch (Java.Lang.Exception) { }
            movie?.Release(); movie?.Dispose(); movie = null; movieSurface?.Dispose(); movieSurface = null;
            recording = false; videoStarting = false; videoStopping = false; videoPreparation = null; videoContext = null; movieOutput = null;
            Window?.ClearFlags(WindowManagerFlags.KeepScreenOn); RequestedOrientation = ScreenOrientation.FullSensor;
            if (paused || IsFinishing) CloseCamera(); else UpdatePreview();
        }
    }
    private MobileUploadMetadata Metadata(CaptureContext capture) => new("camera", capture.Time, capture.Project.Id, Path.GetFileName(capture.Project.Path),
        capture.Options.Place, capture.Options.Note, capture.Watermark, capture.Latitude, capture.Longitude, capture.Location, AppServices.DeviceName, capture.Id, capture.Environment);
}

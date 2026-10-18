using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using System.Runtime.InteropServices;
using MyPan.Contracts;

namespace MyPan.Mobile.Android;

[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
public sealed class ReconstructionService : Service
{
    private static int active;
    internal static bool IsActive => Volatile.Read(ref active) != 0;
    private volatile bool cancel;
    private const int NotificationId = 741;
    public override IBinder? OnBind(Intent? intent) => null;
    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == "cancel") { cancel = true; return StartCommandResult.NotSticky; }
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0) return StartCommandResult.NotSticky;
        if (!Guid.TryParse(intent?.GetStringExtra("scan"), out var id)) { active = 0; StopSelf(); return StartCommandResult.NotSticky; }
        cancel = false;
        if (OperatingSystem.IsAndroidVersionAtLeast(26)) ((NotificationManager)GetSystemService(NotificationService)!).CreateNotificationChannel(new("reconstruction", "三维模型生成", NotificationImportance.Low));
        StartForeground(NotificationId, Notification("正在准备三维重建"));
        _ = Task.Run(() =>
        {
            var last = -1; long updated = 0; var started = System.Environment.TickCount64;
            try
            {
                var value = ScanStore.Get(id); if (value.State == "running") throw new IOException("此任务已在运行。");
                ScanStore.Edit(id, x => x with { State = "running", Progress = 0, Message = "正在本机重建" });
                ReconstructionNative.Generate(id, value.Backend, (_, percent, text) =>
                {
                    if (cancel || AppServices.Client.AccountKey != value.Account || System.Environment.TickCount64 - started > 30 * 60 * 1000L) return 1;
                    try
                    {
                    if (percent != last && System.Environment.TickCount64 - updated > 500)
                    {
                        last = percent; updated = System.Environment.TickCount64; var message = ReconstructionMessages.Progress(Marshal.PtrToStringUTF8(text) ?? "正在生成");
                        ScanStore.Edit(id, x => x with { Progress = percent, Message = message });
                        ((NotificationManager)GetSystemService(NotificationService)!).Notify(NotificationId, Notification("三维重建 · " + percent + "%"));
                    }
                    return 0;
                    }
                    catch (Exception) { return 1; }
                });
                ScanStore.Edit(id, x => x with { State = "ready", Progress = 100, Message = "本机模型已生成，可预览或上传云端。" });
            }
            catch (Exception ex) { try { ScanStore.Edit(id, x => x with { State = "failed", Message = ex.Message == "CANCELLED" ? "已停止，原始照片已保留。" : ex.Message }); } catch (Exception) { } }
            finally
            {
                var partial = ScanStore.Model(id) + ".partial"; if (File.Exists(partial)) File.Delete(partial);
                Interlocked.Exchange(ref active, 0);
                if (OperatingSystem.IsAndroidVersionAtLeast(24)) StopForeground(StopForegroundFlags.Remove);
                StopSelf();
            }
        });
        return StartCommandResult.NotSticky;
    }
    private Notification Notification(string text)
    {
        var open = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(ScanActivity)), PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        var stop = PendingIntent.GetService(this, 1, new Intent(this, typeof(ReconstructionService)).SetAction("cancel"), PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        return new Notification.Builder(this, "reconstruction").SetContentTitle("沄序 · 三维扫描")!.SetContentText(text)!.SetSmallIcon(global::Android.Resource.Drawable.IcMenuCamera)!.SetContentIntent(open)!.SetOngoing(true)!.AddAction(new Notification.Action.Builder(null, "停止", stop).Build())!.Build();
    }
    public override void OnDestroy() { cancel = true; base.OnDestroy(); }
}

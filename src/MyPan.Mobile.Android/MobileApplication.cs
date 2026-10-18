using Android.App;
using Android.Content;
using Android.App.Job;
using Android.Net;
using Android.OS;
using Android.Provider;
using Android.Database;
using MyPan.Mobile.Core;

namespace MyPan.Mobile.Android;

[Application]
public sealed class MobileApplication(nint handle, global::Android.Runtime.JniHandleOwnership ownership) : Application(handle, ownership)
{
    public override void OnCreate() { base.OnCreate(); AppServices.Initialize(this); }
}

internal static class AppServices
{
    private static readonly SemaphoreSlim runner = new(1);
    private static MediaObserver? observer;
    private static ConnectivityManager.NetworkCallback? networkChanges;
    private static CancellationTokenSource? activeTransfer;
    private static PowerChanges? powerChanges;
    private static Network? activeNetwork;
    private static UploadNetworkState uploadNetwork = new(false, false, false, false, true);
    public static UploadNetworkState NetworkState => Volatile.Read(ref uploadNetwork);
    public static bool WifiUnavailable => !NetworkState.AllowsWifiOnly;
    public static string NetworkLabel
    {
        get
        {
            var current = NetworkState;
            var label = !current.Connected ? "离线" : current.AllowsWifiOnly ? "Wi-Fi" : current.Cellular ? "移动网络" : "其他网络";
            return "网络：" + label + (current.Vpn ? " · VPN" : "");
        }
    }
    public static Context Context { get; private set; } = null!;
    public static MobileClient Client { get; private set; } = null!;
    public static MobileRepository Repository => Client.Repository;
    public static string DeviceName => (Build.Manufacturer + " " + Build.Model).Trim();
    public static string Status { get; private set; } = "准备就绪";
    public static event Action? Changed;
    public static bool IsRunning { get; private set; }
    public static void Initialize(Context context)
    {
        if (Client != null) return;
        Context = context.ApplicationContext!;
        Client = new(new MobileRepository(Path.Combine(Context.FilesDir!.AbsolutePath, "sync")), new AndroidAccountVault(Context));
        try { Client.LoadRemembered(); } catch (Exception ex) { Status = "读取登录失败：" + ex.Message; }
        Repository.Changed += () => Changed?.Invoke();
        Client.Progress += p => { Status = "上传中 · " + (p.Total == 0 ? 100 : p.Bytes * 100 / p.Total) + "%"; Changed?.Invoke(); };
        var connectivity = (ConnectivityManager)Context.GetSystemService(Context.ConnectivityService)!;
        activeNetwork = connectivity.ActiveNetwork;
        if (activeNetwork != null) {
            using var capabilities = connectivity.GetNetworkCapabilities(activeNetwork);
            if (capabilities != null) uploadNetwork = ReadNetwork(capabilities);
        }
        networkChanges = new NetworkChanges();
        ((ConnectivityManager)Context.GetSystemService(Context.ConnectivityService)!).RegisterDefaultNetworkCallback(networkChanges);
        powerChanges = new PowerChanges();
        if (OperatingSystem.IsAndroidVersionAtLeast(33)) Context.RegisterReceiver(powerChanges, new IntentFilter(Intent.ActionBatteryChanged), ReceiverFlags.NotExported);
        else Context.RegisterReceiver(powerChanges, new IntentFilter(Intent.ActionBatteryChanged));
        Schedule();
    }
    private static UploadNetworkState ReadNetwork(NetworkCapabilities capabilities) => new(true,
        capabilities.HasTransport(TransportType.Wifi), capabilities.HasTransport(TransportType.Cellular),
        capabilities.HasTransport(TransportType.Vpn), !capabilities.HasCapability(NetCapability.NotMetered));
    public static bool Charging
    {
        get
        {
            using var status = Context.RegisterReceiver(null, new IntentFilter(Intent.ActionBatteryChanged));
            return status != null && status.GetIntExtra(BatteryManager.ExtraPlugged, 0) != 0;
        }
    }
    public static void Kick()
    {
        Schedule();
        // MediaStore enumeration and durable queue writes must not inherit the activity's UI context.
        _ = Task.Run(() => Run(CancellationToken.None));
    }
    public static async Task Run(CancellationToken ct)
    {
        if (Client.Account == null || !await runner.WaitAsync(0, ct)) return;
        IsRunning = true; Changed?.Invoke();
        try
        {
            await Client.RecoverCapturedPhotos(ct);
            if (Client.Options.PauseUploads) { Status = "上传已暂停"; return; }
            if (Client.Options.WifiOnly && WifiUnavailable) { Status = "等待 Wi-Fi，文件已保存在上传队列"; return; }
            if (Client.Options.ChargingOnly && !Charging) { Status = "等待连接充电器"; return; }
            await Client.Resume(ct);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct); activeTransfer = budget;
            budget.CancelAfter(TimeSpan.FromMinutes(8));
            var completed = 0;
            do
            {
                var added = Client.Options.AutoBackup && AndroidAlbumSource.HasPermission(Context)
                    ? await Client.ScanAlbum(new AndroidAlbumSource(Context), DeviceName, budget.Token) : 0;
                completed += await Client.Pump(WifiUnavailable, Charging, budget.Token);
                await ScanRemote.Pump(budget.Token);
                if (added == 0 || Client.Repository.Jobs(Client.AccountKey).Any(x => x.State == "failed")) break;
            } while (!budget.IsCancellationRequested);
            var pending = Repository.Jobs(Client.AccountKey).Count(x => x.State is not ("completed" or "cancelled"));
            Status = pending == 0 ? "已为最新" + (completed > 0 ? " · 本次上传 " + completed + " 个文件" : "") : pending + " 个文件等待上传或重试";
        }
        catch (OperationCanceledException) { Status = "本轮已结束，剩余文件将继续重试"; }
        catch (Exception ex) { Status = ex.Message; }
        finally { activeTransfer = null; IsRunning = false; runner.Release(); Changed?.Invoke(); }
    }
    public static void Schedule()
    {
        if (Context == null || Client == null) return;
        var scheduler = (JobScheduler)Context.GetSystemService(Context.JobSchedulerService)!;
        if (Client.Account == null) { scheduler.Cancel(3080); scheduler.Cancel(3081); return; }
        if (observer == null && AndroidAlbumSource.HasPermission(Context))
        {
            observer = new MediaObserver();
            Context.ContentResolver!.RegisterContentObserver(MediaStore.Images.Media.ExternalContentUri!, true, observer);
            Context.ContentResolver.RegisterContentObserver(MediaStore.Video.Media.ExternalContentUri!, true, observer);
        }
        var component = new ComponentName(Context, Java.Lang.Class.FromType(typeof(BackupJob)));
        var options = Client.Options;
        if (options.PauseUploads || options.WifiOnly && WifiUnavailable || options.ChargingOnly && !Charging)
        {
            try { activeTransfer?.Cancel(); } catch (ObjectDisposedException) { }
        }
        // The app checks active Wi-Fi itself, preserving VPN routing; UNMETERED would block Wi-Fi VPNs.
        var periodic = new JobInfo.Builder(3080, component)
            .SetRequiredNetworkType(NetworkType.Any)!
            .SetRequiresCharging(options.ChargingOnly)!.SetPersisted(true)!.SetPeriodic(15 * 60 * 1000)!.Build();
        scheduler.Schedule(periodic!);
        if (!options.PauseUploads)
            scheduler.Schedule(new JobInfo.Builder(3081, component)
                .SetRequiredNetworkType(NetworkType.Any)!
                .SetRequiresCharging(options.ChargingOnly)!.SetMinimumLatency(1500)!.Build()!);
    }
    private sealed class MediaObserver() : ContentObserver(new Handler(Looper.MainLooper!))
    {
        public override void OnChange(bool selfChange) => Schedule();
    }
    private sealed class NetworkChanges : ConnectivityManager.NetworkCallback
    {
        public override void OnCapabilitiesChanged(Network network, NetworkCapabilities capabilities)
        {
            activeNetwork = network;
            var next = ReadNetwork(capabilities);
            var previous = Interlocked.Exchange(ref uploadNetwork, next);
            if (!next.Connected || Client.Options.WifiOnly && !next.AllowsWifiOnly)
                try { activeTransfer?.Cancel(); } catch (ObjectDisposedException) { }
            if (next != previous) { Changed?.Invoke(); Kick(); }
        }
        public override void OnLost(Network network)
        {
            if (activeNetwork?.Equals(network) != true) return;
            activeNetwork = null;
            Volatile.Write(ref uploadNetwork, new(false, false, false, false, true));
            try { activeTransfer?.Cancel(); } catch (ObjectDisposedException) { }
            Changed?.Invoke(); Schedule();
        }
    }
    private sealed class PowerChanges : BroadcastReceiver
    {
        private bool? previous;
        public override void OnReceive(Context? context, Intent? intent)
        {
            var plugged = intent?.GetIntExtra(BatteryManager.ExtraPlugged, 0) > 0;
            if (previous == plugged) return;
            previous = plugged;
            if (Client.Options.ChargingOnly && !plugged)
                try { activeTransfer?.Cancel(); } catch (ObjectDisposedException) { }
            Schedule();
        }
    }
}

[Service(Name = "app.yunsevo.drive.BackupJob", Permission = "android.permission.BIND_JOB_SERVICE", Exported = true)]
public sealed class BackupJob : JobService
{
    private readonly Dictionary<int, CancellationTokenSource> jobs = [];
    public override bool OnStartJob(JobParameters? parameters)
    {
        if (parameters == null) return false;
        var stop = new CancellationTokenSource();
        lock (jobs) jobs[parameters.JobId] = stop;
        _ = Task.Run(async () =>
        {
            try { await AppServices.Run(stop.Token); }
            finally { lock (jobs) jobs.Remove(parameters.JobId); JobFinished(parameters, false); stop.Dispose(); }
        });
        return true;
    }
    public override bool OnStopJob(JobParameters? parameters)
    { lock (jobs) if (parameters != null && jobs.TryGetValue(parameters.JobId, out var stop)) stop.Cancel(); return true; }
}

[BroadcastReceiver(Enabled = true, Exported = true)]
[IntentFilter([Intent.ActionBootCompleted, Intent.ActionMyPackageReplaced])]
public sealed class RestartReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context == null) return;
        AppServices.Initialize(context); AppServices.Schedule();
    }
}

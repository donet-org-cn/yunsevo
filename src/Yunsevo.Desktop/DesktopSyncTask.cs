using Yunsevo.Contracts;
using Yunsevo.Sync;
#if WINDOWS
using Yunsevo.Client;
#endif

namespace Yunsevo.Desktop;

internal sealed class DesktopSyncTask(SyncTaskProfile profile, string home, Action<DesktopSyncTask, string> log, Action changed)
{
    public SyncTaskProfile Profile { get; set; } = profile;
    public string State { get; private set; } = "paused";
    public string Message { get; private set; } = "已暂停";
    public bool IsRunning => running != null;
    public bool HasSynchronized { get; private set; }
    private IDisposable? engine;
    private CloudConnection? connection;
    private CancellationTokenSource? cancellation;
    private Task? running;
    private bool hasIssues;
    private void Status(string state, string message) { State = state; Message = message; changed(); }
    private void Connected(bool online, bool expired) => Status(expired ? "signed-out" : online ? hasIssues ? "error" : "connected" : "offline",
        expired ? "登录已失效" : online ? hasIssues ? "有项目需要处理" : "已连接" : "连接已断开，正在重试");
    private void Log(string message)
    {
        if (message.StartsWith("同步待重试", StringComparison.Ordinal)) Status("offline", "连接中断，正在重试");
        else if (message.StartsWith("项目待", StringComparison.Ordinal) || message.StartsWith("同步部分", StringComparison.Ordinal)) { hasIssues = true; Status("error", "有项目需要处理"); }
        else if (message.StartsWith("同步完成", StringComparison.Ordinal)) { hasIssues = false; HasSynchronized = true; Status("connected", "已保持同步"); }
        log(this, message);
    }
    public async Task Start(LoginResult session, ConfirmDeletion confirm)
    {
        if (running != null) return;
        HasSynchronized = false;
        Status("paused", "正在连接…");
        cancellation = new(); var ct = cancellation.Token;
        try
        {
            var state = SyncProfiles.StatePath(home, Profile);
#if WINDOWS
            if (Profile.OnDemand)
            {
                var config = new ClientConfig(Profile.Server, Secret.Protect(session.AccessToken), Profile.Local,
                    RemoteDirectory: Profile.Remote, AccountId: session.User.Id.ToString("N"));
                var windows = new SyncEngine(config, state, Log, confirm, Profile.RemoteId); engine = windows;
                windows.ConnectionChanged += Connected;
                await ShellIntegration.Register(Profile.Local, Log);
                running = Task.Run(() => windows.Run(ct));
            }
            else
#endif
            {
                connection = new(Profile.Server, session.AccessToken); connection.ConnectionChanged += Connected;
                var portable = new PortableSync(connection, Profile.Server, session.User.Id, Profile.Remote, Profile.Local, state, Log, confirm, Profile.RemoteId);
                engine = portable; running = Task.Run(() => portable.Run(ct));
            }
            Log("同步任务已启动。");
        }
        catch (Exception ex) { await Stop(); Status("error", ex.Message); log(this, ex.Message); }
    }
    public async Task Stop()
    {
        cancellation?.Cancel();
        if (running != null) try { await running; } catch (OperationCanceledException) { } catch (Exception ex) { log(this, ex.Message); }
        engine?.Dispose(); engine = null;
        connection?.Dispose(); connection = null;
        cancellation?.Dispose(); cancellation = null; running = null;
        Status("paused", "已暂停");
    }
}

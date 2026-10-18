using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls.ApplicationLifetimes;
using MyPan.Contracts;

namespace MyPan.Desktop;

internal sealed partial class MainWindow
{
    private async Task RunRestoreProbe()
    {
        var exit = 0; var checks = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; Log("RESTORE PASS: " + message); }
        try
        {
            Check(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MYPAN_PASSWORD")) && string.IsNullOrEmpty(password.Text), "Process restart receives no password");
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(35) && !(accounts.Count == 2 && accounts.All(x => x.Session != null) && tasks.Count == 3 && tasks.Count(x => x.IsRunning && x.HasSynchronized) == 2)) await Task.Delay(150);
            Check(accounts.Count == 2 && accounts.All(x => x.Session != null), "Both saved servers automatically authenticate after a real process restart");
            Check(workspace.IsVisible && !loginPage.IsVisible, "Remembered login opens task management without the login form");
            Check(tasks.Count == 3 && tasks.Count(x => x.IsRunning && x.HasSynchronized) == 2 && tasks.Count(x => !x.Profile.Enabled && !x.IsRunning) == 1, "Restart restores enabled tasks across servers and preserves the paused task");
            var second = tasks.Single(x => x.Profile.Name == "第二服务器");
            Check(await File.ReadAllTextAsync(Path.Combine(second.Profile.Local, "second-server.txt")) == "second-server", "Restored second-server task retains the correct remote bytes");
            Check(MyPan.Sync.SyncProfiles.Load(PreferencesFile).Tasks.Count == 3, "Remembered login preserves all saved task identities");
            await DesktopScreenshots.Save(this, Path.Combine(home, "auto-login.png"));
            AtomicFile.WriteText(Path.Combine(home, "restore-verification.json"), JsonSerializer.Serialize(new ChangeNotice(checks), ApiJson.Default.ChangeNotice));
        }
        catch (Exception ex) { AtomicFile.WriteText(Path.Combine(home, "restore-error.txt"), ex.ToString()); exit = 1; }
        finally
        {
            reconnectTimer.Stop();
            foreach (var task in tasks)
            {
                await task.Stop();
#if WINDOWS
                if (task.Profile.OnDemand && Directory.Exists(task.Profile.Local)) MyPan.Client.ShellIntegration.Unregister(task.Profile.Local);
#endif
            }
            foreach (var item in accounts) item.Connection?.Dispose();
            closing = true; trayHub?.Close(); tray.Dispose(); Close(); (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown(exit);
        }
    }
}

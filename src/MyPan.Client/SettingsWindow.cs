using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using static MyPan.Client.Win32;

namespace MyPan.Client;

internal sealed class SettingsWindow(string configPath, bool background, bool smokeTest)
{
    private static SettingsWindow? current;
    private nint window, server, secret, remoteDirectory, root, onDemand, startup, selected, output;
    private readonly ConcurrentQueue<string> messages = new();
    private readonly StringBuilder history = new();
    private readonly SemaphoreSlim commands = new(1);
    private SyncEngine? engine;
    private CancellationTokenSource? cancellation;
    private Task? loop;
    private ClientConfig config = ClientConfig.Load(configPath);
    private bool trayReady, quitting;
    private int commandInvocations;

    public unsafe int Run()
    {
        current = this;
        var instance = GetModuleHandle(null);
        fixed (char* name = "MyPan.Settings")
        {
            var wc = new WindowClass { Size = (uint)sizeof(WindowClass), ClassName = name, Instance = instance,
                Procedure = &Procedure, Background = 6, Cursor = LoadCursor(0, 32512), Icon = LoadIcon(0, 32512) };
            if (RegisterClass(wc) == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }
        window = CreateWindow(0x00010000, "MyPan.Settings", "MyPan 云盘 · 设置", 0x00CA0000, 180, 120, 790, 764, 0, 0, instance, 0);
        if (window == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        Build();
        if (smokeTest) VerifyInput();
        var tray = Tray();
        trayReady = !smokeTest && NotifyIcon(0, tray);
        if (!background || !trayReady) ShowWindow(window, smokeTest ? 0 : 5);
        if (smokeTest) _ = SetTimer(window, 1, 500, 0);
        else if (background) Command(2);
        while (GetMessage(out var message, 0, 0, 0) > 0)
        {
            if (!IsDialogMessage(window, message)) { TranslateMessage(message); DispatchMessage(message); }
        }
        current = null;
        commands.Dispose();
        return 0;
    }
    private nint Control(string kind, string text, int x, int y, int w, int h, int id = 0, uint extra = 0)
    {
        var child = CreateWindow(kind == "EDIT" ? 0x200u : 0u, kind, text, 0x50000000 | extra,
            x, y, w, h, window, id, GetModuleHandle(null), 0);
        _ = SendMessage(child, 0x30, (nuint)GetStockObject(17), 1);
        return child;
    }
    private void Build()
    {
        Control("STATIC", "MyPan 云盘", 28, 20, 700, 26);
        Control("STATIC", "不同客户端填写同一云端目录即可连接同一份文件；留空同步全部文件。", 28, 54, 710, 24);
        Control("STATIC", "服务器地址", 28, 100, 112, 24);
        server = Control("EDIT", config.ServerUrl, 150, 96, 580, 28, 100, 0x10080);
        Control("STATIC", "API 密钥", 28, 144, 112, 24);
        secret = Control("EDIT", Secret.Unprotect(config.ProtectedApiKey), 150, 140, 580, 28, 101, 0x100A0);
        Control("STATIC", "云端目录", 28, 188, 112, 24);
        remoteDirectory = Control("EDIT", config.RemoteDirectory, 150, 184, 580, 28, 107, 0x10080);
        Control("STATIC", "本地同步目录", 28, 232, 112, 24);
        root = Control("EDIT", config.SyncRoot, 150, 228, 580, 28, 102, 0x10080);
        onDemand = Control("BUTTON", "按需下载（只在打开文件时下载内容）", 150, 272, 580, 26, 103, 0x10003);
        startup = Control("BUTTON", "登录 Windows 后自动启动", 150, 304, 580, 26, 104, 0x10003);
        SendMessage(onDemand, 0xF1, config.FilesOnDemand ? 1u : 0u, 0);
        SendMessage(startup, 0xF1, config.StartAtLogin ? 1u : 0u, 0);
        Control("BUTTON", "保存并启动", 28, 350, 140, 36, 1, 0x10001);
        Control("BUTTON", "暂停同步", 184, 350, 120, 36, 3, 0x10000);
        Control("BUTTON", "立即同步", 320, 350, 120, 36, 4, 0x10000);
        Control("BUTTON", "打开云盘目录", 456, 350, 140, 36, 5, 0x10000);
        Control("STATIC", "文件或文件夹的相对路径（留空表示全部）", 28, 410, 700, 24);
        selected = Control("EDIT", "", 28, 442, 410, 30, 105, 0x10080);
        Control("BUTTON", "保留在此设备", 456, 440, 132, 34, 6, 0x10000);
        Control("BUTTON", "释放空间", 602, 440, 128, 34, 7, 0x10000);
        Control("STATIC", "运行状态 · 关闭窗口后继续在托盘运行；从托盘菜单退出。", 28, 498, 710, 24);
        output = Control("EDIT", "尚未启动。填写服务端地址、密钥及空的本地 NTFS 同步文件夹。", 28, 530, 702, 152, 106, 0x00200844);
        Control("BUTTON", "退出", 610, 692, 120, 28, 9, 0x10000);
    }
    private static unsafe string Read(nint control)
    { char* buffer = stackalloc char[4096]; var length = GetText(control, buffer, 4096); return new(buffer, 0, length); }
    private void Log(string message)
    {
        messages.Enqueue(DateTime.Now.ToString("HH:mm:ss") + "  " + message);
        _ = PostMessage(window, 0x8001, 0, 0);
    }
    private void Drain()
    {
        if (messages.IsEmpty) return;
        while (messages.TryDequeue(out var message)) history.AppendLine(message);
        if (history.Length > 24000) history.Remove(0, history.Length - 16000);
        SetText(output, history.ToString().ReplaceLineEndings("\r\n"));
        SendMessage(output, 0xB1, (nuint)history.Length, history.Length);
        SendMessage(output, 0xB7, 0, 0);
    }
    private void Command(int id)
    {
        commandInvocations++;
        if (id is not (1 or 2 or 3 or 4 or 5 or 6 or 7 or 9)) return;
        if (quitting) return;
        if (id == 5)
        {
            var directory = engine?.Provider.Root ?? Read(root);
            if (Directory.Exists(directory)) Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { directory }, UseShellExecute = true });
            else Log("同步目录尚未创建。");
            return;
        }
        if (id == 9) { Quit(); return; }
        ClientConfig? next = null;
        var path = Read(selected);
        if (id == 1)
        {
            try
            {
                next = new(Read(server).Trim(), Secret.Protect(Read(secret)), Read(root).Trim(),
                    SendMessage(onDemand, 0xF0, 0, 0) == 1, SendMessage(startup, 0xF0, 0, 0) == 1, Read(remoteDirectory).Trim());
                next.Validate();
                SyncEngine.ValidateLocalDirectory(next, next.GetStatePath(configPath));
            }
            catch (Exception ex) { Log(ex.Message); return; }
        }
        _ = Task.Run(async () =>
        {
            await commands.WaitAsync();
            try
            {
                if (id is 1 or 2)
                {
                    await Stop();
                    var candidate = next ?? config;
                    var started = new SyncEngine(candidate, candidate.GetStatePath(configPath), Log);
                    try
                    {
                        await ShellIntegration.Register(candidate.SyncRoot, Log);
                        if (next != null) { ShellIntegration.SetStartup(configPath, candidate.StartAtLogin); candidate.Save(configPath); }
                    }
                    catch { started.Dispose(); throw; }
                    config = candidate;
                    engine = started;
                    Log("已注册资源管理器云盘。请使用详细信息视图，并在表头中启用“状态”或“可用性状态”列。");
                    cancellation = new();
                    loop = engine.Run(cancellation.Token);
                    Log("已连接云端目录：/" + config.RemoteDirectory + "，正在同步文件目录。");
                }
                else if (id == 3) { await Stop(); Log("同步已暂停。云端占位文件需要恢复同步后才能下载。"); }
                else if (engine == null) Log("请先保存并启动同步。");
                else if (id == 4) await engine.SyncOnce();
                else if (id is 6 or 7) await engine.SetOffline(path, id == 6);
            }
            catch (Exception ex) { Log(ex.Message); }
            finally { commands.Release(); }
        });
    }
    private void VerifyInput()
    {
        // Exercise actual EDIT notifications and WM_CHAR input without saving configuration or starting sync.
        const string sampleKey = "input-regression-key-0123456789abcdef";
        SetText(secret, "");
        foreach (var character in sampleKey) SendMessage(secret, 0x102, character, 1); // WM_CHAR
        SetText(server, "http://localhost:5080");
        SetText(root, @"F:\MyPanInputTest");
        SetText(remoteDirectory, "工作资料/共享文件");
        SetText(selected, "资料/发票.txt");
        SendMessage(onDemand, 0xF5, 0, 0); // BM_CLICK: checkbox notifications are not action commands.
        Log("输入回归检查");
        Drain();
        // Even a matching command ID must originate from its own button HWND.
        SendMessage(window, 0x111, 4, secret);
        if (Read(secret) != sampleKey || Read(remoteDirectory) != "工作资料/共享文件" || Read(selected) != "资料/发票.txt" || commandInvocations != 0 || !messages.IsEmpty)
            throw new InvalidOperationException("输入回归检查失败：输入或日志更新触发了按钮命令。");
        SendMessage(GetDlgItem(window, 3), 0xF5, 0, 0); // A real Pause button click must still work.
        if (commandInvocations != 1) throw new InvalidOperationException("输入回归检查失败：按钮点击没有执行一次。");
    }
    private async Task Stop()
    {
        if (cancellation != null) cancellation.Cancel();
        if (loop != null) await loop;
        engine?.Dispose(); engine = null;
        cancellation?.Dispose(); cancellation = null; loop = null;
    }
    private void Quit()
    {
        if (quitting) return;
        quitting = true;
        _ = Task.Run(async () =>
        {
            await commands.WaitAsync();
            try { await Stop(); }
            finally { commands.Release(); _ = PostMessage(window, 0x8003, 0, 0); }
        });
    }
    private unsafe TrayIcon Tray()
    {
        var icon = new TrayIcon { Size = (uint)sizeof(TrayIcon), Window = window, Id = 1, Flags = 7, Callback = 0x8002, Icon = LoadIcon(0, 32512) };
        "MyPan 云盘".AsSpan().CopyTo(new Span<char>(icon.Tip, 128));
        return icon;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint Procedure(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        try { return current?.Handle(hwnd, message, wParam, lParam) ?? DefWindowProc(hwnd, message, wParam, lParam); }
        catch (Exception ex) { current?.Log(ex.Message); return 0; }
    }
    private nint Handle(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        switch (message)
        {
            case 0x111: // WM_COMMAND: EDIT notifications also arrive here, including during SetWindowText.
                var id = (int)(wParam & 0xffff);
                var notification = (wParam >> 16) & 0xffff;
                if (notification == 0 && id is (1 or 3 or 4 or 5 or 6 or 7 or 9) &&
                    lParam != 0 && lParam == GetDlgItem(hwnd, id)) // BN_CLICKED from an action button only.
                    Command(id);
                return 0;
            case 0x10:
                if (trayReady) ShowWindow(window, 0); else Quit();
                return 0;
            case 0x113: if (smokeTest) Quit(); return 0;
            case 0x8001: Drain(); return 0;
            case 0x8002:
                if (lParam == 0x203) { ShowWindow(window, 9); SetForegroundWindow(window); }
                else if (lParam == 0x205)
                {
                    var menu = CreatePopupMenu();
                    AppendMenu(menu, 0, 10, "打开设置"); AppendMenu(menu, 0, 9, "退出 MyPan");
                    GetCursorPos(out var point); SetForegroundWindow(window);
                    var choice = TrackPopupMenu(menu, 0x100, point.X, point.Y, 0, window, 0);
                    DestroyMenu(menu);
                    if (choice == 9) Quit(); else if (choice == 10) { ShowWindow(window, 9); SetForegroundWindow(window); }
                }
                return 0;
            case 0x8003:
                if (trayReady) NotifyIcon(2, Tray());
                DestroyWindow(window); return 0;
            case 2: PostQuitMessage(0); return 0;
        }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }
}

using System.Runtime.InteropServices;

namespace MyPan.Client;

internal static unsafe partial class Win32
{
    [StructLayout(LayoutKind.Sequential)] internal struct WindowClass
    {
        public uint Size, Style;
        public delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint> Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public char* MenuName;
        public char* ClassName;
        public nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Message
    { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct TrayIcon
    {
        public uint Size; public nint Window; public uint Id, Flags, Callback; public nint Icon;
        public fixed char Tip[128]; public uint State, StateMask;
        public fixed char Info[256]; public uint Timeout; public fixed char Title[64];
        public uint InfoFlags; public Guid Guid; public nint Balloon;
    }
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)] internal static partial nint GetModuleHandle(string? module);
    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)] internal static partial ushort RegisterClass(in WindowClass windowClass);
    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint CreateWindow(uint extended, string className, string text, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")] internal static partial nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);
    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")] internal static partial int GetMessage(out Message message, nint window, uint min, uint max);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool TranslateMessage(in Message message);
    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")] internal static partial nint DispatchMessage(in Message message);
    [LibraryImport("user32.dll", EntryPoint = "IsDialogMessageW")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool IsDialogMessage(nint window, in Message message);
    [LibraryImport("user32.dll")] internal static partial void PostQuitMessage(int code);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool DestroyWindow(nint window);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool ShowWindow(nint window, int command);
    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")] internal static partial nint SendMessage(nint window, uint message, nuint wParam, nint lParam);
    [LibraryImport("user32.dll")] internal static partial nint GetDlgItem(nint window, int id);
    [LibraryImport("user32.dll", EntryPoint = "SetWindowTextW", StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool SetText(nint window, string text);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")] internal static partial int GetText(nint window, char* text, int count);
    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)] internal static partial int MessageBox(nint window, string message, string title, uint flags);
    [LibraryImport("user32.dll", EntryPoint = "LoadIconW")] internal static partial nint LoadIcon(nint instance, nint name);
    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW")] internal static partial nint LoadCursor(nint instance, nint name);
    [LibraryImport("gdi32.dll")] internal static partial nint GetStockObject(int index);
    [LibraryImport("user32.dll")] internal static partial nint CreatePopupMenu();
    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool AppendMenu(nint menu, uint flags, nuint id, string text);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool GetCursorPos(out Point point);
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [LibraryImport("user32.dll")] internal static partial int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rectangle);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool SetForegroundWindow(nint window);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool DestroyMenu(nint menu);
    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool NotifyIcon(uint operation, in TrayIcon icon);
    [LibraryImport("user32.dll")] internal static partial nuint SetTimer(nint window, nuint id, uint interval, nint callback);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool FreeConsole();
}

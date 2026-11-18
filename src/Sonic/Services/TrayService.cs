using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Sonic.Core.Abstractions;
using Sonic.Core.Models;

namespace Sonic.Services;

/// <summary>Notification-area icon with a menu, plus system-wide hotkeys.</summary>
public interface ITrayService : IDisposable
{
    /// <summary>Whether the global hotkeys could be registered (another app may own them).</summary>
    bool HotkeysRegistered { get; }

    void Initialize();
    void ApplySettings();
    void SetTooltip(string text);
}

public sealed class TrayService : ITrayService
{
    public const string AudioHotkeyText = "Ctrl + Alt + D";
    public const string VideoHotkeyText = "Ctrl + Alt + Shift + D";

    private const int WM_APP_TRAY = 0x8001;
    private const int WM_HOTKEY = 0x0312;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_NULL = 0x0000;
    private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_SHOWTIP = 0x80;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;
    private const uint TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100, TPM_NONOTIFY = 0x80;
    private const uint MF_STRING = 0x0, MF_SEPARATOR = 0x800;
    private const int HotkeyAudio = 1, HotkeyVideo = 2;
    private const int CmdOpen = 1, CmdAudio = 2, CmdVideo = 3, CmdPause = 4, CmdResume = 5, CmdExit = 9;

    private readonly IActivationService _activation;
    private readonly IWindowService _windows;
    private readonly IDownloadQueueService _queue;
    private readonly ISettingsService _settings;
    private readonly ILogger<TrayService> _logger;
    private readonly WndProc _wndProc; // must stay referenced while the window exists
    private IntPtr _hwnd;
    private IntPtr _icon;
    private uint _taskbarCreated;
    private bool _iconAdded;
    private string _tooltip = "Sonic";

    public TrayService(IActivationService activation, IWindowService windows, IDownloadQueueService queue, ISettingsService settings, ILogger<TrayService> logger)
    {
        _activation = activation;
        _windows = windows;
        _queue = queue;
        _settings = settings;
        _logger = logger;
        _wndProc = WindowProc;
    }

    public bool HotkeysRegistered { get; private set; }

    public void Initialize()
    {
        var hInstance = GetModuleHandle(null);
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = "SonicTrayWindow",
        };
        if (RegisterClassEx(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410 /* already registered */)
        {
            _logger.LogWarning("Tray window class registration failed: {Error}", Marshal.GetLastWin32Error());
            return;
        }

        // A hidden tool window (not message-only) so it can own the tray popup menu.
        _hwnd = CreateWindowEx(0x80 /* WS_EX_TOOLWINDOW */, "SonicTrayWindow", "Sonic", 0x80000000 /* WS_POPUP */, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            _logger.LogWarning("Tray window creation failed: {Error}", Marshal.GetLastWin32Error());
            return;
        }

        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _icon = LoadImage(IntPtr.Zero, Path.Combine(AppContext.BaseDirectory, "Assets", "Sonic.ico"), 1 /* IMAGE_ICON */,
            GetSystemMetrics(49 /* SM_CXSMICON */), GetSystemMetrics(50 /* SM_CYSMICON */), 0x10 /* LR_LOADFROMFILE */);
        AddIcon();
        ApplySettings();
    }

    public void ApplySettings()
    {
        if (_hwnd == IntPtr.Zero) return;
        UnregisterHotKey(_hwnd, HotkeyAudio);
        UnregisterHotKey(_hwnd, HotkeyVideo);
        HotkeysRegistered = false;
        if (!_settings.Current.GlobalHotkey) return;

        var audio = RegisterHotKey(_hwnd, HotkeyAudio, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x44 /* D */);
        var video = RegisterHotKey(_hwnd, HotkeyVideo, MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_NOREPEAT, 0x44);
        HotkeysRegistered = audio && video;
        if (!HotkeysRegistered) _logger.LogWarning("Global hotkeys could not be registered (in use by another app)");
    }

    public void SetTooltip(string text)
    {
        _tooltip = text.Length > 120 ? text[..120] : text;
        if (!_iconAdded) return;
        var data = CreateData(NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private void AddIcon()
    {
        var data = CreateData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        _iconAdded = Shell_NotifyIcon(NIM_ADD, ref data);
        if (!_iconAdded) _logger.LogWarning("Tray icon could not be added");
    }

    private NOTIFYICONDATA CreateData(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = WM_APP_TRAY,
        hIcon = _icon,
        szTip = _tooltip,
    };

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == WM_APP_TRAY)
            {
                var mouse = (int)(lParam.ToInt64() & 0xFFFF);
                if (mouse == WM_LBUTTONUP) _windows.BringToFront();
                else if (mouse == WM_RBUTTONUP) ShowMenu();
                return IntPtr.Zero;
            }

            if (msg == WM_HOTKEY)
            {
                var kind = wParam.ToInt32() == HotkeyVideo ? MediaKind.Video : MediaKind.Audio;
                _ = _activation.DownloadClipboardAsync(kind);
                return IntPtr.Zero;
            }

            if (msg == _taskbarCreated && _taskbarCreated != 0)
            {
                AddIcon(); // Explorer restarted.
                return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tray message handling failed");
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, MF_STRING, CmdOpen, "Open Sonic");
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, CmdAudio, $"Download audio from clipboard\t{AudioHotkeyText}");
            AppendMenu(menu, MF_STRING, CmdVideo, $"Download video from clipboard\t{VideoHotkeyText}");
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, CmdPause, "Pause all downloads");
            AppendMenu(menu, MF_STRING, CmdResume, "Resume all downloads");
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, CmdExit, "Exit");

            GetCursorPos(out var pt);
            SetForegroundWindow(_hwnd); // required so the menu closes when clicking elsewhere
            var cmd = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY, pt.X, pt.Y, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            _ = RunCommandAsync(cmd);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private async Task RunCommandAsync(int cmd)
    {
        switch (cmd)
        {
            case CmdOpen:
                _windows.BringToFront();
                break;
            case CmdAudio:
                await _activation.DownloadClipboardAsync(MediaKind.Audio);
                break;
            case CmdVideo:
                await _activation.DownloadClipboardAsync(MediaKind.Video);
                break;
            case CmdPause:
                foreach (var i in _queue.Items.Where(i => i.Status is DownloadStatus.Queued || i.Status.IsActive()).ToList()) await _queue.PauseAsync(i.Id);
                break;
            case CmdResume:
                foreach (var i in _queue.Items.Where(i => i.Status == DownloadStatus.Paused).ToList()) await _queue.ResumeAsync(i.Id);
                break;
            case CmdExit:
                _windows.RequestExit();
                break;
        }
    }

    public void Dispose()
    {
        if (_hwnd == IntPtr.Zero) return;
        UnregisterHotKey(_hwnd, HotkeyAudio);
        UnregisterHotKey(_hwnd, HotkeyVideo);
        if (_iconAdded)
        {
            var data = CreateData(0);
            Shell_NotifyIcon(NIM_DELETE, ref data);
        }

        DestroyWindow(_hwnd);
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        _hwnd = IntPtr.Zero;
    }

    // ---------------- Win32 ----------------

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, nint id, string? text);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
}

/// <summary>Registers the sonic:// link scheme for the current user (no admin rights).</summary>
public static class ProtocolRegistration
{
    private const string KeyPath = @"Software\Classes\sonic";

    public static void Apply(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
                return;
            }

            var exe = Environment.ProcessPath!;
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue(string.Empty, "URL:Sonic");
            key.SetValue("URL Protocol", string.Empty);
            using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue(string.Empty, $"\"{exe}\",0");
            using var command = key.CreateSubKey(@"shell\open\command");
            command.SetValue(string.Empty, $"\"{exe}\" \"%1\"");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Serilog.Log.Warning(ex, "Could not update the sonic:// registration");
        }
    }

    public const string AudioBookmarklet = "javascript:location.href='sonic://download?kind=audio&url='+encodeURIComponent(location.href)";
    public const string VideoBookmarklet = "javascript:location.href='sonic://download?kind=video&url='+encodeURIComponent(location.href)";
}

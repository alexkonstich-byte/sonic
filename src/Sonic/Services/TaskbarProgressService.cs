using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Sonic.ViewModels;

namespace Sonic.Services;

/// <summary>Shows overall download progress on Sonic's taskbar button (ITaskbarList3).</summary>
public sealed class TaskbarProgressService(DownloadsStore downloads, IWindowService windows)
{
    private ITaskbarList3? _taskbar;
    private DispatcherQueueTimer? _timer;
    private TaskbarState _lastState = TaskbarState.NoProgress;

    public void Start()
    {
        try
        {
            _taskbar = (ITaskbarList3)new TaskbarInstance();
            _taskbar.HrInit();
        }
        catch (COMException)
        {
            return; // Taskbar not available (e.g. Explorer not running).
        }

        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(700);
        _timer.Tick += (_, _) => Update();
        _timer.Start();
    }

    private void Update()
    {
        if (_taskbar is null || windows.MainWindow is null) return;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(windows.MainWindow);
        var running = downloads.Items.Where(i => i.IsRunning).ToList();
        var waiting = downloads.Items.Count(i => i.IsActive && !i.IsRunning);

        TaskbarState state;
        if (running.Count == 0) state = waiting > 0 ? TaskbarState.Paused : TaskbarState.NoProgress;
        else if (running.All(i => i.IsIndeterminate)) state = TaskbarState.Indeterminate;
        else state = TaskbarState.Normal;

        if (state != _lastState)
        {
            _taskbar.SetProgressState(hwnd, state);
            _lastState = state;
        }

        if (state == TaskbarState.Normal)
        {
            // Average across running and waiting downloads (waiting ones count as 0 %).
            var done = running.Sum(i => i.Progress);
            _taskbar.SetProgressValue(hwnd, (ulong)Math.Round(done), (ulong)(100 * (running.Count + waiting)));
        }
        else if (state == TaskbarState.Paused)
        {
            _taskbar.SetProgressValue(hwnd, 1, 1);
        }
    }

    private enum TaskbarState
    {
        NoProgress = 0,
        Indeterminate = 0x1,
        Normal = 0x2,
        Error = 0x4,
        Paused = 0x8,
    }

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
    private class TaskbarInstance;

    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);

        // ITaskbarList2
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);

        // ITaskbarList3 (only the members used here; order matters)
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, TaskbarState state);
    }
}

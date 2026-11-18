using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;

namespace Sonic.Services;

/// <summary>Access to the main window: handle, XAML root, pickers and activation.</summary>
public interface IWindowService
{
    Window? MainWindow { get; }
    XamlRoot? XamlRoot { get; }
    bool IsForeground { get; }
    void Attach(Window window);
    void BringToFront();
    Task<string?> PickFolderAsync();
    Task<string?> PickSaveFileAsync(string suggestedName, string typeDescription, string extension);
    Task<string?> PickOpenFileAsync(params string[] extensions);

    /// <summary>True after the user chose Exit (tray menu); closing then quits instead of hiding to the tray.</summary>
    bool ExitRequested { get; }

    void RequestExit();
}

public sealed partial class WindowService : IWindowService
{
    private Window? _window;

    public Window? MainWindow => _window;

    public XamlRoot? XamlRoot => _window?.Content?.XamlRoot;

    public bool IsForeground => _window is not null && GetForegroundWindow() == Hwnd;

    private IntPtr Hwnd => _window is null ? IntPtr.Zero : WinRT.Interop.WindowNative.GetWindowHandle(_window);

    public void Attach(Window window) => _window = window;

    public bool ExitRequested { get; private set; }

    public void RequestExit()
    {
        ExitRequested = true;
        if (_window is null) return;
        _window.DispatcherQueue.TryEnqueue(() =>
        {
            BringToFront(); // a confirmation may be needed if downloads are running
            _window.Close();
        });
    }

    public void BringToFront()
    {
        if (_window is null) return;
        if (_window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        _window.AppWindow.Show();
        _window.Activate();
        SetForegroundWindow(Hwnd);
    }

    public async Task<string?> PickFolderAsync()
    {
        if (_window is null) return null;
        var picker = new FolderPicker(_window.AppWindow.Id) { SuggestedStartLocation = PickerLocationId.MusicLibrary };
        var result = await picker.PickSingleFolderAsync();
        return result?.Path;
    }

    public async Task<string?> PickSaveFileAsync(string suggestedName, string typeDescription, string extension)
    {
        if (_window is null) return null;
        var picker = new FileSavePicker(_window.AppWindow.Id)
        {
            SuggestedFileName = suggestedName,
            SuggestedStartLocation = PickerLocationId.MusicLibrary,
        };
        picker.FileTypeChoices.Add(typeDescription, [extension]);
        var result = await picker.PickSaveFileAsync();
        return result?.Path;
    }

    public async Task<string?> PickOpenFileAsync(params string[] extensions)
    {
        if (_window is null) return null;
        var picker = new FileOpenPicker(_window.AppWindow.Id) { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        foreach (var ext in extensions) picker.FileTypeFilter.Add(ext);
        var result = await picker.PickSingleFileAsync();
        return result?.Path;
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);
}

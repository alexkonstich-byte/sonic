using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Sonic.Core.Abstractions;
using Sonic.Core.Models;

namespace Sonic.ViewModels;

public sealed partial class ToolInstallViewModel(IDependencyService dependencies, ILogger<ToolInstallViewModel> logger) : ObservableObject
{
    private CancellationTokenSource? _cts;

    public ToolKind Kind { get; private set; }

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _sourceText = string.Empty;
    [ObservableProperty] private string _stage = "Preparing…";
    [ObservableProperty] private double _percent;
    [ObservableProperty] private bool _isIndeterminate = true;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _succeeded;

    public void Initialize(ToolKind kind)
    {
        Kind = kind;
        var status = dependencies.GetStatus(kind);
        var verb = status.IsUsable ? "Update" : "Install";
        (Title, SourceText) = kind switch
        {
            ToolKind.YtDlp => ($"{verb} yt-dlp", "Downloads the latest yt-dlp.exe from the official GitHub releases (github.com/yt-dlp/yt-dlp) and verifies its SHA-256 checksum."),
            ToolKind.Ffmpeg => ($"{verb} FFmpeg", "Downloads the FFmpeg release build linked from ffmpeg.org (gyan.dev, about 100 MB) and verifies its SHA-256 checksum."),
            _ => ($"{verb} Deno", "Downloads Deno from its official GitHub releases (github.com/denoland/deno) and verifies its SHA-256 checksum."),
        };
    }

    public async Task RunAsync()
    {
        _cts = new CancellationTokenSource();
        IsRunning = true;
        Error = null;
        try
        {
            var progress = new Progress<InstallProgress>(p =>
            {
                Stage = p.Percent is { } pct ? $"{p.Stage} · {pct:0}%" : p.Stage;
                IsIndeterminate = p.Percent is null;
                Percent = p.Percent ?? 0;
            });
            await Task.Run(() => dependencies.InstallAsync(Kind, progress, _cts.Token));
            Succeeded = true;
            Stage = "Installed successfully.";
        }
        catch (OperationCanceledException)
        {
            Error = "Installation canceled.";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Installing {Tool} failed", Kind);
            Error = ex is HttpRequestException ? "Download failed. Check your internet connection. (" + ex.Message + ")" : ex.Message;
        }
        finally
        {
            IsRunning = false;
            IsIndeterminate = false;
        }
    }

    public void Cancel() => _cts?.Cancel();
}

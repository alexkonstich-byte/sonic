using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sonic.Core.Abstractions;
using Sonic.Core.Data;
using Sonic.Core.Models;
using Sonic.Core.Services;
using Xunit.Abstractions;

namespace Sonic.Core.Tests;

/// <summary>
/// End-to-end tests against the real yt-dlp/FFmpeg and network. Enabled with SONIC_INTEGRATION=1.
/// </summary>
public sealed class PipelineIntegrationTests(ITestOutputHelper output) : IAsyncLifetime
{
    // "Me at the zoo" – the first YouTube video, 19 seconds long.
    private const string TestUrl = "https://www.youtube.com/watch?v=jNQXAC9IVRw";

    private readonly string _root = Directory.CreateTempSubdirectory("sonic-it").FullName;
    private ServiceProvider _sp = null!;

    private static bool Enabled => Environment.GetEnvironmentVariable("SONIC_INTEGRATION") == "1";

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(new XunitLoggerProvider(output)).SetMinimumLevel(LogLevel.Information));
        services.AddSonicCore("Sonic-Tests/1.0");
        services.AddSingleton<IAppPaths>(new AppPaths(Path.Combine(_root, "data")));
        _sp = services.BuildServiceProvider();

        var settings = _sp.GetRequiredService<ISettingsService>();
        await settings.LoadAsync();
        await settings.UpdateAsync(s =>
        {
            s.DownloadDirectory = Path.Combine(_root, "music");
            s.MusicFolders = [];
        });
        await _sp.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
        await _sp.GetRequiredService<IDependencyService>().RefreshAsync(checkForUpdates: false);
    }

    public async Task DisposeAsync()
    {
        await _sp.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [SkippableFact]
    public async Task Analyze_returns_metadata_and_formats()
    {
        Skip.IfNot(Enabled);
        var info = await _sp.GetRequiredService<IYtDlpService>().AnalyzeAsync(TestUrl);
        output.WriteLine($"{info.Title} by {info.Uploader}, {info.DurationSeconds}s, {info.AudioFormats.Count} audio formats");
        Assert.Equal(SourcePlatform.YouTube, info.Source);
        Assert.False(string.IsNullOrWhiteSpace(info.Title));
        Assert.True(info.DurationSeconds > 10);
        Assert.NotEmpty(info.AudioFormats);
        Assert.NotNull(info.ThumbnailUrl);
    }

    [SkippableTheory]
    [InlineData(AudioFormat.Mp3)]
    [InlineData(AudioFormat.M4a)]
    [InlineData(AudioFormat.Opus)]
    [InlineData(AudioFormat.Flac)]
    public async Task Downloads_converts_tags_and_imports(AudioFormat format)
    {
        Skip.IfNot(Enabled);
        var queue = _sp.GetRequiredService<IDownloadQueueService>();
        await queue.InitializeAsync();

        var progressValues = new List<double>();
        var done = new TaskCompletionSource<DownloadItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Changed += (_, e) => { if (e.Kind == DownloadChangeKind.Progress) lock (progressValues) progressValues.Add(e.Item.Progress); };
        queue.DownloadFinished += (_, item) => done.TrySetResult(item);

        await queue.EnqueueAsync(new DownloadRequest { Url = TestUrl, Format = format, Quality = AudioQuality.Standard });
        var result = await done.Task.WaitAsync(TimeSpan.FromMinutes(4));

        output.WriteLine($"Status {result.Status}: {result.ErrorMessage} {result.ErrorDetails}");
        Assert.Equal(DownloadStatus.Completed, result.Status);
        Assert.True(File.Exists(result.FilePath));
        Assert.StartsWith(Path.Combine(_root, "music"), result.FilePath);
        Assert.NotEmpty(progressValues);

        var meta = _sp.GetRequiredService<IMetadataService>().Read(result.FilePath!);
        output.WriteLine($"Tags: '{meta.Title}' / '{meta.Artist}' / {meta.DurationSeconds:0.0}s / art {meta.Artwork?.Length ?? 0} bytes");
        Assert.False(string.IsNullOrWhiteSpace(meta.Title));
        Assert.False(string.IsNullOrWhiteSpace(meta.Artist));
        Assert.NotNull(meta.Artwork);
        Assert.InRange(meta.DurationSeconds, 15, 25);

        var tracks = await _sp.GetRequiredService<ILibraryService>().GetTracksAsync();
        Assert.Contains(tracks, t => string.Equals(t.FilePath, result.FilePath, StringComparison.OrdinalIgnoreCase) && t.ArtworkPath is not null);

        // A second download of the same item must not overwrite the first file.
        var second = new TaskCompletionSource<DownloadItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.DownloadFinished += (_, item) => { if (item.Id != result.Id) second.TrySetResult(item); };
        await queue.EnqueueAsync(new DownloadRequest { Url = TestUrl, Format = format, Quality = AudioQuality.Standard });
        var again = await second.Task.WaitAsync(TimeSpan.FromMinutes(4));
        Assert.Equal(DownloadStatus.Completed, again.Status);
        Assert.NotEqual(result.FilePath, again.FilePath);
        Assert.True(File.Exists(result.FilePath));
    }

    [SkippableTheory]
    [InlineData(VideoContainer.Mp4, SubtitleMode.Embed)]
    [InlineData(VideoContainer.Mkv, SubtitleMode.Separate)]
    public async Task Downloads_video_with_subtitles(VideoContainer container, SubtitleMode mode)
    {
        Skip.IfNot(Enabled);
        // TED publishes manual subtitles in many languages.
        const string url = "https://www.ted.com/talks/sir_ken_robinson_do_schools_kill_creativity";
        var queue = _sp.GetRequiredService<IDownloadQueueService>();
        await queue.InitializeAsync();
        var stages = new List<string>();
        var done = new TaskCompletionSource<DownloadItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Changed += (_, e) => { lock (stages) if (e.Item.StageText is { } s && !stages.Contains(s)) stages.Add(s); };
        queue.DownloadFinished += (_, item) => done.TrySetResult(item);

        var videoDir = Path.Combine(_root, "videos");
        await queue.EnqueueAsync(new DownloadRequest
        {
            Url = url,
            Kind = MediaKind.Video,
            OutputDirectory = videoDir,
            FilenameTemplate = "{uploader}/{title} [{resolution}]",
            Video = new VideoOptions
            {
                Container = container, MaxHeight = 240, SubtitleMode = mode, SubtitleFormat = SubtitleFormat.Srt,
                SubtitleLanguages = "en,ru",
            },
        });
        var result = await done.Task.WaitAsync(TimeSpan.FromMinutes(8));
        output.WriteLine($"Status {result.Status}: {result.ErrorMessage} {result.ErrorDetails}");
        output.WriteLine("Stages: " + string.Join(" → ", stages));
        output.WriteLine("File: " + result.FilePath);
        Assert.Equal(DownloadStatus.Completed, result.Status);
        Assert.Equal("." + container.ToString().ToLowerInvariant(), Path.GetExtension(result.FilePath));
        Assert.StartsWith(videoDir, result.FilePath);
        Assert.Matches(@"\[\d+p\]", result.FilePath);
        Assert.Contains(stages, s => s.StartsWith("Downloading", StringComparison.Ordinal));

        var probe = await _sp.GetRequiredService<IFfmpegService>().ProbeAsync(result.FilePath!);
        Assert.NotNull(probe.Codec); // has an audio stream after merging
        var files = Directory.GetFiles(Path.GetDirectoryName(result.FilePath!)!);
        output.WriteLine("Files: " + string.Join(", ", files.Select(Path.GetFileName)));
        if (mode == SubtitleMode.Separate)
        {
            Assert.Contains(files, f => f.EndsWith(".en.srt", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(files, f => f.EndsWith(".ru.srt", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            Assert.DoesNotContain(files, f => f.EndsWith(".srt", StringComparison.OrdinalIgnoreCase));
            // Embedded: the container must have subtitle streams.
            var runner = _sp.GetRequiredService<IProcessRunner>();
            var ffprobe = _sp.GetRequiredService<IDependencyService>().FfprobePath!;
            var streams = await runner.RunAsync(new ProcessRequest
            {
                FileName = ffprobe,
                Arguments = ["-v", "error", "-select_streams", "s", "-show_entries", "stream=codec_name:stream_tags=language", "-of", "csv=p=0", result.FilePath!],
                CaptureStandardOutput = true,
            });
            output.WriteLine("Subtitle streams: " + streams.StandardOutput.Replace('\n', ' '));
            Assert.Contains("eng", streams.StandardOutput);
            Assert.Contains("rus", streams.StandardOutput);
        }

        // Videos stay out of the music library.
        Assert.DoesNotContain(await _sp.GetRequiredService<ILibraryService>().GetTracksAsync(), t => t.FilePath == result.FilePath);
    }

    [SkippableFact]
    public async Task Downloads_rutube_video_as_mp4()
    {
        Skip.IfNot(Enabled);
        var queue = _sp.GetRequiredService<IDownloadQueueService>();
        await queue.InitializeAsync();
        var done = new TaskCompletionSource<DownloadItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.DownloadFinished += (_, item) => done.TrySetResult(item);
        await queue.EnqueueAsync(new DownloadRequest
        {
            Url = "https://rutube.ru/video/c6cc4d620b1d4338901770a44b3e82f4/",
            Kind = MediaKind.Video,
            OutputDirectory = Path.Combine(_root, "videos"),
            FilenameTemplate = "{title} [{resolution}]",
            Video = new VideoOptions { Container = VideoContainer.Mp4, MaxHeight = 360, Codec = VideoCodecPreference.BestQuality },
        });
        var result = await done.Task.WaitAsync(TimeSpan.FromMinutes(6));
        output.WriteLine($"Status {result.Status}: {result.ErrorMessage} → {result.FilePath}");
        Assert.Equal(DownloadStatus.Completed, result.Status);
        Assert.EndsWith(".mp4", result.FilePath);
        Assert.True(new FileInfo(result.FilePath!).Length > 1_000_000);
    }

    [SkippableFact]
    public async Task Cancel_stops_download_and_cleans_up()
    {
        Skip.IfNot(Enabled);
        var queue = _sp.GetRequiredService<IDownloadQueueService>();
        await queue.InitializeAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Changed += (_, e) => { if (e.Item.Status == DownloadStatus.Downloading) started.TrySetResult(); };
        var item = await queue.EnqueueAsync(new DownloadRequest { Url = TestUrl, Format = AudioFormat.Flac });
        await started.Task.WaitAsync(TimeSpan.FromMinutes(1));
        await queue.CancelAsync(item.Id);
        Assert.Equal(DownloadStatus.Canceled, queue.Items.Single(i => i.Id == item.Id).Status);
        var work = Path.Combine(_sp.GetRequiredService<IAppPaths>().TempDirectory, item.Id.ToString("N"));
        Assert.False(Directory.Exists(work));
    }

    [SkippableFact]
    public async Task Unavailable_video_fails_with_friendly_message()
    {
        Skip.IfNot(Enabled);
        var yt = _sp.GetRequiredService<IYtDlpService>();
        var ex = await Assert.ThrowsAsync<YtDlpException>(() => yt.AnalyzeAsync("https://www.youtube.com/watch?v=aaaaaaaaaaa"));
        output.WriteLine(ex.Message);
        Assert.DoesNotContain("ERROR:", ex.Message);
    }
}

internal sealed class XunitLoggerProvider(ITestOutputHelper output) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new XunitLogger(output, categoryName);
    public void Dispose() { }

    private sealed class XunitLogger(ITestOutputHelper output, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            try { output.WriteLine($"[{logLevel}] {category.Split('.').Last()}: {formatter(state, exception)}"); }
            catch (InvalidOperationException) { /* Test already finished. */ }
        }
    }
}

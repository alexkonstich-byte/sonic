# Sonic

A native Windows 11 music downloader and library manager built with **WinUI 3 / Windows App SDK 2.6**, **.NET 10** and MVVM.
Sonic downloads audio with **yt-dlp**, converts it with **FFmpeg**, tags it with **TagLib#**, keeps a **SQLite** library
(EF Core), and imports playlists via the official **Spotify Web API** (metadata only).

## Requirements

| Tool | Notes |
| --- | --- |
| Windows 10 19041+ (Windows 11 recommended for Mica) | x64 or ARM64 |
| .NET SDK 10.0.404+ | Only for building. `global.json` pins the feature band. |
| yt-dlp, FFmpeg | Detected on PATH, or installed by Sonic into `%LOCALAPPDATA%\Sonic\tools` (Settings › Integrations). |
| Deno or Node.js (optional) | Used by yt-dlp for YouTube's JavaScript challenges. Sonic can install Deno. |

No Visual Studio is required; everything builds with the `dotnet` CLI.

## Build, run, test

```powershell
dotnet build src\Sonic\Sonic.csproj                    # Debug build (x64)
src\Sonic\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Sonic.exe

dotnet test tests\Sonic.Core.Tests                      # unit + offline integration tests (needs FFmpeg)
$env:SONIC_INTEGRATION = "1"; dotnet test tests\Sonic.Core.Tests   # also runs real YouTube download tests

dotnet publish src\Sonic\Sonic.csproj -c Release -r win-x64 -o artifacts\Sonic-win-x64   # self-contained release
```

The release folder is self-contained (no .NET or Windows App Runtime install needed): copy it anywhere and run `Sonic.exe`.

## Install, updates and releases

Download **SonicMusic-win-Setup.exe** from the latest [release](https://github.com/alexkonstich-byte/sonic/releases).
It installs per user (no admin) into `%LOCALAPPDATA%\SonicMusic`, adds Start menu and desktop shortcuts, and keeps
your library and settings in `%LOCALAPPDATA%\Sonic` (untouched by updates and uninstall).

The installed app checks GitHub Releases every 6 hours (Settings › About › Updates), downloads updates in the
background (usually a small delta) and installs them on restart — or quietly when Sonic is closed.

Publishing a release (maintainers):

```powershell
gh auth login                                   # once
./scripts/release.ps1 -Version 1.3.0 -Publish   # build, pack (Velopack), upload as GitHub release v1.3.0
./scripts/release.ps1 -Version 1.3.0            # build only, into artifacts\releases
```

The script also builds the signed identity package used for the Windows Share menu (see below). It signs it with a
self-signed certificate (`CN=Sonic Local Signing`) kept in the current user's certificate store.

## Solution layout

```
src/Sonic.Core            Platform logic, no UI
  Abstractions/           Service interfaces (IYtDlpService, IFfmpegService, IMetadataService, ISpotifyService, …)
  Models/                 Entities and DTOs (DownloadItem, Track, Playlist, AppSettings, …)
  Services/               Implementations: download queue + pipeline, yt-dlp, FFmpeg, TagLib, Spotify, library, playlists,
                          dependency detection/installation, settings
  Repositories/ Data/     EF Core SQLite context, migrations, repositories (IDbContextFactory, short-lived contexts)
  Processes/              Async process runner + Windows Job Object (child processes die with Sonic)
  Helpers/                Title parsing, filename templates, yt-dlp output parsing, version comparison, DPAPI, Recycle Bin
src/Sonic                 WinUI 3 app
  Views/ Views/Dialogs/   Pages and ContentDialogs (x:Bind only)
  ViewModels/             CommunityToolkit.Mvvm view models; DownloadsStore/ToolsStore marshal Core events to the UI thread
  Services/               Navigation, dialogs, theme, window/pickers, toasts, in-app notifications, startup registration
  Controls/ Converters/ Styles/ Helpers/
tests/Sonic.Core.Tests    xUnit: helpers, conversion planning, pipeline metadata, tagging in all formats, library, playlists,
                          real end-to-end downloads
```

## How a download works

1. **Queue** (`DownloadQueueService`) persists the item, then runs up to *N* items at once (configurable).
2. **yt-dlp** downloads the best audio stream into `%LOCALAPPDATA%\Sonic\temp\<id>` with a structured
   `--progress-template`, so progress, speed and ETA come straight from yt-dlp. It also writes the info JSON and thumbnail.
3. **FFmpeg** converts to the chosen format. When the source codec already fits (AAC→M4A, Opus→Opus, MP3→MP3, …)
   the stream is **copied without re-encoding**; progress comes from `-progress pipe:1`.
4. **TagLib#** writes title/artist/album/album artist/track/year/genre/comment and embeds square-cropped artwork.
   Tags copied from the source are kept; known values replace them.
5. The file is moved to the download folder using the filename template, **never overwriting** (`name (2).mp3`), and is
   added to the library (and linked to a playlist entry when it came from "Find source").

Pause keeps partial data so yt-dlp resumes; quitting during a download (or a crash) resumes it on next launch.
Transient failures (network, HTTP 403/429) retry automatically with back-off.

## Video downloads

Switch Home to **Video** before or after analyzing a link. Options (defaults live in Settings › Video):

| Option | Choices |
| --- | --- |
| Format | MP4 (plays everywhere), MKV (any codec), WebM |
| Quality | Best available or a maximum height; the list shows the resolutions the video actually has |
| Codec | Most compatible (H.264 + AAC) or best quality (VP9/AV1) |
| Subtitles | None, embedded in the video, or separate files · SRT / WebVTT / ASS · languages picked from the video's own tracks (manual + original-language auto captions); playlists use typed language codes |
| Extras | Embed thumbnail as cover (not WebM), chapters, metadata |
| File name | Per download, tokens `{title} {uploader} {id} {date} {year} {resolution}`, `/` creates folders, live preview |

yt-dlp downloads the streams, FFmpeg merges/remuxes them, and Sonic shows each step (downloading video/audio/subtitles,
merging, embedding subtitles…). Videos go to `Downloads\Sonic\Video` and are not added to the music library.
Containers only accept certain subtitle codecs, so Sonic uses WebVTT when embedding into WebM and SRT for MP4.

## Download a list

Home › **List** (or the "Download a list" card, or drop a file / multi-line text onto the Home page):

- Paste one item per line — links are downloaded directly, anything else (`Artist - Title`, `1. Song name`…) is searched
  on YouTube. Select **Find matches** to review each match (title, channel, length, *match 1 of 3* with a button to try
  the next result) before downloading.
- Open **.txt**, **.csv** or **.m3u/.m3u8**. CSV columns are detected automatically (url/link, title/track name,
  artist(s), album, duration — including Exportify/Spotify exports; `,` `;` or tab separated). Durations pick the
  closest-length search result, and CSV artist/title/album are written into the tags. M3U files from other players use
  their `#EXTINF` titles.
- Choose Audio or Video and the format/quality; for audio, **Save as playlist** creates a playlist whose entries are linked
  to the files as they finish downloading.

## Automatic setup and zapret

- With **Install and update tools automatically** (default on), missing yt-dlp, FFmpeg and Deno are downloaded at startup
  from their official sources (SHA-256 verified) into `%LOCALAPPDATA%\Sonic\tools`; Sonic's own yt-dlp is kept current.
- **zapret** (Flowseal/zapret-discord-youtube) is only fetched when YouTube is actually blocked: Sonic checks Windows'
  connectivity endpoint, youtube.com and googlevideo.com; if the internet works but YouTube doesn't, it downloads the
  latest release from GitHub (checksum verified) and offers **Start zapret** (Windows asks for administrator approval —
  WinDivert needs it). An existing installation (e.g. on the Desktop) is detected automatically. Strategies, start/stop,
  a YouTube check and "start with Sonic" live in Settings › Integrations › zapret. YouTube network errors in downloads or
  analysis also suggest zapret.

## YouTube account

The **YouTube** page shows your watch history, liked videos, Watch later and subscription feed (50 at a time, *Load
more*, search), with one-click **Audio**/**Video** downloads, multi-select, or "Choose format…" for the full options.
Sign-in uses cookies imported from your browser (Settings › Integrations › Cookies) — Google blocks sign-in inside
embedded browsers, and Sonic never sees your password.

## Player and desktop integration

- Built-in player (Windows MediaPlayer): queue, shuffle, repeat, seek, volume; media keys and the Windows media overlay
  work. Library/playlist/Home "Play" use it; "Open with default app" is still available.
- Clipboard watcher: copied YouTube/SoundCloud/VK/… links are offered for download (toast with Audio/Video buttons in
  the background).
- Tray icon (open, download from clipboard, pause/resume all, exit) and **Close to tray**.
- Global shortcuts: **Ctrl+Alt+D** downloads the copied link as audio, **Ctrl+Alt+Shift+D** as video.
- `sonic://download?kind=audio|video&url=…` links (registered per user) and "Send to Sonic" bookmarklets.
- Network: per-download speed limit and an http/https/socks proxy (used by yt-dlp and Sonic's own requests).
- SponsorBlock: cut non-music parts from music videos; mark or cut sponsor segments in videos.

## Smart downloads

- **Search**: type words instead of a link on Home to search YouTube; each result has Preview, Audio, Video and
  Options (full analysis).
- **Download now** (or Ctrl+Enter in the link box) queues a link with the current options without analyzing it.
  **Ctrl+V** anywhere on Home (outside text fields) pastes and analyzes the copied link.
- **Presets**: apply saved option sets (MP3 320, FLAC, original audio, 1080p MP4, 720p with subtitles…) from the
  "Apply preset" box; save the current options with the button next to it; manage them in Settings › Smart downloads.
- **Already downloaded**: links found in the history are marked (Home, search, YouTube page, list dialog — where they
  start deselected), and quick downloads ask before downloading an existing file again.
- **Preview**: stream a video's audio in the player before downloading (Home, search results, playlist entries, YouTube
  page). Nothing is saved.
- **Game mode**: while a Steam game runs (Steam's `RunningAppID`, trusted only while the Steam client is running), new
  downloads wait; the Downloads page shows the game and a **Download anyway** button that lifts the pause until that
  game exits.
- **Auto-retry**: downloads that failed with network errors in the last 6 hours are retried when Windows reports the
  network back, and YouTube failures are retried when zapret starts.
- **Taskbar progress**: the taskbar button shows overall progress (yellow while paused).
- **Drag out**: drag songs from the Library or a playlist into Explorer, Telegram, Discord, a DAW… Library drags only
  copy; playlist drags (which also reorder) hand out hard links from a temp folder so a "move" never takes the original.

## Album art, Discord, Share menu

- **Real album covers**: downloads look the song up on iTunes and Deezer (public APIs, no keys) and embed the square
  album cover instead of the video thumbnail when the match is confident (artist, title and length; karaoke/cover
  versions are rejected). An empty album and year are filled from the same release. Library › right-click ›
  **Find album art** does the same for existing songs; Edit metadata › **Find cover online** shows all candidates.
- **Discord status** (Settings › Desktop integration, off by default): shows the playing song as "Listening to …"
  with a progress bar through the local Discord app. Create an application in the
  [Discord Developer Portal](https://discord.com/developers/applications) (its name is what Discord shows, e.g. "Sonic")
  and paste its Application ID.
- **Windows Share menu** (installed version, Settings › Desktop integration): registers a signed identity package for
  the installed `Sonic.exe` ("packaging with external location"). Its self-signed certificate is added once to the
  machine's Trusted People store (one administrator prompt). Then *Share › Sonic* in a browser or app opens the link on
  Home. *Remove* unregisters the package and offers to remove the certificate.

## Cookies (signed-in downloads)

Settings › Integrations › **Cookies** lets yt-dlp download as your signed-in self — needed for age-restricted and
members-only videos and when YouTube says "confirm you're not a bot".

- **Import from browser**: Sonic lists installed browsers and their profiles (Firefox, Chrome, Edge, Brave, Vivaldi,
  Chromium, Opera, Yandex Browser) and copies that profile's cookies once, showing how many were imported and whether a
  signed-in YouTube session is present. Firefox works while running; Chromium browsers lock their cookie database while
  open (Sonic warns first) and recent versions may encrypt cookies so yt-dlp can't read them.
- **Import cookies.txt**: any Netscape-format cookies file (e.g. from a browser extension).
- **Read from the browser on every download**: no snapshot, always fresh (Chromium browsers must be closed).

Imported cookies are stored DPAPI-encrypted for your Windows account (`%LOCALAPPDATA%\Sonic\cookies.dat`). Each yt-dlp run
gets its own temporary copy that is deleted when the run ends (leftovers are swept at startup). "Remove" deletes them.

## Data locations

| What | Where |
| --- | --- |
| Settings | `%LOCALAPPDATA%\Sonic\settings.json` (Spotify secret is DPAPI-encrypted) |
| Library database | `%LOCALAPPDATA%\Sonic\sonic.db` (SQLite, WAL, EF Core migrations applied at startup) |
| Logs | `%LOCALAPPDATA%\Sonic\logs` (Serilog, daily rolling, 14 days) |
| Managed tools | `%LOCALAPPDATA%\Sonic\tools` |
| Default downloads | `Downloads\Sonic` (audio) and `Downloads\Sonic\Video` (video); change per download with **Save to** on Home or in the list dialog (Browse…, recent folders, Default) |

## Spotify

Create an app at <https://developer.spotify.com/dashboard>, then paste its Client ID and Client Secret in
Settings › Integrations › Spotify. Sonic uses the Client Credentials flow to read public playlists/albums and to look up
track metadata in the metadata editor. Spotify-curated/algorithmic playlists are not available to third-party apps.
**Sonic never accesses Spotify audio.** For playlist entries that aren't in your library, "Find source" searches YouTube and
lets you choose the upload to download; Spotify's track information is then written into the file.

## Legal

Only download content you're authorized to download and respect each platform's terms. Sonic refuses DRM-protected
streaming services (Spotify, Apple Music, Deezer, Tidal, …) and yt-dlp refuses DRM-protected media.

## Known limitations

- The UI is English only; there is no language setting yet.
- No built-in audio player: Play opens the file in the default Windows app.
- Reordering by drag and drop is available in playlist detail lists; "Find source" works one song at a time (by design, so
  the user reviews each match).

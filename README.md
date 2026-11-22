# Sonic

A native Windows 10/11 app for downloading music and video and keeping a local music library — built on yt-dlp and FFmpeg,
with a Fluent (Mica, light/dark) interface.

**Install:** download **SonicMusic-win-Setup.exe** from the latest [release](https://github.com/alexkonstich-byte/sonic/releases).
It installs per user (no administrator rights) into `%LOCALAPPDATA%\SonicMusic`, adds Start menu and desktop shortcuts,
and updates itself. Your library and settings live separately in `%LOCALAPPDATA%\Sonic` and survive updates and uninstall.

---

## Features

### Downloading audio

- **Paste a link** on Home from YouTube, YouTube Music, SoundCloud, Bandcamp, VK, Rutube, Vimeo or any of the
  [sites yt-dlp supports](https://github.com/yt-dlp/yt-dlp/blob/master/supportedsites.md). **Analyze** shows the title,
  artist, length and thumbnail; playlists and albums list every item with checkboxes.
- **Formats:** MP3, AAC/M4A, Opus, Vorbis/OGG, FLAC, WAV or **Original** (no re-encoding), with quality presets. You can
  also pick the exact source audio stream. When the source codec already fits the target, the stream is copied without
  re-encoding.
- **Tags and artwork:** title, artist, album, album artist, track number, year and genre are written to every file.
  "Artist - Title (Official Video)" titles are cleaned up and split.
- **Real album covers:** instead of the video thumbnail, Sonic looks the song up on iTunes and Deezer and embeds the
  1000×1000 album cover when the match is confident (artist, title and length must agree; karaoke, cover and tribute
  versions are rejected). An empty album and year are filled in from the same release.
- **File names** come from a template (`{artist} - {title}`, `{album}`, `{track}`, `{year}`, `{uploader}`…). `/` creates
  folders, a live preview shows the result, and existing files are never overwritten (`name (2).mp3`).
- **Save to:** downloads go to `Downloads\Sonic` by default. Choose another folder per download (Browse, recent
  folders, Default).
- **SponsorBlock** can cut non-music parts (intros, talking) out of music videos.

### Downloading video

- Switch Home to **Video**. Container: MP4 (plays everywhere), MKV (any codec) or WebM.
- **Quality:** best available, or a maximum resolution taken from what the video actually offers (up to 8K).
- **Codec:** most compatible (H.264 + AAC) or best quality (VP9/AV1).
- **Subtitles:** none, embedded or separate files; SRT, WebVTT or ASS. Languages are picked from the video's own tracks,
  including auto-generated captions.
- **Extras:** thumbnail as cover, chapters, and SponsorBlock (mark sponsor segments as chapters or cut them out).
- Each step is shown separately: downloading video, audio and subtitles, then merging and embedding.
  Videos go to `Downloads\Sonic\Video`.

### Faster ways to download

- **Search:** type words instead of a link to search YouTube. Each result has Preview, Audio, Video and Options.
- **Download now** (or Ctrl+Enter) queues a link with the current options, skipping analysis.
  **Ctrl+V** anywhere on Home pastes and analyzes the copied link.
- **Presets:** apply saved option sets (MP3 320, FLAC, original audio, 1080p MP4, 720p with subtitles…) in one click.
  Save your own; manage them in Settings › Smart downloads.
- **Preview:** stream a song's audio in the built-in player before downloading. Nothing is saved.
- **Already downloaded:** links in your download history are marked on Home, in search results, on the YouTube page and
  in the list dialog (where they start unticked). Quick downloads ask before fetching a file you already have.
- **Download a list:** paste links or "Artist - Title" lines, or open a .txt, .csv or .m3u file (Exportify and Spotify
  CSV exports work). Songs are searched on YouTube with a length check. You can review each match and switch to the next
  result, then download everything as audio or video, optionally saving it as a playlist.
- **Clipboard watcher:** copy a link anywhere and Sonic offers to download it.
- **Global shortcuts:** Ctrl+Alt+D downloads the copied link as audio, Ctrl+Alt+Shift+D as video.
- **Browser integration:** `sonic://download?kind=audio|video&url=…` links and "Send to Sonic" bookmarklets.
  In the installed version, Sonic can also appear in the **Windows Share menu**: *Share › Sonic* from a browser or app
  opens the link.

### The download queue

- Real progress, speed and ETA. **Pause, resume, cancel and retry** each download, or all of them at once.
- Several downloads run in parallel (configurable). Unfinished downloads continue after a restart or crash.
  Network errors (HTTP 403/429) retry automatically with back-off.
- **Auto-retry:** downloads that failed because of the network are retried when the connection comes back, and
  YouTube failures are retried when zapret starts.
- **Game mode:** while a Steam game runs, new downloads wait so your connection stays free. The Downloads page shows the
  game and a **Download anyway** button. Apps that run through Steam but aren't games (Wallpaper Engine by default)
  are ignored. Edit that list in Settings › Smart downloads › **Not games**: pick any installed Steam app, or remove one.
  The banner's "This isn't a game" link adds the current app.
- **Taskbar progress:** the taskbar button shows overall progress (yellow while paused). Windows notifications
  appear when downloads finish in the background.
- **Speed limit and proxy** (http/https/socks), used for every download.

### Library

- Scans your music folders (Downloads\Sonic and any you add), watches them for changes, and reads tags and artwork.
- **Views:** songs (sort, search, filter by format), albums and artists, plus **duplicate detection** (same
  artist/title across files).
- **Edit metadata** for any song, including artwork. You can pick an image, **Find cover online** (iTunes/Deezer
  candidates), or look tags up on Spotify. Right-click › **Find album art** fixes covers for many songs at once.
- **Favorites**, remove from library, or move files to the Recycle Bin.
- **Drag songs out** of the library or a playlist into Explorer, Telegram, Discord, a DAW and so on.
  Dropping never moves a file out of your library.

### Playlists and Spotify

- Create local playlists, add songs from the library, reorder by drag and drop, and export them as M3U.
- **Import from Spotify:** paste a public playlist or album link to get every song's metadata through the official
  Spotify Web API (your own Client ID and Secret). Spotify audio is never accessed.
- **Paste and go:** paste a Spotify playlist or album link on Home (Ctrl+V, Paste, or straight into the box) and Sonic
  downloads it right away: it imports the playlist, skips songs you already have and queues the rest from YouTube with
  the current audio format and folder. Progress shows on Home, and a summary appears when everything is queued.
- **Download all:** pick a folder and Sonic scans it, including subfolders, plus your whole library for songs that already
  exist. It matches by tags or "Artist - Title" file names, ignores suffixes like "- Remastered 2011" or "(Radio Edit)",
  and checks the length. Songs it finds are linked instead of downloaded again. The rest is searched on YouTube (closest
  length, skipping karaoke/cover/live uploads) and queued into that folder. Songs that weren't found are listed so you
  can use **Find source** on them.
- **Number files in playlist order:** an option in Download all names files by their position
  (`01 - Artist - Title.mp3`, the number goes before the file name, not the folders) and queues them in playlist
  order, so Explorer and players list them like the playlist.
- **Liked Songs:** sign in with your Spotify account (Settings › Integrations › Spotify; add the shown Redirect URI to
  your Spotify app) and Sonic checks your Liked Songs every 30 minutes. New likes, or all of them, are found on YouTube
  and downloaded into the folder you pick; songs you already have are linked. They appear in a "Liked Songs" playlist.
  Sonic only reads your library.
- **Match with library** links playlist entries to files you already have. **Refresh from Spotify** picks up changes to
  the playlist.

### YouTube account

- The **YouTube** page shows your watch history, liked videos, Watch later and subscriptions feed, with search,
  "Load more", multi-select, one-click Audio/Video downloads, preview, and "Downloaded" badges.
- **Sign-in** uses cookies imported from your browser: Firefox, Chrome, Edge, Brave, Vivaldi, Opera or Yandex, or a
  cookies.txt file. Sonic never sees your password. Cookies are stored encrypted for your Windows account and also
  unlock age-restricted and members-only videos and "confirm you're not a bot" checks.

### Player

- Built-in player with a queue, shuffle, repeat, seek and volume. Media keys and the Windows media overlay work.
- **Discord status** (optional): shows "Listening to *song*" with a progress bar through the Discord app. Create an
  application in the [Discord Developer Portal](https://discord.com/developers/applications) and paste its Application ID
  in Settings › Desktop integration.

### Setup, network and updates

- **Automatic tools:** missing yt-dlp, FFmpeg and Deno are downloaded from their official sources (SHA-256 verified) and
  kept up to date.
- **zapret:** when the internet works but YouTube doesn't, Sonic downloads zapret (Flowseal/zapret-discord-youtube) or
  uses an existing copy, such as one on the Desktop. It offers to start zapret, which needs an administrator prompt,
  and suggests it whenever YouTube network errors appear. You can choose strategies, check YouTube and start zapret
  with Sonic.
- **Tray icon** and **Close to tray** (downloads keep running), **Launch at sign-in**, light/dark theme.
- **Updates:** the installed app checks GitHub Releases every 6 hours and downloads updates in the background, usually a
  small delta. They install on restart, or quietly when Sonic closes. See Settings › About.

### Data locations

| What | Where |
| --- | --- |
| Settings, library database, logs, tools | `%LOCALAPPDATA%\Sonic` (`settings.json`, `sonic.db`, `logs\`, `tools\`) |
| App | `%LOCALAPPDATA%\SonicMusic` |
| Downloads | `Downloads\Sonic` (audio), `Downloads\Sonic\Video` (video), or any folder you choose |

Only download content you're authorized to download, and respect each platform's terms. Sonic refuses DRM-protected
streaming services, and yt-dlp refuses DRM-protected media. The interface is English only.

---

## Architecture

- **Sonic.Core** (.NET 10, no UI) holds the services behind interfaces: download queue and pipeline (yt-dlp → FFmpeg → TagLib#), library and playlists (EF Core + SQLite), Spotify/iTunes/Deezer clients, settings, tool installer and zapret.
- **Sonic** (WinUI 3 / Windows App SDK, unpackaged) is MVVM on CommunityToolkit.Mvvm with `x:Bind`. It runs in a .NET Generic Host with dependency injection and Serilog, is single-instance via `AppInstance`, and adds tray, hotkeys, toasts and the player on top.
- External tools run through an async process runner inside a Windows Job Object, so child processes die with Sonic. Progress comes from yt-dlp's `--progress-template` and FFmpeg's `-progress`.
- **SonicShare.exe** is a small helper that alone carries the package identity used for the Share menu. Velopack builds the installer and delta updates from GitHub Releases (`scripts/release.ps1 -Version x.y.z -Publish`).
- Build with `dotnet build src\Sonic\Sonic.csproj`; test with `dotnet test tests\Sonic.Core.Tests` (set `SONIC_INTEGRATION=1` to include real downloads).

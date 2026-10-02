<p align="center"><img src="docs/icon.png" width="96" alt="ReelKeep"></p>

<h1 align="center">ReelKeep</h1>

<p align="center"><b>Get your videos back.</b><br>
A free Windows app that turns video you've published online back into files you own — for when the online copy is the only one left.</p>

---

## Why ReelKeep?

Hard drives fail. Old laptops get wiped. Project folders get "tidied up", phones get replaced, and that external drive
from 2017 is nowhere to be found. Very often the only surviving copy of a video is the one you **published** —
on your channel, your portfolio, your course page or your company's video library.

ReelKeep rebuilds a proper local copy from that published version and keeps it organised:

- **Recover lost originals** – restore your own uploads after losing the source files or the editing project.
- **Back up before it disappears** – keep an offline copy before closing an account, leaving a job or moving platforms,
  or simply in case a hosting service removes something.
- **Archive what you have the right to keep** – talks you gave, lessons you recorded, family videos, Creative Commons
  material, footage you've been given permission to use.
- **Reuse it** – save the audio as a podcast or music file, or cut a clip out for a highlight reel.

Every file comes back with its **details** — title, creator, upload date, link, description, chapters and thumbnail —
so months later you still know exactly where each one came from.

## Features

- **Paste a link, several links, or a whole playlist / channel** and pick what to keep.
- **Download queue** with up to 6 at a time, pause / resume and retry.
- **Save as video, audio, or both** — audio as M4A, MP3, FLAC or WAV, tagged with title, creator and cover art.
- **Library** with search, sort, grid / list views, multi-select and a right-click menu.
- **Built-in player** with chapters, a mini player that keeps playing while you browse, and theater mode.
- **Clips** — mark a start and end, preview, and save as MP4 or audio.
- **Details panel** (right-click › Details… or Alt+Enter) with *Copy details* for your records.
- **Re-download** an item at a different quality without losing your clips or audio files.
- **Rename** (every related file follows), **delete to the Recycle Bin**, **show in folder**.
- **Light, dark or follow-Windows theme.**

Everything stays on your PC: one folder of normal media files you can open with any player, plus a small hidden
`.library` folder holding the details.

## Getting started

1. Download **`ReelKeep.exe`** from the [latest release](../../releases/latest). It's a single file — no installer.
2. Run it. On first start it fetches the open-source tools it needs, [yt-dlp](https://github.com/yt-dlp/yt-dlp) and
   [FFmpeg](https://ffmpeg.org), into `%LOCALAPPDATA%\ReelKeep\tools` (ffmpeg is about 140 MB, downloaded once).
3. Paste a link to your video. Files are saved to `Videos\ReelKeep`; you can change this at the bottom of the sidebar.

**Requirements:** Windows 10 or 11 (64-bit) with the Microsoft Edge WebView2 Runtime, which is already part of
Windows 11 and up-to-date Windows 10.

> Windows SmartScreen may warn about an unrecognised app the first time, because the exe isn't code-signed.
> Choose **More info › Run anyway** if you downloaded it from this repository's Releases page.

### Keyboard

| Key | Action | Key | Action |
|---|---|---|---|
| Space | Play / pause | ← / → | Back / forward 10 s |
| ↑ / ↓ | Volume | M | Mute |
| F | Theater mode | Esc | Close / back |
| `[` / `]` | Clip start / end | F2 | Rename |
| Delete | Delete selected | Ctrl+A | Select all |
| Alt+Enter | Details | Right-click | Item menu |

## Building from source

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download) (or newer).

```bat
build.bat      :: compile
run.bat        :: build and run
publish.bat    :: dist\ReelKeep.exe – one self-contained file
```

Pushing a tag such as `v1.0.1` makes the GitHub Actions workflow build the exe and attach it to a new release.

The app is a small C# (.NET 8, WinForms + WebView2) host with a plain HTML/CSS/JS interface embedded in the exe:

```
src/ReelKeep/
├─ Program.cs            entry point and error log
├─ Host/                 the window, and the bridge between the interface and the engine
├─ Core/                 library, download queue, clips, audio, renaming (yt-dlp / ffmpeg wrappers)
└─ wwwroot/              the interface (Preact + htm, no build step) and bundled fonts
```

## Responsible use

ReelKeep is meant for content you own or have the right to keep — your own uploads, material shared under an open
licence, or anything the creator and the hosting service allow you to save. Please respect creators' copyright and
each site's terms of service.

## Licence

ReelKeep is released into the **public domain** under [The Unlicense](LICENSE): use it, copy it, change it, sell it
or ship it however you like, no permission or credit needed.

The third-party components it uses keep their own (permissive) licences — see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

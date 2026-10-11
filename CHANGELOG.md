# Changelog

All notable changes to ReelKeep. Versions follow `major.minor.patch`; each one is on the
[Releases](https://github.com/Jamesking96/ReelKeep/releases) page with a ready-to-run `ReelKeep.exe`.

## 1.1.0 — 2026-10-12

### Added
- **Folders:** a *Save to folder* option for new downloads (Options, *Multiple links* and the playlist picker), with
  naming tokens such as `{channel}` and a "use the playlist name" shortcut.
- **Move to folder…** for existing items (right-click menu, selection bar and Details panel). All of an item's files move
  together; clashes are renamed "Name (2)"; empty folders are removed.
- **Folders** section in the sidebar to browse and filter the library by folder.
- **Sign-in for restricted videos** (sidebar › *Sign-in*): a browser's sign-in (Firefox, Chrome, Edge, Brave, Opera,
  Vivaldi, Chromium) or a `cookies.txt` file, passed to yt-dlp.
- **Error details** for failed downloads with **Copy error**; *Copy error* also on the link box and error dialogs.

### Changed
- Failed downloads show a short, plain-English reason (e.g. "Age-restricted – set up Sign-in, then retry") instead of the
  raw last line of yt-dlp's output, and sign-in problems link to the fix.
- The Details panel, *Copy details* and the Queue show each item's folder.

## 1.0.0 — 2026-10-02

First release: download queue (up to 6 at once), video / audio (M4A, MP3, FLAC, WAV) / both, library with search, sort,
grid and list views, built-in player with chapters and mini player, clips, re-download, rename, delete to the Recycle
Bin, Details panel, playlist picker, light / dark / follow-Windows theme.

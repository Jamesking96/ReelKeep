# Third-party notices

## Bundled in the app

| Component | Used for | License |
|---|---|---|
| [Preact](https://preactjs.com) 10 + [htm](https://github.com/developit/htm) 3 (`wwwroot/js/vendor/preact-htm.js`) | UI rendering | MIT / Apache-2.0 |
| [Barlow & Barlow Condensed](https://github.com/jpt/barlow) fonts (`wwwroot/fonts`) | Typography | SIL Open Font License 1.1 (`wwwroot/fonts/OFL-Barlow.txt`) |
| [Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2) | Hosting the UI in the Edge WebView2 runtime | Microsoft WebView2 SDK license |
| Icons | Drawn in the style of [Lucide](https://lucide.dev) (stroke 1.5) | ISC |

The look follows the *Industry* design system created in Claude Design for this project (`wwwroot/css/industry.css`).

## Downloaded at runtime (not included in the repository or the exe)

| Tool | Used for | License |
|---|---|---|
| [yt-dlp](https://github.com/yt-dlp/yt-dlp) | Reading video info and downloading | Unlicense |
| [FFmpeg](https://ffmpeg.org) (yt-dlp's [FFmpeg-Builds](https://github.com/yt-dlp/FFmpeg-Builds)) | Merging, audio conversion, clips | GPL v3 (these builds) |
| [SponsorBlock](https://sponsor.ajay.app) API | Community sponsor-segment data | CC BY-NC-SA 4.0 (data) |

ReelKeep downloads yt-dlp on first run and offers to download ffmpeg; both are stored in `%LOCALAPPDATA%\ReelKeep\tools`.

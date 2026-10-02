using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ReelKeep.Core;

public sealed record DownloadProgress(double Percent, string Speed, string Eta, string FormatId);

public sealed record DownloadResult(string VideoFile, string? AudioFile);

/// <summary>One video in a playlist/channel listing (from yt-dlp --flat-playlist, so no per-video request).</summary>
public sealed record PlaylistItem(int Index, string Id, string Title, string Url, double? Duration, string? Channel, bool Unavailable);

public sealed record PlaylistInfo(string Title, string? Channel, string SourceUrl, List<PlaylistItem> Items);

/// <summary>
/// Thin wrapper around the yt-dlp command-line tool (https://github.com/yt-dlp/yt-dlp).
/// yt-dlp does the hard part (YouTube signature/throttling changes etc.) and is updated frequently,
/// which makes it far more reliable than re-implementing YouTube's protocol in C#.
///
/// - yt-dlp.exe is looked up in the app's tools folder, then PATH, and downloaded on first run if missing.
/// - ffmpeg is optional. With it, video+audio are merged into one .mp4. Without it, the best video-only
///   and audio-only streams are saved separately and VLC plays them together (audio as an "input slave").
/// - YouTube requires a JavaScript runtime for full format access; deno is used automatically if present,
///   otherwise Node.js is enabled via --js-runtimes.
/// </summary>
public sealed class YtDlpService
{
    private const string YtDlpDownloadUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";

    public string ToolsDir { get; } = Path.Combine(AppSettings.AppDataDir, "tools");
    public string? YtDlpPath { get; private set; }
    public string? FfmpegPath { get; private set; }

    /// <summary>Point yt-dlp at an ffmpeg (e.g. one the app just downloaded).</summary>
    public void SetFfmpeg(string? path) { if (path != null) FfmpegPath = path; }
    public string? JsRuntime { get; private set; }
    public string? Version { get; private set; }

    public bool HasFfmpeg => FfmpegPath != null;

    public async Task EnsureReadyAsync(IProgress<string> status, CancellationToken ct = default)
    {
        Directory.CreateDirectory(ToolsDir);

        YtDlpPath = FirstExisting(Path.Combine(ToolsDir, "yt-dlp.exe"), Path.Combine(AppContext.BaseDirectory, "yt-dlp.exe"),
                        Path.Combine(AppSettings.LegacyToolsDir, "yt-dlp.exe"))
                    ?? FindOnPath("yt-dlp.exe");
        if (YtDlpPath == null)
        {
            status.Report("Downloading yt-dlp.exe (first run only)...");
            var target = Path.Combine(ToolsDir, "yt-dlp.exe");
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ReelKeep/1.0");
            await using (var src = await http.GetStreamAsync(YtDlpDownloadUrl, ct))
            await using (var dst = File.Create(target + ".tmp"))
                await src.CopyToAsync(dst, ct);
            File.Move(target + ".tmp", target, overwrite: true);
            YtDlpPath = target;
        }

        FfmpegPath = FirstExisting(Path.Combine(ToolsDir, "ffmpeg.exe"), Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
                         Path.Combine(AppSettings.LegacyToolsDir, "ffmpeg.exe"))
                     ?? FindOnPath("ffmpeg.exe");

        // deno is yt-dlp's default JS runtime; fall back to node if that's what is installed.
        if (FindOnPath("deno.exe") != null) JsRuntime = "deno";
        else if (FindOnPath("node.exe") != null) JsRuntime = "node";
        else JsRuntime = null;

        var (_, stdout, _) = await RunAsync(new[] { "--version" }, null, ct);
        Version = stdout.Trim();
        status.Report("Ready");
    }

    /// <summary>Update yt-dlp itself (do this when YouTube changes break downloads).</summary>
    public async Task<string> UpdateAsync(CancellationToken ct = default)
    {
        var (_, o, e) = await RunAsync(new[] { "-U" }, null, ct);
        var (_, v, _) = await RunAsync(new[] { "--version" }, null, ct);
        Version = v.Trim();
        return (o + e).Trim();
    }

    /// <summary>Fetch full metadata without downloading. Returns the parsed object and the raw JSON.</summary>
    public async Task<(VideoMetadata Meta, string RawJson)> GetMetadataAsync(string url, CancellationToken ct = default)
    {
        var args = CommonArgs().Concat(new[] { "-J", "--no-playlist", "--skip-download", url });
        var (code, stdout, stderr) = await RunAsync(args, null, ct);
        if (code != 0 || string.IsNullOrWhiteSpace(stdout))
            throw new InvalidOperationException("yt-dlp could not read this video:\n" + LastErrors(stderr));
        return (VideoMetadata.FromYtDlpJson(stdout), stdout);
    }

    /// <summary>
    /// List the videos in a playlist (or channel) without downloading anything. Uses --flat-playlist,
    /// so a 500-video playlist is one quick request instead of 500.
    /// </summary>
    public async Task<PlaylistInfo> GetPlaylistAsync(string url, CancellationToken ct = default)
    {
        var args = CommonArgs().Concat(new[] { "--flat-playlist", "--yes-playlist", "-J", url });
        var (code, stdout, stderr) = await RunAsync(args, null, ct);
        if (code != 0 || string.IsNullOrWhiteSpace(stdout))
            throw new InvalidOperationException("yt-dlp could not read this playlist:\n" + LastErrors(stderr));

        using var doc = System.Text.Json.JsonDocument.Parse(stdout);
        var r = doc.RootElement;
        string? Str(System.Text.Json.JsonElement e, string n) =>
            e.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;

        var items = new List<PlaylistItem>();
        if (r.TryGetProperty("entries", out var entries) && entries.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var e in entries.EnumerateArray())
            {
                var id = Str(e, "id");
                // Channel URLs list tabs ("Videos", "Shorts") as nested playlists - only keep real videos
                if (id is not { Length: 11 } || Str(e, "_type") is "playlist") continue;
                var title = Str(e, "title") ?? id;
                var availability = Str(e, "availability");
                var unavailable = title is "[Private video]" or "[Deleted video]" ||
                                  availability is "private" or "needs_auth" or "subscriber_only" or "premium_only";
                double? dur = e.TryGetProperty("duration", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.Number ? d.GetDouble() : null;
                var videoUrl = Str(e, "url") is { } u && u.StartsWith("http") ? u : $"https://www.youtube.com/watch?v={id}";
                items.Add(new PlaylistItem(items.Count + 1, id, title, videoUrl, dur, Str(e, "channel") ?? Str(e, "uploader"), unavailable));
            }
        }
        if (items.Count == 0) throw new InvalidOperationException("That link didn't contain any downloadable videos.");
        return new PlaylistInfo(Str(r, "title") ?? "Playlist", Str(r, "channel") ?? Str(r, "uploader"), url, items);
    }

    /// <summary>
    /// Download into <paramref name="outDir"/> using the temporary base name <paramref name="tempBase"/>
    /// (the caller renames the result to the user's chosen name afterwards).
    /// </summary>
    /// <param name="audioOnly">Only fetch the best audio stream (for MP3/FLAC/M4A/WAV export).</param>
    /// <param name="preferAac">Prefer YouTube's AAC (m4a) audio - lets an M4A export be a lossless copy.</param>
    public async Task<DownloadResult> DownloadAsync(string url, VideoMetadata meta, string outDir, string tempBase,
        bool audioOnly, bool preferAac, int maxHeight,
        IProgress<DownloadProgress> progress, IProgress<string> log, CancellationToken ct = default)
    {
        Directory.CreateDirectory(outDir);
        var h = maxHeight > 0 ? $"[height<={maxHeight}]" : "";

        var args = CommonArgs().ToList();
        args.AddRange(new[]
        {
            "--no-playlist", "--newline", "--progress", "--windows-filenames", "--no-mtime",
            "--progress-template", "download:[dl]%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s|%(info.format_id)s",
            // Print the final path of every file once post-processing is done
            "--print", "after_move:filepath",
        });

        if (audioOnly)
        {
            // Best audio only. Opus (usually best quality) unless AAC is wanted for a lossless M4A copy.
            args.AddRange(new[]
            {
                "-f", preferAac ? "ba[ext=m4a]/ba/b" : "ba/b",
                "-o", Path.Combine(outDir, tempBase + ".%(ext)s"),
            });
        }
        else if (HasFfmpeg)
        {
            // Sort: resolution first, then prefer H.264 + AAC so the .mp4 plays in any player, not just VLC.
            args.AddRange(new[]
            {
                "--ffmpeg-location", FfmpegPath!,
                "-f", $"bv*{h}+ba/b{h}/b",
                "-S", maxHeight > 0 ? $"res:{maxHeight},vcodec:h264,acodec:m4a" : "res,vcodec:h264,acodec:m4a",
                "--merge-output-format", "mp4",
                "-o", Path.Combine(outDir, tempBase + ".%(ext)s"),
            });
        }
        else
        {
            // No ffmpeg: grab video-only and audio-only as two files ("," = download both formats).
            args.AddRange(new[]
            {
                "-f", $"bv{h},ba/b",
                "-o", Path.Combine(outDir, tempBase + ".f%(format_id)s.%(ext)s"),
            });
        }
        args.Add(url);

        var files = new List<string>();
        var (code, _, stderr) = await RunAsync(args, line =>
        {
            if (line.StartsWith("[dl]"))
            {
                var p = line[4..].Split('|');
                if (p.Length >= 4 && double.TryParse(p[0].Trim().TrimEnd('%'), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var pct))
                    progress.Report(new DownloadProgress(pct, p[1].Trim(), p[2].Trim(), p[3].Trim()));
            }
            else if (File.Exists(line.Trim()))
            {
                files.Add(line.Trim());
            }
            else if (!string.IsNullOrWhiteSpace(line))
            {
                log.Report(line);
            }
        }, ct);

        if (code != 0 || files.Count == 0)
        {
            // Fallback: look for anything named after the id (e.g. when --print output was swallowed)
            files = Directory.EnumerateFiles(outDir, tempBase + ".*")
                .Where(f => !f.EndsWith(".json") && !f.EndsWith(".jpg") && !f.EndsWith(".part") && !f.EndsWith(".ytdl"))
                .ToList();
            if (code != 0 || files.Count == 0)
                throw new InvalidOperationException("Download failed:\n" + LastErrors(stderr));
        }

        if (files.Count == 1) return new DownloadResult(files[0], null);

        // Two DASH files: work out which is video and which is audio from the format id in the name.
        string? video = null, audio = null;
        foreach (var f in files)
        {
            var m = Regex.Match(Path.GetFileName(f), @"\.f([^.]+)\.[^.]+$");
            var isVideo = m.Success && meta.FormatHasVideo.TryGetValue(m.Groups[1].Value, out var hv) ? hv : f.EndsWith(".mp4");
            if (isVideo) video ??= f; else audio ??= f;
        }
        return new DownloadResult(video ?? files[0], audio);
    }

    private IEnumerable<string> CommonArgs()
    {
        if (JsRuntime == "node") { yield return "--js-runtimes"; yield return "node"; }
        yield return "--no-warnings";
    }

    /// <summary>Run yt-dlp, streaming stdout lines to <paramref name="onLine"/>. Kills the process tree on cancel.</summary>
    private async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(IEnumerable<string> args, Action<string>? onLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(YtDlpPath ?? throw new InvalidOperationException("yt-dlp not initialised"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data == null) return; lock (stdout) stdout.AppendLine(e.Data); onLine?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data == null) return; lock (stderr) stderr.AppendLine(e.Data); };

        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }
        p.WaitForExit(); // flush async readers
        return (p.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static string LastErrors(string stderr)
    {
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var errors = lines.Where(l => l.StartsWith("ERROR")).ToList();
        return string.Join("\n", (errors.Count > 0 ? errors : lines.TakeLast(5)));
    }

    private static string? FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);

    private static string? FindOnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(full)) return full;
            }
            catch { /* bad PATH entry */ }
        }
        return null;
    }

    /// <summary>Links that are a list of videos rather than one video: /playlist?list=, channel pages.</summary>
    public static bool IsListUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        var path = u.AbsolutePath.ToLowerInvariant();
        if (path.StartsWith("/playlist")) return true;
        if (path.StartsWith("/@") || path.StartsWith("/channel/") || path.StartsWith("/c/") || path.StartsWith("/user/")) return true;
        return false;
    }

    /// <summary>A single-video link that also carries a playlist (watch?v=...&amp;list=...).</summary>
    public static bool IsVideoInPlaylist(string url) =>
        TryGetVideoId(url) != null && GetListId(url) != null;

    public static string? GetListId(string url)
    {
        var m = Regex.Match(url, @"[?&]list=([A-Za-z0-9_-]+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Pull every link out of pasted text: one per line, or separated by spaces/commas/semicolons.
    /// Bare 11-character video ids are accepted too. Duplicates are removed, order kept.
    /// </summary>
    public static List<string> ParseLinks(string text)
    {
        var result = new List<string>();
        foreach (var raw in Regex.Split(text ?? "", @"[\s,;]+"))
        {
            var t = raw.Trim().Trim('"', '\'', '<', '>', '(', ')');
            if (t.Length == 0) continue;
            if (!t.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                if (Regex.IsMatch(t, @"^[A-Za-z0-9_-]{11}$")) t = "https://www.youtube.com/watch?v=" + t;
                else if (t.Contains("youtu", StringComparison.OrdinalIgnoreCase)) t = "https://" + t;
                else continue;
            }
            if (!result.Contains(t, StringComparer.OrdinalIgnoreCase)) result.Add(t);
        }
        return result;
    }

    /// <summary>Extract the 11-character video id from common YouTube URL shapes.</summary>
    public static string? TryGetVideoId(string url)
    {
        var m = Regex.Match(url, @"(?:v=|youtu\.be/|shorts/|embed/|live/)([A-Za-z0-9_-]{11})");
        return m.Success ? m.Groups[1].Value : null;
    }
}

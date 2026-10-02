using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using ReelKeep.Core;

namespace ReelKeep.Host;

/// <summary>
/// JSON messaging between the web UI and the <see cref="Engine"/>.
///  UI → host:  { "id": 7, "cmd": "rename", "args": { ... } }
///  host → UI:  { "reply": 7, "ok": true, "result": ... }  or  { "reply": 7, "ok": false, "error": "..." }
///  host → UI:  { "evt": "state" | "job" | "toast" | "play" | "task" | "release", "data": ... }
/// </summary>
public sealed class Bridge
{
    public const string MediaHost = "media.reelkeep";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Engine _engine;
    private readonly CoreWebView2 _web;
    private readonly Form _owner;
    private readonly System.Windows.Forms.Timer _stateTimer = new() { Interval = 60 };
    private readonly Dictionary<int, DateTime> _lastJobPush = new();
    private readonly Dictionary<string, TaskCompletionSource> _releases = new();
    private bool _stateDirty;

    public Bridge(Engine engine, CoreWebView2 web, Form owner)
    {
        _engine = engine;
        _web = web;
        _owner = owner;

        // Coalesce bursts of changes into one snapshot
        _stateTimer.Tick += (_, _) => { _stateTimer.Stop(); if (_stateDirty) { _stateDirty = false; Post("state", BuildState()); } };
        engine.Changed += () => { _stateDirty = true; _stateTimer.Stop(); _stateTimer.Start(); };
        engine.JobProgress += job =>
        {
            var now = DateTime.UtcNow;
            if (_lastJobPush.TryGetValue(job.Id, out var last) && (now - last).TotalMilliseconds < 150) return;
            _lastJobPush[job.Id] = now;
            Post("job", JobDto(job));
        };
        engine.Toast += msg => Post("toast", msg);
        engine.PlayRequested += id => Post("play", id);
        engine.TaskProgress += (label, pct) => Post("task", label == null ? null : new { label, pct });
        engine.ReleaseMediaAsync = ReleaseMediaAsync;

        web.WebMessageReceived += async (_, e) => await HandleAsync(e.WebMessageAsJson);
    }

    private void Post(string evt, object? data)
    {
        try { _web.PostWebMessageAsJson(JsonSerializer.Serialize(new { evt, data }, Json)); }
        catch (Exception ex) { Debug.WriteLine("Post failed: " + ex.Message); }
    }

    /// <summary>Ask the UI to stop playing an item's files (so they can be renamed / replaced / deleted).</summary>
    private async Task ReleaseMediaAsync(string id)
    {
        var token = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _releases[token] = tcs;
        Post("release", new { id, token });
        await Task.WhenAny(tcs.Task, Task.Delay(2500));
        _releases.Remove(token);
        await Task.Delay(150); // let the media pipeline close its file handles
    }

    // =========================================================================================
    // Commands
    // =========================================================================================
    private async Task HandleAsync(string json)
    {
        int id = 0;
        try
        {
            var msg = JsonNode.Parse(json)!.AsObject();
            id = msg["id"]?.GetValue<int>() ?? 0;
            var cmd = msg["cmd"]?.GetValue<string>() ?? "";
            var a = msg["args"] as JsonObject ?? new JsonObject();
            var result = await ExecuteAsync(cmd, a);
            Reply(id, true, result, null);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            Reply(id, false, null, ex is OperationCanceledException ? "Cancelled." : ex.Message);
        }
    }

    private void Reply(int id, bool ok, object? result, string? error)
    {
        if (id == 0) return;
        try { _web.PostWebMessageAsJson(JsonSerializer.Serialize(new { reply = id, ok, result, error }, Json)); }
        catch (Exception ex) { Debug.WriteLine("Reply failed: " + ex.Message); }
    }

    private static string S(JsonObject a, string key) => a[key]?.GetValue<string>() ?? "";
    private static string? SN(JsonObject a, string key) => a[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static bool B(JsonObject a, string key, bool def = false) => a[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : def;
    private static double D(JsonObject a, string key) => a[key]?.GetValue<double>() ?? 0;
    private static List<string> L(JsonObject a, string key) =>
        (a[key] as JsonArray)?.Select(n => n!.GetValue<string>()).ToList() ?? new List<string>();

    private DownloadOptions OptionsFrom(JsonObject a) => new(
        SaveAsText.Parse(SN(a, "saveAs") ?? _engine.Settings.SaveAs),
        AudioFormats.Parse(SN(a, "audioFmt") ?? _engine.Settings.AudioFormat),
        DownloadOptions.ParseQuality(SN(a, "quality") ?? _engine.Settings.Quality));

    private async Task<object?> ExecuteAsync(string cmd, JsonObject a)
    {
        switch (cmd)
        {
            case "init":
                return BuildState();

            case "released":
                if (_releases.TryGetValue(S(a, "token"), out var tcs)) tcs.TrySetResult();
                return null;

            // ---- looking things up ----
            case "fetchInfo":
            {
                var (meta, _) = await _engine.FetchInfoAsync(S(a, "url"));
                return new
                {
                    id = meta.Id, title = meta.Title, channel = meta.Channel ?? meta.Uploader ?? "", len = meta.DurationSeconds ?? 0,
                    date = meta.UploadDate?.ToString("yyyy-MM-dd") ?? "", thumb = meta.ThumbnailUrl,
                    name = _engine.ApplyPattern(meta, SN(a, "pattern")),
                    inLibrary = _engine.Library.Any(e => e.Metadata.Id == meta.Id),
                };
            }
            case "applyPattern":
            {
                var (meta, _) = await _engine.FetchInfoAsync(S(a, "url"));
                return _engine.ApplyPattern(meta, S(a, "pattern"));
            }
            case "playlist":
            {
                var pl = await _engine.GetPlaylistAsync(S(a, "url"));
                var inLib = _engine.Library.Select(e => e.Metadata.Id).ToHashSet();
                var digits = Math.Max(2, pl.Items.Count.ToString().Length);
                return new
                {
                    title = pl.Title, channel = pl.Channel ?? "",
                    items = pl.Items.Select(p => new
                    {
                        n = p.Index.ToString("D" + digits), id = p.Id, title = p.Title, len = p.Duration ?? 0, url = p.Url,
                        note = p.Unavailable ? "Private" : inLib.Contains(p.Id) ? "In library" : "",
                    }),
                };
            }

            // ---- queue ----
            case "enqueue":
            {
                var options = OptionsFrom(a);
                var playAfter = B(a, "playAfter");
                var jobs = new List<QueueJob>();
                foreach (var n in (a["items"] as JsonArray) ?? new JsonArray())
                {
                    if (n is not JsonObject it) continue;
                    var url = S(it, "url");
                    if (url.Length == 0) continue;
                    jobs.Add(new QueueJob
                    {
                        Url = url, VideoId = SN(it, "id") ?? YtDlpService.TryGetVideoId(url), Title = SN(it, "title") ?? url,
                        NamePrefix = SN(it, "prefix"), NameOverride = SN(it, "name"), Options = options, PlayAfter = playAfter,
                    });
                }
                var before = _engine.Queue.Count;
                _engine.Enqueue(jobs);
                return _engine.Queue.Count - before;
            }
            case "queuePause": _engine.PauseQueue(); return null;
            case "queueResume": _engine.ResumeQueue(); return null;
            case "queueRetry": _engine.RetryJobs(a["id"]?.GetValue<int>()); return null;
            case "queueRemove": _engine.RemoveJob(a["id"]!.GetValue<int>()); return null;
            case "queueClear": _engine.ClearFinished(); return null;

            // ---- library operations ----
            case "rename": await _engine.RenameEntryAsync(S(a, "id"), S(a, "name")); return null;
            case "renameFile": await _engine.RenameFileAsync(S(a, "id"), S(a, "file"), S(a, "name")); return null;
            case "delete": return await _engine.DeleteAsync(L(a, "ids"));
            case "saveAudio": return await _engine.SaveAudioAsync(L(a, "ids"), AudioFormats.Parse(SN(a, "fmt") ?? _engine.Settings.AudioFormat));
            case "redownload":
            {
                var keep = B(a, "keep", true);
                var options = B(a, "same", true) ? null : OptionsFrom(a);
                return _engine.QueueRedownloads(L(a, "ids"), options, keep);
            }
            case "saveClip":
            {
                var kind = S(a, "kind") switch { "fast" => ClipKind.FastCopy, "audio" => ClipKind.Audio, _ => ClipKind.PreciseMp4 };
                var f = await _engine.SaveClipAsync(S(a, "id"), D(a, "start"), D(a, "end"), kind, SN(a, "name"));
                return f.FileName;
            }
            case "rawJson": return _engine.GetRawJson(S(a, "id"));

            // ---- shell ----
            case "showInFolder":
            {
                var e = _engine.Get(S(a, "id"));
                var file = SN(a, "file") is { } fn ? Path.Combine(e.Root, fn) : e.MainPath;
                if (file != null && File.Exists(file)) Process.Start("explorer.exe", $"/select,\"{file}\"");
                else Process.Start("explorer.exe", $"\"{e.Root}\"");
                return null;
            }
            case "openLibraryFolder":
                Directory.CreateDirectory(_engine.Settings.LibraryFolder);
                Process.Start("explorer.exe", $"\"{_engine.Settings.LibraryFolder}\"");
                return null;
            case "openUrl":
            {
                var url = S(a, "url");
                if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "https" or "http")
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return null;
            }
            case "changeFolder":
            {
                using var dlg = new FolderBrowserDialog
                {
                    Description = "Choose the folder ReelKeep saves downloads in", UseDescriptionForTitle = true,
                    SelectedPath = _engine.Settings.LibraryFolder, ShowNewFolderButton = true,
                };
                if (dlg.ShowDialog(_owner) != DialogResult.OK) return null;
                _engine.ChangeLibraryFolder(dlg.SelectedPath);
                MapMediaFolder(_web, dlg.SelectedPath);
                return dlg.SelectedPath;
            }

            // ---- tools & settings ----
            case "updateYtdlp": return await _engine.UpdateYtDlpAsync();
            case "installFfmpeg": await _engine.InstallFfmpegAsync(); return null;
            case "settings": ApplySettings(a); return null;

            default:
                throw new InvalidOperationException("Unknown command: " + cmd);
        }
    }

    private void ApplySettings(JsonObject a)
    {
        var s = _engine.Settings;
        if (SN(a, "saveAs") is { } sa) s.SaveAs = SaveAsText.Key(SaveAsText.Parse(sa));
        if (SN(a, "quality") is { } q) s.Quality = q;
        if (SN(a, "audioFmt") is { } f) s.AudioFormat = AudioFormats.Parse(f).ToString();
        if (SN(a, "pattern") is { } p) s.NamePattern = p;
        if (SN(a, "viewMode") is { } vm) s.ViewMode = vm;
        if (SN(a, "sort") is { } so) s.Sort = so;
        if (SN(a, "clipKind") is { } ck) s.ClipKind = ck;
        if (SN(a, "theme") is { } th && th is "system" or "light" or "dark")
        {
            s.Theme = th;
            if (_owner is MainWindow w) w.BeginInvoke(w.ApplyTheme);
        }
        if (a["autoSkip"] is JsonValue) s.AutoSkipSponsors = B(a, "autoSkip");
        if (a["numberPlaylist"] is JsonValue) s.NumberPlaylistFiles = B(a, "numberPlaylist");
        if (a["keepExtras"] is JsonValue) s.KeepExtrasOnRedownload = B(a, "keepExtras");
        if (a["volume"] is JsonValue) s.Volume = (int)Math.Clamp(D(a, "volume"), 0, 100);
        if (a["parallel"] is JsonValue) _engine.SetParallelDownloads((int)D(a, "parallel"));
        _engine.SaveSettings();
    }

    public static void MapMediaFolder(CoreWebView2 web, string folder)
    {
        Directory.CreateDirectory(folder);
        try { web.ClearVirtualHostNameToFolderMapping(MediaHost); } catch { /* not mapped yet */ }
        web.SetVirtualHostNameToFolderMapping(MediaHost, folder, CoreWebView2HostResourceAccessKind.Allow);
    }

    // =========================================================================================
    // Snapshot for the UI
    // =========================================================================================
    private object BuildState()
    {
        var s = _engine.Settings;
        return new
        {
            library = _engine.Library.Select(EntryDto).ToList(),
            queue = _engine.Queue.Select(JobDto).ToList(),
            queuePaused = _engine.QueuePaused,
            tools = new
            {
                ready = _engine.ToolsReady, error = _engine.ToolsError, ytdlp = _engine.YtDlp.Version,
                ffmpeg = _engine.Ffmpeg.IsAvailable, js = _engine.YtDlp.JsRuntime,
            },
            settings = new
            {
                libraryFolder = s.LibraryFolder, libraryShort = ShortPath(s.LibraryFolder),
                saveAs = s.SaveAs, quality = s.Quality, audioFmt = s.AudioFormat, pattern = s.NamePattern,
                numberPlaylist = s.NumberPlaylistFiles, keepExtras = s.KeepExtrasOnRedownload, autoSkip = s.AutoSkipSponsors,
                volume = s.Volume, viewMode = s.ViewMode, sort = s.Sort, clipKind = s.ClipKind, theme = s.Theme,
                parallel = _engine.MaxParallel, parallelMax = Engine.MaxParallelLimit,
            },
        };
    }

    private static string ShortPath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? path[(home.Length + 1)..] : path;
    }

    private static object JobDto(QueueJob j) => new
    {
        id = j.Id, title = j.Title, opts = j.Target != null ? "Re-download · " + j.Options.Describe() : j.Options.Describe(),
        state = j.State switch
        {
            JobState.Working => "dl", JobState.Done => "done", JobState.Skipped => "skip", JobState.Failed => "fail", _ => "wait",
        },
        pct = Math.Round(j.Percent, 1), detail = j.Detail, resultId = j.ResultId, redl = j.Target != null,
    };

    private object EntryDto(LibraryEntry e)
    {
        var m = e.Metadata;
        var hasExport = e.Files.Any(f => f.Role == FileRole.AudioExport);
        var exportFmt = e.Files.FirstOrDefault(f => f.Role == FileRole.AudioExport)?.Format;
        long size = 0;
        foreach (var f in e.Files.Where(f => f.Role is FileRole.Main or FileRole.SeparateAudio))
            try { size += new FileInfo(e.FullPath(f)).Length; } catch { /* gone */ }

        var thumbFile = e.Files.FirstOrDefault(f => f.Role == FileRole.Thumbnail);
        var thumb = thumbFile != null ? MediaUrl(e, thumbFile.FileName)
            : File.Exists(e.ThumbnailPath) ? MediaUrl(e, LibraryEntry.SidecarFolderName + "/" + Path.GetFileName(e.ThumbnailPath)) : null;

        string quality;
        if (e.IsAudioOnly) quality = "—";
        else if (e.VideoHeight is { } h) quality = $"{h}p" + (e.VideoFps is { } fps && fps > 0 ? $" · {Math.Round(fps)}" : "");
        else quality = e.DownloadMaxHeight is > 0 ? $"≤ {e.DownloadMaxHeight}p" : "Best";

        return new
        {
            id = m.Id, name = e.Name, title = m.Title, channel = m.Channel ?? m.Uploader ?? "", len = m.DurationSeconds ?? 0,
            type = e.IsAudioOnly ? "Audio" : hasExport ? "V + A" : "Video",
            audio = e.IsAudioOnly || hasExport,
            saved = e.DownloadedAt.ToString("yyyy-MM-dd"), savedTs = new DateTimeOffset(e.DownloadedAt).ToUnixTimeMilliseconds(),
            isNew = _engine.NewIds.Contains(m.Id),
            thumb, src = e.MainPath is { } mp && File.Exists(mp) ? MediaUrl(e, e.Main!.FileName) : null,
            audioSrc = e.SeparateAudio is { } sa ? MediaUrl(e, sa.FileName) : null,
            url = m.WebpageUrl, quality,
            audioLabel = e.IsAudioOnly ? (e.Main?.Format ?? "Audio") : hasExport ? $"{exportFmt} file" : "In video",
            size, width = e.VideoWidth, height = e.VideoHeight,
            chapters = m.Chapters.Select(c => new { s = c.StartSeconds, e = c.EndSeconds, name = c.Title }),
            sponsors = e.SponsorSegments.Select(x => new { s = x.Start, e = x.End, cat = x.Category }),
            desc = m.Description ?? "",
            views = m.ViewCount, uploaded = m.UploadDate?.ToString("yyyy-MM-dd"),
            files = e.Files.OrderBy(f => f.Role).ThenBy(f => f.CreatedAt).Select(f => FileDto(e, f)),
        };
    }

    private static object FileDto(LibraryEntry e, EntryFile f)
    {
        long size = 0;
        try { size = new FileInfo(e.FullPath(f)).Length; } catch { /* gone */ }
        return new
        {
            name = f.FileName, role = f.Role.ToString().ToLowerInvariant(), fmt = f.Format, size,
            what = f.Role switch
            {
                FileRole.Main => e.IsAudioOnly ? $"Audio · {f.Format}" : "Video" + (e.VideoHeight is { } h ? $" · {h}p" : ""),
                FileRole.SeparateAudio => "Audio track (plays with the video)",
                FileRole.AudioExport => $"Audio file · {f.Format}",
                FileRole.Clip => $"Clip · {TimeFmt.Short(TimeSpan.FromSeconds(f.ClipStart ?? 0))}–{TimeFmt.Short(TimeSpan.FromSeconds(f.ClipEnd ?? 0))} · {f.Format}",
                FileRole.Thumbnail => "Thumbnail",
                _ => "",
            },
            clipS = f.ClipStart, clipE = f.ClipEnd, renamable = f.Role is FileRole.AudioExport or FileRole.Clip,
            src = MediaUrl(e, f.FileName), created = new DateTimeOffset(f.CreatedAt).ToUnixTimeMilliseconds(),
        };
    }

    /// <summary>URL of a library file on the media host; the ?v= stamp changes when the file does (re-downloads).</summary>
    private static string MediaUrl(LibraryEntry e, string relativePath)
    {
        long stamp = 0;
        try { stamp = File.GetLastWriteTimeUtc(Path.Combine(e.Root, relativePath)).Ticks / TimeSpan.TicksPerSecond; } catch { /* ignore */ }
        var path = string.Join("/", relativePath.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
        return $"https://{MediaHost}/{path}?v={stamp}";
    }
}

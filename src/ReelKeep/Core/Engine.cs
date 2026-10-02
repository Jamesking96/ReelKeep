using System.Diagnostics;

namespace ReelKeep.Core;

/// <summary>What a download saves.</summary>
public enum SaveAs { Video, Audio, VideoAndAudio }

public static class SaveAsText
{
    public static SaveAs Parse(string? s) => s?.ToLowerInvariant() switch
    {
        "audio" => SaveAs.Audio,
        "video" => SaveAs.Video,
        _ => SaveAs.VideoAndAudio,
    };

    public static string Key(SaveAs s) => s switch { SaveAs.Audio => "audio", SaveAs.Video => "video", _ => "va" };

    public static string Label(SaveAs s) => s switch { SaveAs.Audio => "Audio only", SaveAs.Video => "Video", _ => "Video + audio" };
}

/// <summary>Settings a download is made with (captured when it's queued).</summary>
public sealed record DownloadOptions(SaveAs Mode, AudioFormat Format, int MaxHeight)
{
    public string Describe() =>
        SaveAsText.Label(Mode)
        + (Mode != SaveAs.Audio ? " · " + (MaxHeight > 0 ? MaxHeight + "p" : "Best") : "")
        + (Mode != SaveAs.Video ? " · " + Format : "");

    public static int ParseQuality(string? q) =>
        q != null && q.EndsWith('p') && int.TryParse(q[..^1], out var h) ? h : 0;
}

public enum JobState { Waiting, Working, Done, Skipped, Failed }

/// <summary>One item in the download queue: a new download, or a re-download of a library item.</summary>
public sealed class QueueJob
{
    private static int _next;
    public int Id { get; } = Interlocked.Increment(ref _next);
    public required string Url { get; init; }
    public string? VideoId { get; set; }
    public string Title { get; set; } = "";
    public string? NamePrefix { get; init; }
    /// <summary>Exact file name chosen in the options panel (single downloads).</summary>
    public string? NameOverride { get; init; }
    public required DownloadOptions Options { get; init; }
    public bool PlayAfter { get; init; }
    /// <summary>Set for a re-download: the item to download again and replace.</summary>
    public LibraryEntry? Target { get; init; }
    public bool KeepExtras { get; init; } = true;

    public JobState State { get; set; }
    public double Percent { get; set; }
    public string Detail { get; set; } = "";
    public string? ResultId { get; set; }
    internal CancellationTokenSource? Cts { get; set; }
}

/// <summary>A clip cut from a library item.</summary>
public enum ClipKind { PreciseMp4, FastCopy, Audio }

/// <summary>
/// The app's engine: the library on disk, the download queue, and every file operation (download, re-download,
/// save audio, clip, rename, delete). It has no UI; the host listens to its events and calls its methods.
/// All members are meant to be used from the UI thread (async work awaits without blocking it).
/// </summary>
public sealed class Engine
{
    public AppSettings Settings { get; } = AppSettings.Load();
    public YtDlpService YtDlp { get; } = new();
    public FfmpegService Ffmpeg { get; } = new();
    public List<LibraryEntry> Library { get; } = new();
    public List<QueueJob> Queue { get; } = new();
    public bool QueuePaused { get; private set; }
    public bool ToolsReady { get; private set; }
    public string? ToolsError { get; private set; }

    /// <summary>Items downloaded this session (shown with a NEW badge).</summary>
    public HashSet<string> NewIds { get; } = new();

    /// <summary>Library, queue, settings or tools changed - the UI should refresh its snapshot.</summary>
    public event Action? Changed;
    /// <summary>Progress of one queue job (frequent).</summary>
    public event Action<QueueJob>? JobProgress;
    public event Action<string>? Toast;
    /// <summary>A "Download &amp; play" download finished: play this item.</summary>
    public event Action<string>? PlayRequested;
    /// <summary>Progress of a non-queue task (save audio, clip, ffmpeg install). Null label = finished.</summary>
    public event Action<string?, double>? TaskProgress;

    /// <summary>Set by the host: stop playing an item's files so Windows lets us rename, replace or delete them.</summary>
    public Func<string, Task>? ReleaseMediaAsync { get; set; }

    private readonly Dictionary<string, (VideoMetadata Meta, string Raw, DateTime At)> _infoCache = new();
    private readonly HashSet<string> _busy = new();
    private readonly SemaphoreSlim _taskLane = new(1, 1);
    /// <summary>Downloads running right now (at most <see cref="MaxParallel"/>).</summary>
    private readonly List<QueueJob> _active = new();
    private DateTime _nextStartAt = DateTime.MinValue;
    private bool _ffmpegWarned;

    /// <summary>Gap between starting downloads, so a burst of new jobs doesn't hit the site all at once.</summary>
    private static readonly TimeSpan StartStagger = TimeSpan.FromSeconds(1.5);
    public const int MaxParallelLimit = 6;

    /// <summary>How many downloads may run at the same time (the "Simultaneous downloads" setting).</summary>
    public int MaxParallel => Math.Clamp(Settings.ParallelDownloads, 1, MaxParallelLimit);

    public int ActiveDownloads => _active.Count;

    // =========================================================================================
    // Startup & tools
    // =========================================================================================
    public async Task InitAsync(IProgress<string> status)
    {
        LoadLibrary();
        Changed?.Invoke();
        try
        {
            Ffmpeg.Locate();
            await YtDlp.EnsureReadyAsync(status);
            YtDlp.SetFfmpeg(Ffmpeg.FfmpegPath);
            ToolsReady = true;
            ToolsError = null;
        }
        catch (Exception ex)
        {
            ToolsError = "Couldn't prepare yt-dlp: " + ex.Message;
        }
        Changed?.Invoke();
        Pump();
        _ = BackfillThumbnailsAsync();
    }

    /// <summary>Download ffmpeg (needed to merge video+audio, save audio and cut clips).</summary>
    public async Task InstallFfmpegAsync(CancellationToken ct = default)
    {
        if (Ffmpeg.IsAvailable) return;
        try
        {
            await Ffmpeg.DownloadAsync(new Progress<(double Percent, string Text)>(p => TaskProgress?.Invoke(p.Text, p.Percent)), ct);
            YtDlp.SetFfmpeg(Ffmpeg.FfmpegPath);
        }
        finally { TaskProgress?.Invoke(null, 0); Changed?.Invoke(); }
        Toast?.Invoke("ffmpeg installed");
        _ffmpegWarned = false;
        Pump();
    }

    public async Task<string> UpdateYtDlpAsync()
    {
        if (!ToolsReady) throw new InvalidOperationException("yt-dlp isn't ready yet.");
        if (_active.Count > 0) throw new InvalidOperationException("Wait for the current downloads to finish (or pause the queue) before updating yt-dlp.");
        var output = await YtDlp.UpdateAsync();
        Changed?.Invoke();
        var last = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "Done";
        return last;
    }

    // =========================================================================================
    // Library
    // =========================================================================================
    public void LoadLibrary()
    {
        Library.Clear();
        var root = Settings.LibraryFolder;
        if (Directory.Exists(root))
        {
            var metas = Directory.EnumerateFiles(root, "*.meta.json").ToList(); // oldest layout (migrated on load)
            var side = Path.Combine(root, LibraryEntry.SidecarFolderName);
            if (Directory.Exists(side)) metas.AddRange(Directory.EnumerateFiles(side, "*.meta.json"));
            foreach (var f in metas)
            {
                if (LibraryEntry.Load(f, root) is not { } e || Library.Any(x => x.Metadata.Id == e.Metadata.Id)) continue;
                if (e.Files.Any(x => x.FixedName == null && x.FileName != LibraryOps.TargetFileName(e, x)))
                    try { LibraryOps.Rename(e, e.Name); } catch (Exception ex) { Debug.WriteLine("Rename on load failed: " + ex.Message); }
                try { if (LibraryOps.AddThumbnailCopy(e)) e.Save(); } catch (Exception ex) { Debug.WriteLine("Thumbnail copy failed: " + ex.Message); }
                Library.Add(e);
            }
        }
        Library.Sort((a, b) => b.DownloadedAt.CompareTo(a.DownloadedAt));
    }

    public void ChangeLibraryFolder(string folder)
    {
        if (_active.Count > 0) throw new InvalidOperationException("Wait for the current downloads to finish (or pause the queue) before changing the library folder.");
        Directory.CreateDirectory(folder);
        Settings.LibraryFolder = folder;
        Settings.Save();
        NewIds.Clear();
        LoadLibrary();
        Changed?.Invoke();
        _ = BackfillThumbnailsAsync();
    }

    public LibraryEntry Get(string id) =>
        Library.FirstOrDefault(e => e.Metadata.Id == id) ?? throw new InvalidOperationException("That item is no longer in the library.");

    public string? GetRawJson(string id)
    {
        var e = Get(id);
        return File.Exists(e.InfoFile) ? File.ReadAllText(e.InfoFile) : null;
    }

    /// <summary>Record the real resolution / frame rate of an item's video.</summary>
    private async Task ProbeAsync(LibraryEntry e)
    {
        if (e.IsAudioOnly || e.MainPath is not { } p || !File.Exists(p)) return;
        try
        {
            if (await Ffmpeg.ProbeVideoAsync(p) is { } v)
            { e.VideoWidth = v.Width; e.VideoHeight = v.Height; e.VideoFps = Math.Round(v.Fps, 2); }
        }
        catch { /* not important */ }
    }

    private async Task BackfillThumbnailsAsync()
    {
        // Resolution of items downloaded before it was recorded
        if (Ffmpeg.FfprobePath != null)
            foreach (var e in Library.Where(e => !e.IsAudioOnly && e.VideoHeight == null).ToList())
            {
                await ProbeAsync(e);
                if (e.VideoHeight != null) try { e.Save(); } catch { /* ignore */ }
            }

        foreach (var e in Library.Where(e => !File.Exists(e.ThumbnailPath) && e.Metadata.ThumbnailUrl != null).ToList())
        {
            Directory.CreateDirectory(e.SidecarDir);
            await TryDownloadThumbnailAsync(e.Metadata, e.ThumbnailPath, CancellationToken.None);
            try { if (LibraryOps.AddThumbnailCopy(e)) e.Save(); } catch { /* ignore */ }
        }
        Changed?.Invoke();
    }

    // =========================================================================================
    // Looking things up
    // =========================================================================================
    public async Task<(VideoMetadata Meta, string Raw)> FetchInfoAsync(string url, CancellationToken ct = default)
    {
        RequireTools();
        if (_infoCache.TryGetValue(url, out var c) && DateTime.Now - c.At < TimeSpan.FromMinutes(20)) return (c.Meta, c.Raw);
        var (meta, raw) = await YtDlp.GetMetadataAsync(url, ct);
        _infoCache[url] = (meta, raw, DateTime.Now);
        return (meta, raw);
    }

    public Task<PlaylistInfo> GetPlaylistAsync(string url, CancellationToken ct = default)
    {
        RequireTools();
        return YtDlp.GetPlaylistAsync(url, ct);
    }

    public string ApplyPattern(VideoMetadata meta, string? pattern = null) => LibraryOps.ApplyPattern(pattern ?? Settings.NamePattern, meta);

    private void RequireTools()
    {
        if (!ToolsReady) throw new InvalidOperationException(ToolsError ?? "Still preparing yt-dlp – try again in a moment.");
    }

    // =========================================================================================
    // Queue
    // =========================================================================================
    public void Enqueue(IEnumerable<QueueJob> jobs)
    {
        var added = 0;
        foreach (var j in jobs)
        {
            // Don't queue the same thing twice while it's still pending
            if (Queue.Any(q => q.State is JobState.Waiting or JobState.Working &&
                               (j.Target != null ? q.Target == j.Target : q.Target == null && SameVideo(q, j)))) continue;
            Queue.Add(j);
            added++;
        }
        Changed?.Invoke();
        if (added > 0) Pump();
    }

    private static bool SameVideo(QueueJob a, QueueJob b) =>
        (a.VideoId != null && a.VideoId == b.VideoId) || string.Equals(a.Url, b.Url, StringComparison.OrdinalIgnoreCase);

    public void PauseQueue()
    {
        QueuePaused = true;
        foreach (var job in _active.ToList()) job.Cts?.Cancel();
        Changed?.Invoke();
    }

    public void ResumeQueue()
    {
        QueuePaused = false;
        Changed?.Invoke();
        Pump();
    }

    public void RetryJobs(int? id = null)
    {
        foreach (var j in Queue.Where(j => j.State == JobState.Failed && (id == null || j.Id == id)))
        { j.State = JobState.Waiting; j.Detail = ""; j.Percent = 0; }
        Changed?.Invoke();
        Pump();
    }

    public void RemoveJob(int id)
    {
        if (Queue.FirstOrDefault(j => j.Id == id) is not { } job) return;
        if (job.State == JobState.Working) { job.Cts?.Cancel(); _removeAfterCancel.Add(job.Id); return; }
        Queue.Remove(job);
        Changed?.Invoke();
    }
    private readonly HashSet<int> _removeAfterCancel = new();

    public void ClearFinished()
    {
        Queue.RemoveAll(j => j.State is JobState.Done or JobState.Skipped);
        Changed?.Invoke();
    }

    /// <summary>The simultaneous-downloads setting changed: start more jobs now if there's room. Lowering it
    /// lets running downloads finish; new ones only start once fewer than the limit are running.</summary>
    public void SetParallelDownloads(int n)
    {
        Settings.ParallelDownloads = Math.Clamp(n, 1, MaxParallelLimit);
        Settings.Save();
        Changed?.Invoke();
        Pump();
    }

    /// <summary>
    /// Start waiting jobs until <see cref="MaxParallel"/> are running. Called whenever something changes (a job is
    /// added, finishes, is retried, the queue is resumed, the limit goes up). Everything here runs on the UI thread,
    /// so picking a job and marking it as working can't race with another call.
    /// </summary>
    private void Pump()
    {
        if (!ToolsReady || QueuePaused) return;
        while (_active.Count < MaxParallel && NextRunnable() is { } job)
        {
            if (!Ffmpeg.IsAvailable)
            {
                // Every download needs ffmpeg (to merge video + audio into one playable file)
                if (!_ffmpegWarned) Toast?.Invoke("ffmpeg is needed before downloading – install it from the setup prompt.");
                _ffmpegWarned = true;
                return;
            }

            // Space out the starts a little
            var now = DateTime.UtcNow;
            var startAt = _nextStartAt > now ? _nextStartAt : now;
            _nextStartAt = startAt + StartStagger;

            _active.Add(job);
            job.Cts = new CancellationTokenSource();
            job.State = JobState.Working;
            job.Percent = 0;
            job.Detail = "Starting…";
            _ = RunJobAsync(job, startAt - now);
        }
        Changed?.Invoke();
    }

    /// <summary>The first waiting job that doesn't touch an item that's already busy (being downloaded,
    /// re-downloaded or converted) – those wait their turn instead of failing.</summary>
    private QueueJob? NextRunnable() => Queue.FirstOrDefault(j =>
        j.State == JobState.Waiting &&
        !(j.Target != null && _busy.Contains(j.Target.Metadata.Id)) &&
        !(j.VideoId != null && (_busy.Contains(j.VideoId) || _active.Any(a => a.VideoId == j.VideoId))));

    private async Task RunJobAsync(QueueJob job, TimeSpan delay)
    {
        var ct = job.Cts!.Token;
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
            var (state, detail) = await ProcessJobAsync(job, ct);
            job.State = state;
            job.Detail = detail;
            job.Percent = 100;
        }
        catch (OperationCanceledException)
        {
            job.State = JobState.Waiting;
            job.Detail = QueuePaused ? "Paused" : "";
            job.Percent = 0;
            if (_removeAfterCancel.Remove(job.Id)) Queue.Remove(job);
        }
        catch (Exception ex)
        {
            job.State = JobState.Failed;
            job.Detail = FirstUsefulLine(ex.Message);
        }
        finally
        {
            job.Cts?.Dispose();
            job.Cts = null;
            _active.Remove(job);
            Changed?.Invoke();
            Pump();
        }
    }

    /// <summary>Another job is working on this video (two links to the same video, or a save/convert in
    /// progress): wait for it to finish rather than downloading the same thing twice.</summary>
    private async Task WaitUntilFreeAsync(QueueJob job, string videoId, CancellationToken ct)
    {
        if (!_busy.Contains(videoId)) return;
        ReportJob(job, 0, "Waiting – this video is already being worked on…");
        while (_busy.Contains(videoId)) await Task.Delay(400, ct);
    }

    private static string FirstUsefulLine(string message) =>
        message.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Select(l => l.Replace("ERROR: ", "")).LastOrDefault() ?? "Failed";

    private void ReportJob(QueueJob job, double pct, string detail)
    {
        job.Percent = pct;
        job.Detail = detail;
        JobProgress?.Invoke(job);
    }

    private async Task<(JobState, string)> ProcessJobAsync(QueueJob job, CancellationToken ct)
    {
        if (job.Target is { } target)
        {
            if (!Library.Contains(target)) return (JobState.Skipped, "No longer in the library");
            var fresh = await RedownloadCoreAsync(job, target, ct);
            job.ResultId = fresh.Metadata.Id;
            return (JobState.Done, "Re-downloaded");
        }

        if (job.VideoId != null) await WaitUntilFreeAsync(job, job.VideoId, ct);
        var existing = job.VideoId == null ? null : Library.FirstOrDefault(e => e.Metadata.Id == job.VideoId);
        if (existing != null) return await HandleExistingAsync(job, existing, ct);

        ReportJob(job, 0, "Reading info…");
        var (meta, raw) = await FetchInfoAsync(job.Url, ct);
        job.VideoId = meta.Id;
        if (string.IsNullOrEmpty(job.Title) || job.Title.StartsWith("http")) job.Title = meta.Title;
        await WaitUntilFreeAsync(job, meta.Id, ct);
        if (Library.FirstOrDefault(e => e.Metadata.Id == meta.Id) is { } dup) return await HandleExistingAsync(job, dup, ct);

        var name = LibraryOps.Sanitize(job.NameOverride is { Length: > 0 } n ? n : (job.NamePrefix ?? "") + ApplyPattern(meta));
        _busy.Add(meta.Id);
        LibraryEntry entry;
        try { entry = await DownloadNewAsync(job, meta, raw, name, ct); }
        finally { _busy.Remove(meta.Id); }

        Library.Insert(0, entry);
        NewIds.Add(entry.Metadata.Id);
        job.ResultId = entry.Metadata.Id;
        Changed?.Invoke();
        if (job.PlayAfter) PlayRequested?.Invoke(entry.Metadata.Id);
        else Toast?.Invoke("Downloaded · " + entry.Name);
        return (JobState.Done, Path.GetFileName(entry.MainPath) ?? "Done");
    }

    /// <summary>Already downloaded: just add the audio file if one was asked for and is missing.</summary>
    private async Task<(JobState, string)> HandleExistingAsync(QueueJob job, LibraryEntry existing, CancellationToken ct)
    {
        job.ResultId = existing.Metadata.Id;
        job.Title = existing.Name;
        if (job.Options.Mode != SaveAs.Video && !existing.IsAudioOnly && !HasAudioExport(existing, job.Options.Format))
        {
            await WaitUntilFreeAsync(job, existing.Metadata.Id, ct);
            if (!Library.Contains(existing)) return (JobState.Skipped, "No longer in the library");
            if (HasAudioExport(existing, job.Options.Format)) return (JobState.Skipped, "Already in library");
            ReportJob(job, 0, $"Saving {job.Options.Format} from the library copy…");
            _busy.Add(existing.Metadata.Id);
            try { await ExportAudioCoreAsync(existing, job.Options.Format, new Progress<double>(p => ReportJob(job, p, $"Saving {job.Options.Format} · {p:0}%")), ct); }
            finally { _busy.Remove(existing.Metadata.Id); }
            Changed?.Invoke();
            if (job.PlayAfter) PlayRequested?.Invoke(existing.Metadata.Id);
            return (JobState.Done, $"Already downloaded – saved {job.Options.Format}");
        }
        if (job.PlayAfter) PlayRequested?.Invoke(existing.Metadata.Id);
        return (JobState.Skipped, "Already in library");
    }

    /// <summary>Download one video into the library under <paramref name="name"/>.</summary>
    private async Task<LibraryEntry> DownloadNewAsync(QueueJob job, VideoMetadata meta, string raw, string name, CancellationToken ct)
    {
        var o = job.Options;
        var root = Settings.LibraryFolder;
        Directory.CreateDirectory(root);
        var entry = new LibraryEntry
        {
            Metadata = meta, Name = name, Root = root, IsAudioOnly = o.Mode == SaveAs.Audio, DownloadMaxHeight = o.MaxHeight,
        };
        var tempBase = meta.Id + ".download";

        var progress = new Progress<DownloadProgress>(p =>
            ReportJob(job, p.Percent, $"{p.Percent:0}%" + (string.IsNullOrWhiteSpace(p.Speed) || p.Speed == "NA" ? "" : " · " + p.Speed)));
        var log = new Progress<string>(line => Debug.WriteLine("yt-dlp: " + line));
        var result = await YtDlp.DownloadAsync(job.Url, meta, root, tempBase, audioOnly: o.Mode == SaveAs.Audio,
            preferAac: o.Format == AudioFormat.M4A, o.MaxHeight, progress, log, ct);

        try
        {
            Directory.CreateDirectory(entry.SidecarDir);
            await File.WriteAllTextAsync(entry.InfoFile, raw, ct);
            await TryDownloadThumbnailAsync(meta, entry.ThumbnailPath, ct);
            try { LibraryOps.AddThumbnailCopy(entry); } catch (Exception ex) { Debug.WriteLine("Thumbnail copy failed: " + ex.Message); }

            if (o.Mode == SaveAs.Audio)
            {
                var produced = Path.Combine(root, meta.Id + ".converting" + AudioFormats.Extension(o.Format));
                await Ffmpeg.ExportAudioAsync(result.VideoFile, null, produced, o.Format, null, null, TagsFor(entry), entry.ThumbnailPath,
                    new Progress<double>(p => ReportJob(job, p, $"Converting to {o.Format} · {p:0}%")), ct);
                LibraryOps.TryDelete(result.VideoFile);
                LibraryOps.AddFile(entry, produced, new EntryFile { Role = FileRole.Main, Format = o.Format.ToString() });
            }
            else
            {
                LibraryOps.AddFile(entry, result.VideoFile, new EntryFile { Role = FileRole.Main, Format = "Video" });
                if (result.AudioFile != null)
                    LibraryOps.AddFile(entry, result.AudioFile, new EntryFile { Role = FileRole.SeparateAudio, Suffix = " [audio track]" });
                if (o.Mode == SaveAs.VideoAndAudio)
                    await ExportAudioCoreAsync(entry, o.Format, new Progress<double>(p => ReportJob(job, p, $"Saving {o.Format} · {p:0}%")), ct, save: false);
            }

            await ProbeAsync(entry);
            try { entry.SponsorSegments = await SponsorBlockService.GetSegmentsAsync(meta.Id, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Debug.WriteLine("SponsorBlock lookup failed: " + ex.Message); }
            entry.Save();
            return entry;
        }
        catch
        {
            // Leave nothing half-made behind
            foreach (var f in entry.Files) LibraryOps.TryDelete(entry.FullPath(f));
            foreach (var pattern in new[] { meta.Id + ".download*", meta.Id + ".converting*", meta.Id + ".thumbnail.jpg" })
                foreach (var f in Directory.EnumerateFiles(root, pattern)) LibraryOps.TryDelete(f);
            throw;
        }
    }

    // =========================================================================================
    // Re-download
    // =========================================================================================
    /// <summary>The settings an item was saved with (older items without a recorded quality use the current default).</summary>
    public DownloadOptions OriginalOptions(LibraryEntry e)
    {
        var mode = e.IsAudioOnly ? SaveAs.Audio
            : e.Files.Any(f => f.Role == FileRole.AudioExport) ? SaveAs.VideoAndAudio : SaveAs.Video;
        var format = e.IsAudioOnly && Enum.TryParse<AudioFormat>(e.Main?.Format, true, out var f1) ? f1
            : e.Files.FirstOrDefault(f => f.Role == FileRole.AudioExport) is { } x && Enum.TryParse<AudioFormat>(x.Format, true, out var f2) ? f2
            : AudioFormats.Parse(Settings.AudioFormat);
        return new DownloadOptions(mode, format, e.DownloadMaxHeight ?? DownloadOptions.ParseQuality(Settings.Quality));
    }

    /// <summary>Queue re-downloads. <paramref name="options"/> null = each item's own original settings.</summary>
    public int QueueRedownloads(IEnumerable<string> ids, DownloadOptions? options, bool keepExtras)
    {
        var jobs = ids.Select(Get).Select(e => new QueueJob
        {
            Url = e.Metadata.WebpageUrl, VideoId = e.Metadata.Id, Title = e.Name, Target = e, KeepExtras = keepExtras,
            Options = options ?? OriginalOptions(e),
        }).ToList();
        var before = Queue.Count;
        Enqueue(jobs);
        return Queue.Count - before;
    }

    /// <summary>
    /// Download an item again and swap it in. The new copy is downloaded under a temporary name first; only when that
    /// succeeds are the old main file / audio track / thumbnail removed, the kept extras carried over, and everything
    /// renamed back. If it fails or is cancelled, the old files are untouched.
    /// </summary>
    private async Task<LibraryEntry> RedownloadCoreAsync(QueueJob job, LibraryEntry old, CancellationToken ct)
    {
        await WaitUntilFreeAsync(job, old.Metadata.Id, ct);
        if (!Library.Contains(old)) throw new InvalidOperationException("No longer in the library");
        var name = old.Name;
        _busy.Add(old.Metadata.Id);
        try
        {
            ReportJob(job, 0, "Reading info…");
            var (meta, raw) = await FetchInfoAsync(old.Metadata.WebpageUrl, ct);
            var tempName = LibraryOps.Sanitize($"{name} [re-download {meta.Id}]");
            LibraryEntry fresh;
            try
            {
                fresh = await DownloadNewAsync(job, meta, raw, tempName, ct);
            }
            catch
            {
                try { old.Save(); } catch { /* put the old record back */ }
                await Task.Delay(300, CancellationToken.None);
                foreach (var f in Directory.EnumerateFiles(old.Root, tempName + "*")) LibraryOps.TryDelete(f);
                throw;
            }

            // From here until the rename below, the item's final file names are briefly free on disk: reserve them so
            // a download finishing in parallel (with the same title) picks "Name (2)" instead of taking them.
            var reserved = fresh.Files.Select(f => LibraryOps.TargetFileName(fresh, f, name)).ToList();
            LibraryOps.Reserve(reserved);
            try
            {
                if (ReleaseMediaAsync != null) await ReleaseMediaAsync(old.Metadata.Id);

                var replacedFormats = fresh.Files
                    .Where(f => f.Role == FileRole.AudioExport || (fresh.IsAudioOnly && f.Role == FileRole.Main))
                    .Select(f => f.Format).ToHashSet();
                foreach (var f in old.Files.ToList())
                {
                    var extra = f.Role is FileRole.AudioExport or FileRole.Clip;
                    var remove = !extra || !job.KeepExtras || (f.Role == FileRole.AudioExport && replacedFormats.Contains(f.Format));
                    if (remove) await RetryIo(() => { var p = old.FullPath(f); if (File.Exists(p)) File.Delete(p); });
                    else fresh.Files.Add(f);
                }

                fresh.DownloadedAt = DateTime.Now;
                fresh.Save();
                await RetryIo(() => LibraryOps.Rename(fresh, name));
            }
            finally { LibraryOps.Unreserve(reserved); }

            var index = Library.IndexOf(old);
            if (index >= 0) Library[index] = fresh; else Library.Insert(0, fresh);
            NewIds.Add(fresh.Metadata.Id);
            Changed?.Invoke();
            Toast?.Invoke("Re-downloaded · " + fresh.Name);
            return fresh;
        }
        finally { _busy.Remove(old.Metadata.Id); }
    }

    // =========================================================================================
    // Audio, clips, rename, delete
    // =========================================================================================
    public static bool HasAudioExport(LibraryEntry e, AudioFormat f) =>
        e.Files.Any(x => x.Role == FileRole.AudioExport && x.Format == f.ToString());

    /// <summary>Save the audio of each item in <paramref name="format"/> (local conversion - no re-download).</summary>
    public async Task<int> SaveAudioAsync(IReadOnlyList<string> ids, AudioFormat format)
    {
        RequireFfmpeg();
        var entries = ids.Select(Get).Where(e => !(e.IsAudioOnly && e.Main?.Format == format.ToString()) && !HasAudioExport(e, format)).ToList();
        if (entries.Count == 0) return 0;
        await _taskLane.WaitAsync();
        try
        {
            var done = 0;
            foreach (var e in entries)
            {
                if (!_busy.Add(e.Metadata.Id)) continue;
                try
                {
                    var label = entries.Count > 1 ? $"Saving {format} ({done + 1}/{entries.Count})" : $"Saving {format}";
                    await ExportAudioCoreAsync(e, format, new Progress<double>(p => TaskProgress?.Invoke(label, p)), CancellationToken.None);
                    done++;
                }
                finally { _busy.Remove(e.Metadata.Id); }
            }
            return done;
        }
        finally
        {
            _taskLane.Release();
            TaskProgress?.Invoke(null, 0);
            Changed?.Invoke();
            Pump();   // queued jobs for these items were waiting for them to be free
        }
    }

    private async Task ExportAudioCoreAsync(LibraryEntry entry, AudioFormat format, IProgress<double> progress, CancellationToken ct, bool save = true)
    {
        var produced = Path.Combine(entry.Root, entry.Metadata.Id + ".converting" + AudioFormats.Extension(format));
        await Ffmpeg.ExportAudioAsync(entry.MainPath!, entry.SeparateAudioPath, produced, format, null, null, TagsFor(entry),
            entry.ThumbnailPath, progress, ct);
        LibraryOps.AddFile(entry, produced, new EntryFile { Role = FileRole.AudioExport, Format = format.ToString() });
        if (save) entry.Save();
    }

    public static string ClipSuffix(TimeSpan start, TimeSpan end) => $" [clip {TimeFmt.FileSafe(start)}-{TimeFmt.FileSafe(end)}]";

    /// <summary>Cut a clip from an item. A name equal to the automatic one follows the item when it's renamed.</summary>
    public async Task<EntryFile> SaveClipAsync(string id, double startSec, double endSec, ClipKind kind, string? name)
    {
        RequireFfmpeg();
        var entry = Get(id);
        var start = TimeSpan.FromSeconds(Math.Max(0, startSec));
        var end = TimeSpan.FromSeconds(endSec);
        if (end <= start) throw new InvalidOperationException("The clip's end must be after its start.");
        var format = AudioFormats.Parse(Settings.AudioFormat);
        var audio = kind == ClipKind.Audio || entry.IsAudioOnly;

        var defaultName = entry.Name + ClipSuffix(start, end);
        var typed = LibraryOps.Sanitize(string.IsNullOrWhiteSpace(name) ? defaultName : name.Trim());
        var file = new EntryFile
        {
            Role = FileRole.Clip,
            Format = audio ? $"Audio {format}" : kind == ClipKind.FastCopy ? "MP4 fast copy" : "MP4 precise",
            ClipStart = start.TotalSeconds,
            ClipEnd = end.TotalSeconds,
        };
        if (string.Equals(typed, defaultName, StringComparison.OrdinalIgnoreCase)) file.Suffix = ClipSuffix(start, end);
        else file.FixedName = typed;

        await _taskLane.WaitAsync();
        try
        {
            var src = entry.MainPath!;
            var sep = entry.SeparateAudioPath;
            var ext = audio ? AudioFormats.Extension(format) : ".mp4";
            var produced = Path.Combine(entry.Root, entry.Metadata.Id + ".clipping" + ext);
            var progress = new Progress<double>(p => TaskProgress?.Invoke("Cutting clip", p));
            if (audio)
                await Ffmpeg.ExportAudioAsync(src, sep, produced, format, start, end,
                    TagsFor(entry, $"{entry.Metadata.Title} ({TimeFmt.Short(start)}–{TimeFmt.Short(end)})"), entry.ThumbnailPath, progress, CancellationToken.None);
            else
                await Ffmpeg.ClipVideoAsync(src, sep, produced, start, end, precise: kind != ClipKind.FastCopy, progress, CancellationToken.None);

            var added = LibraryOps.AddFile(entry, produced, file);
            entry.Save();
            return added;
        }
        finally
        {
            _taskLane.Release();
            TaskProgress?.Invoke(null, 0);
            Changed?.Invoke();
        }
    }

    public async Task RenameEntryAsync(string id, string newName)
    {
        var e = Get(id);
        newName = LibraryOps.Sanitize(newName);
        if (newName == e.Name) return;
        if (_busy.Contains(id)) throw new InvalidOperationException("That item is busy – try again when it's finished.");
        if (ReleaseMediaAsync != null) await ReleaseMediaAsync(id);
        await RetryIo(() => LibraryOps.Rename(e, newName));
        Changed?.Invoke();
    }

    public async Task RenameFileAsync(string id, string fileName, string newName)
    {
        var e = Get(id);
        var f = e.Files.FirstOrDefault(x => x.FileName == fileName) ?? throw new InvalidOperationException("That file is gone.");
        if (f.Role is FileRole.Main or FileRole.SeparateAudio or FileRole.Thumbnail)
            throw new InvalidOperationException("This file follows the item's name – rename the item instead.");
        if (ReleaseMediaAsync != null) await ReleaseMediaAsync(id);
        await RetryIo(() => LibraryOps.RenameFile(e, f, newName));
        Changed?.Invoke();
    }

    /// <summary>Move items (every file, plus the app's records) to the Recycle Bin.</summary>
    public async Task<int> DeleteAsync(IReadOnlyList<string> ids)
    {
        var count = 0;
        foreach (var id in ids)
        {
            if (Library.FirstOrDefault(e => e.Metadata.Id == id) is not { } e) continue;
            if (_busy.Contains(id)) { Toast?.Invoke($"\"{e.Name}\" is busy – skipped"); continue; }
            if (ReleaseMediaAsync != null) await ReleaseMediaAsync(id);
            foreach (var f in e.Files) await RecycleAsync(e.FullPath(f));
            LibraryOps.TryDelete(e.InfoFile);
            LibraryOps.TryDelete(e.ThumbnailPath);
            LibraryOps.TryDelete(e.MetaFile);
            Library.Remove(e);
            NewIds.Remove(id);
            foreach (var q in Queue.Where(q => q.Target == e && q.State == JobState.Waiting))
            { q.State = JobState.Skipped; q.Detail = "Deleted from library"; }
            count++;
        }
        Changed?.Invoke();
        return count;
    }

    private static async Task RecycleAsync(string path)
    {
        if (!File.Exists(path)) return;
        await RetryIo(() => Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin));
    }

    // =========================================================================================
    // Settings
    // =========================================================================================
    public void SaveSettings() { Settings.Save(); Changed?.Invoke(); }

    // =========================================================================================
    // Helpers
    // =========================================================================================
    private void RequireFfmpeg()
    {
        if (!Ffmpeg.IsAvailable) throw new InvalidOperationException("ffmpeg isn't installed yet – it's needed for audio files and clips.");
    }

    public static FfmpegService.Tags TagsFor(LibraryEntry e, string? titleOverride = null) => new(
        titleOverride ?? e.Metadata.Title,
        e.Metadata.Channel ?? e.Metadata.Uploader,
        e.Metadata.UploadDate?.ToString("yyyy-MM-dd"),
        e.Metadata.WebpageUrl,
        e.Metadata.Description);

    /// <summary>Best available thumbnail; not every video has a max-resolution one, so fall back through YouTube's sizes.</summary>
    public static async Task TryDownloadThumbnailAsync(VideoMetadata meta, string path, CancellationToken ct)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(meta.ThumbnailUrl)) candidates.Add(meta.ThumbnailUrl);
        if (!string.IsNullOrEmpty(meta.Id))
            foreach (var size in new[] { "maxresdefault", "sddefault", "hqdefault", "mqdefault" })
                candidates.Add($"https://i.ytimg.com/vi/{meta.Id}/{size}.jpg");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        foreach (var url in candidates.Distinct())
        {
            try
            {
                var bytes = await http.GetByteArrayAsync(url, ct);
                if (bytes.Length < 1024) continue;
                await File.WriteAllBytesAsync(path, bytes, ct);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* next size */ }
        }
    }

    /// <summary>Retry briefly - the player may take a moment to let go of a file.</summary>
    private static async Task RetryIo(Action action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { action(); return; }
            catch (IOException) when (attempt < 15) { await Task.Delay(200); }
            catch (UnauthorizedAccessException) when (attempt < 15) { await Task.Delay(200); }
        }
    }
}

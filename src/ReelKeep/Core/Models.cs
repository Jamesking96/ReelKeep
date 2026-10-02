using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReelKeep.Core;

public sealed record Chapter(double StartSeconds, double EndSeconds, string Title);

/// <summary>A SponsorBlock segment (community-submitted sponsor reads, intros, "like and subscribe" etc.).</summary>
public sealed record SponsorSegment(double Start, double End, string Category, string Uuid);

/// <summary>The useful subset of yt-dlp's info JSON (the full raw JSON is saved alongside as &lt;id&gt;.info.json).</summary>
public sealed class VideoMetadata
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string WebpageUrl { get; set; } = "";
    public string? Channel { get; set; }
    public string? ChannelId { get; set; }
    public string? ChannelUrl { get; set; }
    public long? ChannelFollowerCount { get; set; }
    public bool? ChannelIsVerified { get; set; }
    public string? Uploader { get; set; }
    public string? UploaderId { get; set; }
    public string? UploaderUrl { get; set; }
    public DateTime? UploadDate { get; set; }
    public double? DurationSeconds { get; set; }
    public long? ViewCount { get; set; }
    public long? LikeCount { get; set; }
    public long? CommentCount { get; set; }
    public string? Description { get; set; }
    public List<string> Tags { get; set; } = new();
    public List<string> Categories { get; set; } = new();
    public string? ThumbnailUrl { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? Fps { get; set; }
    public string? License { get; set; }
    public int? AgeLimit { get; set; }
    public string? Availability { get; set; }
    public string? LiveStatus { get; set; }
    public string? Language { get; set; }
    public List<Chapter> Chapters { get; set; } = new();
    /// <summary>format_id -> true if the format contains video (used to tell DASH video/audio files apart).</summary>
    public Dictionary<string, bool> FormatHasVideo { get; set; } = new();

    /// <summary>Parse the JSON produced by <c>yt-dlp -J</c>.</summary>
    public static VideoMetadata FromYtDlpJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var m = new VideoMetadata
        {
            Id = Str(r, "id") ?? "",
            Title = Str(r, "title") ?? "(untitled)",
            WebpageUrl = Str(r, "webpage_url") ?? Str(r, "original_url") ?? "",
            Channel = Str(r, "channel"),
            ChannelId = Str(r, "channel_id"),
            ChannelUrl = Str(r, "channel_url"),
            ChannelFollowerCount = Long(r, "channel_follower_count"),
            ChannelIsVerified = r.TryGetProperty("channel_is_verified", out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null,
            Uploader = Str(r, "uploader"),
            UploaderId = Str(r, "uploader_id"),
            UploaderUrl = Str(r, "uploader_url"),
            DurationSeconds = Dbl(r, "duration"),
            ViewCount = Long(r, "view_count"),
            LikeCount = Long(r, "like_count"),
            CommentCount = Long(r, "comment_count"),
            Description = Str(r, "description"),
            Tags = StrList(r, "tags"),
            Categories = StrList(r, "categories"),
            Width = (int?)Long(r, "width"),
            Height = (int?)Long(r, "height"),
            Fps = Dbl(r, "fps"),
            License = Str(r, "license"),
            AgeLimit = (int?)Long(r, "age_limit"),
            Availability = Str(r, "availability"),
            LiveStatus = Str(r, "live_status"),
            Language = Str(r, "language"),
        };

        if (Str(r, "upload_date") is { Length: 8 } d &&
            DateTime.TryParseExact(d, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var date))
            m.UploadDate = date;

        // Prefer a JPEG thumbnail (WinForms PictureBox can't decode WebP)
        string? jpg = null;
        if (r.TryGetProperty("thumbnails", out var thumbs) && thumbs.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in thumbs.EnumerateArray())
            {
                var url = Str(t, "url");
                if (url != null && url.Contains(".jpg", StringComparison.OrdinalIgnoreCase)) jpg = url; // list is sorted worst -> best
            }
        }
        m.ThumbnailUrl = jpg ?? (m.Id.Length > 0 ? $"https://i.ytimg.com/vi/{m.Id}/hqdefault.jpg" : Str(r, "thumbnail"));

        if (r.TryGetProperty("chapters", out var chapters) && chapters.ValueKind == JsonValueKind.Array)
            foreach (var c in chapters.EnumerateArray())
                m.Chapters.Add(new Chapter(Dbl(c, "start_time") ?? 0, Dbl(c, "end_time") ?? 0, Str(c, "title") ?? ""));

        if (r.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
            foreach (var f in formats.EnumerateArray())
                if (Str(f, "format_id") is { } fid)
                    m.FormatHasVideo[fid] = Str(f, "vcodec") is { } vc && vc != "none";

        return m;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static long? Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? (long)p.GetDouble() : null;

    private static double? Dbl(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : null;

    private static List<string> StrList(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : new List<string>();
}

public enum FileRole { Main, SeparateAudio, AudioExport, Clip, Thumbnail }

/// <summary>One media file that belongs to a library entry.</summary>
public sealed class EntryFile
{
    public FileRole Role { get; set; }
    /// <summary>File name relative to the library folder.</summary>
    public string FileName { get; set; } = "";
    /// <summary>Appended to the entry's name to build the file name (e.g. " [clip 01m23s-02m10s]"). Ignored when <see cref="FixedName"/> is set.</summary>
    public string Suffix { get; set; } = "";
    /// <summary>A custom base name that does not follow the entry when it's renamed (e.g. a clip you named yourself).</summary>
    public string? FixedName { get; set; }
    public string? Format { get; set; }
    public double? ClipStart { get; set; }
    public double? ClipEnd { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public string Describe() => Role switch
    {
        FileRole.Main => "Main download",
        FileRole.SeparateAudio => "Audio track (for playback)",
        FileRole.AudioExport => $"Audio ({Format})",
        FileRole.Clip => $"Clip {Fmt(ClipStart)}–{Fmt(ClipEnd)} ({Format})",
        FileRole.Thumbnail => "Thumbnail",
        _ => Role.ToString(),
    };

    private static string Fmt(double? s) => s is { } v ? TimeFmt.Short(TimeSpan.FromSeconds(v)) : "?";
}

/// <summary>
/// A downloaded video/song in the local library. Media files are named after <see cref="Name"/>; the app's own
/// sidecar files (&lt;id&gt;.meta.json, &lt;id&gt;.info.json, &lt;id&gt;.jpg) live in a ".library" sub-folder so the
/// library folder itself only contains your nicely named media.
/// </summary>
public sealed class LibraryEntry
{
    public const string SidecarFolderName = ".library";

    public VideoMetadata Metadata { get; set; } = new();
    /// <summary>Base file name chosen by the user (defaults to the video title).</summary>
    public string Name { get; set; } = "";
    public bool IsAudioOnly { get; set; }
    /// <summary>Max video height used for the download (0 = best). Null for items downloaded before this was recorded.</summary>
    public int? DownloadMaxHeight { get; set; }
    /// <summary>Actual size / frame rate of the downloaded video (probed with ffprobe; null until known).</summary>
    public int? VideoWidth { get; set; }
    public int? VideoHeight { get; set; }
    public double? VideoFps { get; set; }
    public List<EntryFile> Files { get; set; } = new();
    public DateTime DownloadedAt { get; set; } = DateTime.Now;
    public List<SponsorSegment> SponsorSegments { get; set; } = new();

    // Legacy fields (first version of the app) - read for migration, never written.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? VideoFile { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? AudioFile { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ThumbnailFile { get; set; }

    [JsonIgnore] public string Root { get; set; } = "";

    [JsonIgnore] public string SidecarDir => Path.Combine(Root, SidecarFolderName);
    [JsonIgnore] public string MetaFile => Path.Combine(SidecarDir, Metadata.Id + ".meta.json");
    [JsonIgnore] public string InfoFile => Path.Combine(SidecarDir, Metadata.Id + ".info.json");
    [JsonIgnore] public string ThumbnailPath => Path.Combine(SidecarDir, Metadata.Id + ".jpg");

    [JsonIgnore] public EntryFile? Main => Files.FirstOrDefault(f => f.Role == FileRole.Main);
    [JsonIgnore] public EntryFile? SeparateAudio => Files.FirstOrDefault(f => f.Role == FileRole.SeparateAudio);

    public string FullPath(EntryFile f) => Path.Combine(Root, f.FileName);
    public string? MainPath => Main is { } m ? FullPath(m) : null;
    public string? SeparateAudioPath => SeparateAudio is { } a ? FullPath(a) : null;

    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public void Save()
    {
        Directory.CreateDirectory(SidecarDir);
        try { File.SetAttributes(SidecarDir, File.GetAttributes(SidecarDir) | FileAttributes.Hidden); } catch { }
        File.WriteAllText(MetaFile, JsonSerializer.Serialize(this, JsonOptions));
    }

    public static LibraryEntry? Load(string metaFile, string root)
    {
        try
        {
            var e = JsonSerializer.Deserialize<LibraryEntry>(File.ReadAllText(metaFile), JsonOptions);
            if (e == null) return null;
            e.Root = root;
            e.MigrateLegacy(metaFile);
            e.Files.RemoveAll(f => !File.Exists(e.FullPath(f)));
            return e.Main != null ? e : null;
        }
        catch { return null; }
    }

    /// <summary>Convert an entry written by the first version (absolute paths, sidecars next to the media).</summary>
    private void MigrateLegacy(string metaFile)
    {
        if (Files.Count == 0 && VideoFile != null)
        {
            Files.Add(new EntryFile { Role = FileRole.Main, FileName = Path.GetFileName(VideoFile) });
            if (AudioFile != null) Files.Add(new EntryFile { Role = FileRole.SeparateAudio, FileName = Path.GetFileName(AudioFile), Suffix = " [audio track]" });
        }
        if (string.IsNullOrWhiteSpace(Name)) Name = LibraryOps.Sanitize(Metadata.Title);

        var legacyDir = Path.GetDirectoryName(metaFile)!;
        if (!string.Equals(Path.GetFullPath(legacyDir), Path.GetFullPath(SidecarDir), StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(SidecarDir);
            MoveIfExists(Path.Combine(legacyDir, Metadata.Id + ".info.json"), InfoFile);
            MoveIfExists(ThumbnailFile ?? Path.Combine(legacyDir, Metadata.Id + ".jpg"), ThumbnailPath);
            VideoFile = AudioFile = ThumbnailFile = null;
            Save();
            try { File.Delete(metaFile); } catch { }
        }
    }

    private static void MoveIfExists(string from, string to)
    {
        if (File.Exists(from) && !File.Exists(to)) File.Move(from, to);
    }
}

/// <summary>File naming, renaming and cleanup for library entries.</summary>
public static class LibraryOps
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3" };

    /// <summary>Make any text safe to use as a Windows file name.</summary>
    public static string Sanitize(string? name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var chars = (name ?? "").Select(c => bad.Contains(c) ? ' ' : c).ToArray();
        var s = System.Text.RegularExpressions.Regex.Replace(new string(chars), @"\s+", " ").Trim().TrimEnd('.', ' ');
        if (s.Length > 150) s = s[..150].TrimEnd('.', ' ');
        if (s.Length == 0) s = "untitled";
        if (Reserved.Contains(s)) s = "_" + s;
        return s;
    }

    /// <summary>Expand a naming pattern: {title} {channel} {id} {date} {year}.</summary>
    public static string ApplyPattern(string pattern, VideoMetadata m) => Sanitize(pattern
        .Replace("{title}", m.Title, StringComparison.OrdinalIgnoreCase)
        .Replace("{channel}", m.Channel ?? m.Uploader ?? "", StringComparison.OrdinalIgnoreCase)
        .Replace("{id}", m.Id, StringComparison.OrdinalIgnoreCase)
        .Replace("{date}", m.UploadDate?.ToString("yyyy-MM-dd") ?? "", StringComparison.OrdinalIgnoreCase)
        .Replace("{year}", m.UploadDate?.Year.ToString() ?? "", StringComparison.OrdinalIgnoreCase));

    public static string TargetFileName(LibraryEntry e, EntryFile f, string? newEntryName = null, string? ext = null) =>
        (f.FixedName ?? (newEntryName ?? e.Name) + f.Suffix) + (ext ?? Path.GetExtension(f.FileName));

    /// <summary>File names promised to an operation that's in the middle of moving files into them (a re-download
    /// swapping in its new copy). Downloads finishing in parallel treat them as taken. Used from the UI thread only.</summary>
    private static readonly Dictionary<string, int> ReservedNames = new(StringComparer.OrdinalIgnoreCase);

    public static void Reserve(IEnumerable<string> fileNames)
    {
        foreach (var n in fileNames) ReservedNames[n] = ReservedNames.GetValueOrDefault(n) + 1;
    }

    public static void Unreserve(IEnumerable<string> fileNames)
    {
        foreach (var n in fileNames)
            if (ReservedNames.TryGetValue(n, out var c)) { if (c <= 1) ReservedNames.Remove(n); else ReservedNames[n] = c - 1; }
    }

    /// <summary>A file name in <paramref name="root"/> that isn't taken: "name.ext", "name (2).ext", ...</summary>
    public static string UniqueFileName(string root, string baseName, string ext, IEnumerable<string>? alsoTaken = null)
    {
        var taken = new HashSet<string>(alsoTaken ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var candidate = baseName + ext;
        for (var i = 2; File.Exists(Path.Combine(root, candidate)) || taken.Contains(candidate) || ReservedNames.ContainsKey(candidate); i++)
            candidate = $"{baseName} ({i}){ext}";
        return candidate;
    }

    /// <summary>Move a freshly produced file to its final name and register it on the entry.</summary>
    public static EntryFile AddFile(LibraryEntry e, string producedPath, EntryFile f)
    {
        var ext = Path.GetExtension(producedPath);
        var desired = TargetFileName(e, f, ext: ext);
        var baseName = Path.GetFileNameWithoutExtension(desired);
        var name = UniqueFileName(e.Root, baseName, ext);
        var target = Path.Combine(e.Root, name);
        if (!string.Equals(Path.GetFullPath(producedPath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            File.Move(producedPath, target);
        // If we had to add " (2)", remember the real base name so renames keep it unique
        if (!string.Equals(name, desired, StringComparison.OrdinalIgnoreCase) && f.FixedName == null)
            f.Suffix += name[(e.Name.Length + f.Suffix.Length)..^ext.Length];
        f.FileName = name;
        e.Files.Add(f);
        return f;
    }

    /// <summary>Rename an entry: every media file that follows the entry name is renamed to match.</summary>
    public static void Rename(LibraryEntry e, string newName)
    {
        newName = Sanitize(newName);
        var plan = e.Files
            .Where(f => f.FixedName == null)
            .Select(f => (File: f, From: e.FullPath(f), To: TargetFileName(e, f, newName)))
            .Where(x => !string.Equals(Path.GetFileName(x.From), x.To, StringComparison.Ordinal))
            .ToList();

        var ours = new HashSet<string>(e.Files.Select(e.FullPath).Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        foreach (var (_, _, to) in plan)
        {
            var full = Path.GetFullPath(Path.Combine(e.Root, to));
            if (File.Exists(full) && !ours.Contains(full))
                throw new IOException($"A file called \"{to}\" already exists in the library folder.");
        }

        // Two-phase move so swaps and case-only renames work
        var temps = plan.Select(p => (p.File, Temp: p.From + ".renaming", p.To)).ToList();
        for (var i = 0; i < plan.Count; i++) File.Move(plan[i].From, temps[i].Temp);
        foreach (var (file, temp, to) in temps)
        {
            File.Move(temp, Path.Combine(e.Root, to));
            file.FileName = to;
        }
        e.Name = newName;
        e.Save();
    }

    /// <summary>Rename one file to a custom name (it then stops following the entry's name).</summary>
    public static void RenameFile(LibraryEntry e, EntryFile f, string newBaseName)
    {
        newBaseName = Sanitize(newBaseName);
        var to = newBaseName + Path.GetExtension(f.FileName);
        var toFull = Path.Combine(e.Root, to);
        var fromFull = e.FullPath(f);
        if (!string.Equals(Path.GetFullPath(fromFull), Path.GetFullPath(toFull), StringComparison.OrdinalIgnoreCase) && File.Exists(toFull))
            throw new IOException($"A file called \"{to}\" already exists.");
        File.Move(fromFull, fromFull + ".renaming");
        File.Move(fromFull + ".renaming", toFull);
        f.FileName = to;
        f.FixedName = newBaseName;
        e.Save();
    }

    /// <summary>
    /// Put a copy of the video's thumbnail next to the media as "&lt;name&gt;.jpg" (it follows renames and is deleted
    /// with the entry). The original stays in the .library folder for the app's own use. Returns true if added.
    /// </summary>
    public static bool AddThumbnailCopy(LibraryEntry e)
    {
        if (e.Files.Any(f => f.Role == FileRole.Thumbnail) || !File.Exists(e.ThumbnailPath)) return false;
        var temp = Path.Combine(e.Root, e.Metadata.Id + ".thumbnail.jpg");
        File.Copy(e.ThumbnailPath, temp, overwrite: true);
        AddFile(e, temp, new EntryFile { Role = FileRole.Thumbnail, Format = "JPG" });
        return true;
    }

    public static void DeleteEntry(LibraryEntry e)
    {
        foreach (var f in e.Files) TryDelete(e.FullPath(f));
        TryDelete(e.InfoFile);
        TryDelete(e.ThumbnailPath);
        TryDelete(e.MetaFile);
    }

    public static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}

/// <summary>Small time formatting helpers shared by the engine and the UI data.</summary>
public static class TimeFmt
{
    /// <summary>1:05 / 1:02:05</summary>
    public static string Short(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    /// <summary>01m23s - file-name safe.</summary>
    public static string FileSafe(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:00}m{t.Seconds:00}s" : $"{(int)t.TotalMinutes:00}m{t.Seconds:00}s";
}

/// <summary>User settings, stored in %LOCALAPPDATA%\ReelKeep\settings.json.</summary>
public sealed class AppSettings
{
    public const string AppName = "ReelKeep";

    public string LibraryFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), AppName);

    // Defaults for new downloads
    /// <summary>"video", "audio" or "va" (video + audio file).</summary>
    public string SaveAs { get; set; } = "va";
    public string Quality { get; set; } = "Best";
    /// <summary>Preferred audio format (downloads, "Save audio" and audio clips).</summary>
    public string AudioFormat { get; set; } = AudioFormats.Default.ToString();
    /// <summary>How new downloads are named. Tokens: {title} {channel} {id} {date} {year}.</summary>
    public string NamePattern { get; set; } = "{title}";
    /// <summary>Prefix playlist downloads with their position ("01 - Title").</summary>
    public bool NumberPlaylistFiles { get; set; } = true;
    public bool KeepExtrasOnRedownload { get; set; } = true;
    /// <summary>How many downloads run at the same time (1–6).</summary>
    public int ParallelDownloads { get; set; } = 2;

    // Player / view
    public bool AutoSkipSponsors { get; set; } = true;
    public int Volume { get; set; } = 80;
    public string ViewMode { get; set; } = "grid";
    public string Sort { get; set; } = "newest";
    /// <summary>"mp4" (precise), "fast" (stream copy) or "audio".</summary>
    public string ClipKind { get; set; } = "mp4";
    /// <summary>"system" (follow Windows), "light" or "dark".</summary>
    public string Theme { get; set; } = "system";

    // Window
    public int[]? WindowBounds { get; set; }
    public bool WindowMaximized { get; set; }

    public static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

    /// <summary>Tools folder of the earlier "YouTube Offline Player" example - reused if it already has yt-dlp/ffmpeg.</summary>
    public static string LegacyToolsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YouTubeOfflinePlayer", "tools");

    private static string SettingsFile => Path.Combine(AppDataDir, "settings.json");

    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this, LibraryEntry.JsonOptions));
        }
        catch { /* settings are best-effort */ }
    }
}

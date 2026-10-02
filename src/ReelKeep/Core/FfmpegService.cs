using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace ReelKeep.Core;

/// <summary>Audio formats the app can save. M4A is the default (see <see cref="AudioFormats"/>).</summary>
public enum AudioFormat { M4A, MP3, FLAC, WAV }

public static class AudioFormats
{
    public const AudioFormat Default = AudioFormat.M4A;

    public static string Extension(AudioFormat f) => f switch
    {
        AudioFormat.MP3 => ".mp3",
        AudioFormat.FLAC => ".flac",
        AudioFormat.WAV => ".wav",
        _ => ".m4a",
    };

    public static string Describe(AudioFormat f) => f switch
    {
        AudioFormat.M4A => "M4A (AAC) – recommended: YouTube's own AAC audio copied bit-for-bit, no quality loss, small, tags + cover art, plays everywhere.",
        AudioFormat.MP3 => "MP3 320 kbps – maximum compatibility (old players, car stereos). Re-encoded, so a tiny generational loss.",
        AudioFormat.FLAC => "FLAC 16-bit – lossless container. The source is already lossy, so no quality gain vs M4A, just ~5x bigger. Good for editing software.",
        AudioFormat.WAV => "WAV 16-bit PCM – uncompressed, largest (~10 MB/min). For DAWs/editors that need raw audio. No cover art.",
        _ => "",
    };

    public static bool SupportsCover(AudioFormat f) => f != AudioFormat.WAV;

    public static AudioFormat Parse(string? s) =>
        Enum.TryParse<AudioFormat>(s, true, out var f) ? f : Default;
}

/// <summary>
/// Wrapper around ffmpeg / ffprobe. Used for: merging YouTube video+audio (via yt-dlp),
/// exporting audio (MP3/FLAC/M4A/WAV with tags and cover art) and cutting clips.
/// If ffmpeg isn't installed the app downloads a static build (yt-dlp's own FFmpeg-Builds) into its tools folder.
/// </summary>
public sealed class FfmpegService
{
    private const string DownloadUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

    public string ToolsDir { get; } = Path.Combine(AppSettings.AppDataDir, "tools");
    public string? FfmpegPath { get; private set; }
    public string? FfprobePath { get; private set; }
    public bool IsAvailable => FfmpegPath != null && FfprobePath != null;

    public void Locate()
    {
        FfmpegPath = Find("ffmpeg.exe");
        FfprobePath = Find("ffprobe.exe");
    }

    private string? Find(string exe)
    {
        foreach (var c in new[] { Path.Combine(ToolsDir, exe), Path.Combine(AppContext.BaseDirectory, exe), Path.Combine(AppSettings.LegacyToolsDir, exe) })
            if (File.Exists(c)) return c;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { var p = Path.Combine(dir.Trim('"'), exe); if (File.Exists(p)) return p; } catch { }
        }
        return null;
    }

    /// <summary>Download and unpack ffmpeg.exe + ffprobe.exe (~140 MB zip) into the tools folder.</summary>
    public async Task DownloadAsync(IProgress<(double Percent, string Text)> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(ToolsDir);
        var zipPath = Path.Combine(ToolsDir, "ffmpeg-download.zip");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ReelKeep/1.0");
            using var resp = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zipPath);
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            var lastReport = Stopwatch.StartNew();
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (lastReport.ElapsedMilliseconds > 200)
                {
                    lastReport.Restart();
                    progress.Report((total > 0 ? done * 100.0 / total : 0, $"Downloading ffmpeg… {done / 1048576} / {total / 1048576} MB"));
                }
            }
        }

        progress.Report((100, "Unpacking ffmpeg…"));
        await Task.Run(() =>
        {
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var entry in zip.Entries)
            {
                var name = entry.Name.ToLowerInvariant();
                if (name is "ffmpeg.exe" or "ffprobe.exe")
                    entry.ExtractToFile(Path.Combine(ToolsDir, name), overwrite: true);
            }
        }, ct);
        File.Delete(zipPath);
        Locate();
        if (!IsAvailable) throw new InvalidOperationException("ffmpeg download finished but ffmpeg.exe/ffprobe.exe were not found in the archive.");
    }

    /// <summary>Codec name of the first audio stream (e.g. "aac", "opus"), or null.</summary>
    public async Task<string?> ProbeAudioCodecAsync(string file, CancellationToken ct = default)
    {
        var (code, stdout, _) = await RunRawAsync(FfprobePath!, new[]
        {
            "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=codec_name", "-of", "default=nw=1:nk=1", file
        }, null, ct);
        var c = stdout.Trim();
        return code == 0 && c.Length > 0 ? c : null;
    }

    public sealed record Tags(string? Title, string? Artist, string? Date, string? Url, string? Description);

    /// <summary>
    /// Save the audio of <paramref name="source"/> (or of <paramref name="separateAudio"/> when the video's audio is a
    /// separate file) as <paramref name="format"/>, optionally only the <paramref name="start"/>–<paramref name="end"/> range.
    /// </summary>
    public async Task ExportAudioAsync(string source, string? separateAudio, string output, AudioFormat format,
        TimeSpan? start, TimeSpan? end, Tags tags, string? coverJpg,
        IProgress<double>? progress, CancellationToken ct)
    {
        var audioInput = separateAudio ?? source;
        var args = new List<string>();
        AddInput(args, audioInput, start, end);
        var hasCover = AudioFormats.SupportsCover(format) && coverJpg != null && File.Exists(coverJpg);
        if (hasCover) { args.Add("-i"); args.Add(coverJpg!); }

        args.AddRange(new[] { "-map", "0:a:0" });
        if (hasCover) args.AddRange(new[] { "-map", "1:v:0", "-c:v", "copy", "-disposition:v:0", "attached_pic" });

        switch (format)
        {
            case AudioFormat.M4A:
                // Copy AAC as-is (lossless); only re-encode when the source is e.g. Opus.
                var codec = await ProbeAudioCodecAsync(audioInput, ct);
                if (codec == "aac") args.AddRange(new[] { "-c:a", "copy" });
                else args.AddRange(new[] { "-c:a", "aac", "-b:a", "256k" });
                args.AddRange(new[] { "-movflags", "+faststart" });
                break;
            case AudioFormat.MP3:
                args.AddRange(new[] { "-c:a", "libmp3lame", "-b:a", "320k", "-id3v2_version", "3" });
                break;
            case AudioFormat.FLAC:
                args.AddRange(new[] { "-c:a", "flac", "-sample_fmt", "s16", "-compression_level", "8" });
                break;
            case AudioFormat.WAV:
                args.AddRange(new[] { "-c:a", "pcm_s16le" });
                break;
        }

        args.AddRange(new[] { "-map_metadata", "-1" });
        AddTag(args, "title", tags.Title);
        AddTag(args, "artist", tags.Artist);
        AddTag(args, "album_artist", tags.Artist);
        AddTag(args, "date", tags.Date);
        AddTag(args, "comment", tags.Url);
        if (format != AudioFormat.WAV) AddTag(args, "description", Truncate(tags.Description, 1000));
        args.Add(output);

        await RunAsync(args, Length(start, end, await DurationAsync(audioInput, ct)), progress, ct);
    }

    /// <summary>
    /// Cut a video clip. Precise = re-encode to H.264/AAC MP4 (frame-accurate, plays everywhere).
    /// Fast = stream copy into MP4 (instant, lossless, but starts on the nearest keyframe before the start point).
    /// </summary>
    public async Task ClipVideoAsync(string video, string? separateAudio, string output, TimeSpan start, TimeSpan end,
        bool precise, IProgress<double>? progress, CancellationToken ct)
    {
        var args = new List<string>();
        AddInput(args, video, start, end);
        if (separateAudio != null) AddInput(args, separateAudio, start, end);
        args.AddRange(new[] { "-map", "0:v:0", "-map", separateAudio != null ? "1:a:0" : "0:a:0?" });
        if (precise)
            args.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p",
                                  "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart" });
        else
            args.AddRange(new[] { "-c", "copy", "-avoid_negative_ts", "make_zero", "-movflags", "+faststart" });
        args.Add(output);
        await RunAsync(args, (end - start).TotalSeconds, progress, ct);
    }

    /// <summary>Width, height and frame rate of the first video stream (null if there's no video).</summary>
    public async Task<(int Width, int Height, double Fps)?> ProbeVideoAsync(string file, CancellationToken ct = default)
    {
        if (FfprobePath == null) return null;
        var (code, stdout, _) = await RunRawAsync(FfprobePath, new[]
        {
            "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height,avg_frame_rate", "-of", "csv=p=0", file
        }, null, ct);
        var parts = stdout.Trim().Split(',');
        if (code != 0 || parts.Length < 3 || !int.TryParse(parts[0], out var w) || !int.TryParse(parts[1], out var h)) return null;
        double fps = 0;
        var fr = parts[2].Split('/');
        if (fr.Length == 2 && double.TryParse(fr[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) &&
            double.TryParse(fr[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0) fps = n / d;
        return (w, h, fps);
    }

    public async Task<double> DurationAsync(string file, CancellationToken ct = default)
    {
        var (_, stdout, _) = await RunRawAsync(FfprobePath!, new[] { "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", file }, null, ct);
        return double.TryParse(stdout.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    // ------------------------------------------------------------------------------------------
    private static void AddInput(List<string> args, string file, TimeSpan? start, TimeSpan? end)
    {
        if (start is { } s && s > TimeSpan.Zero) { args.Add("-ss"); args.Add(Ts(s)); }
        if (end is { } e) { args.Add("-to"); args.Add(Ts(e)); }
        args.Add("-i"); args.Add(file);
    }

    private static double Length(TimeSpan? start, TimeSpan? end, double full) =>
        (end?.TotalSeconds ?? full) - (start?.TotalSeconds ?? 0);

    private static void AddTag(List<string> args, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        args.Add("-metadata");
        args.Add($"{key}={value}");
    }

    private static string? Truncate(string? s, int max) => s == null || s.Length <= max ? s : s[..max] + "…";

    private static string Ts(TimeSpan t) => t.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture);

    /// <summary>Run ffmpeg with progress reporting (0..100) based on the expected output duration.</summary>
    private async Task RunAsync(List<string> args, double totalSeconds, IProgress<double>? progress, CancellationToken ct)
    {
        var full = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-progress", "pipe:1", "-nostats" };
        full.AddRange(args);
        int code;
        string stderr;
        try
        {
            (code, _, stderr) = await RunRawAsync(FfmpegPath!, full, line =>
            {
                // "out_time_us=12345678" (microseconds; older builds call it out_time_ms but it's still µs)
                if ((line.StartsWith("out_time_us=") || line.StartsWith("out_time_ms=")) && totalSeconds > 0 &&
                    long.TryParse(line[(line.IndexOf('=') + 1)..], out var us))
                    progress?.Report(Math.Clamp(us / 1e6 / totalSeconds * 100, 0, 100));
            }, ct);
        }
        catch (OperationCanceledException)
        {
            await Task.Delay(300); // let the killed process release the file
            try { File.Delete(args[^1]); } catch { }
            throw;
        }
        if (code != 0)
        {
            try { File.Delete(args[^1]); } catch { }
            throw new InvalidOperationException("ffmpeg failed:\n" + string.Join("\n", stderr.Split('\n').Where(l => l.Trim().Length > 0).TakeLast(6)));
        }
        progress?.Report(100);
    }

    private static async Task<(int, string, string)> RunRawAsync(string exe, IEnumerable<string> args, Action<string>? onLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        var so = new StringBuilder();
        var se = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data == null) return; lock (so) so.AppendLine(e.Data); onLine?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data == null) return; lock (se) se.AppendLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        p.WaitForExit();
        return (p.ExitCode, so.ToString(), se.ToString());
    }
}

using System.Net;
using System.Text.Json;

namespace ReelKeep.Core;

/// <summary>
/// Looks up community-submitted "skip" segments (in-video sponsor reads, self-promotion, "like &amp; subscribe"
/// reminders, intros/outros) from the public SponsorBlock API: https://wiki.sponsor.ajay.app/w/API_Docs
/// The player then seeks past them automatically. (YouTube's own inserted ads are never part of the
/// downloaded file in the first place.)
/// </summary>
public static class SponsorBlockService
{
    public static readonly string[] Categories = { "sponsor", "selfpromo", "interaction", "intro", "outro", "preview", "music_offtopic" };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static async Task<List<SponsorSegment>> GetSegmentsAsync(string videoId, CancellationToken ct = default)
    {
        var cats = Uri.EscapeDataString(JsonSerializer.Serialize(Categories));
        var url = $"https://sponsor.ajay.app/api/skipSegments?videoID={Uri.EscapeDataString(videoId)}&categories={cats}";
        using var resp = await Http.GetAsync(url, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return new(); // no segments submitted for this video
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var list = new List<SponsorSegment>();
        foreach (var s in doc.RootElement.EnumerateArray())
        {
            if (s.TryGetProperty("actionType", out var at) && at.GetString() != "skip") continue;
            var seg = s.GetProperty("segment");
            list.Add(new SponsorSegment(seg[0].GetDouble(), seg[1].GetDouble(),
                s.GetProperty("category").GetString() ?? "", s.GetProperty("UUID").GetString() ?? ""));
        }
        return list.OrderBy(x => x.Start).ToList();
    }
}

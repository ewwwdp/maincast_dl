using System.Net;
using System.Net.Http.Json;

namespace MaincastDl.Core;

public sealed record Vod(string Name, string Date, string Link);
public sealed record VodInfo(string Name, string Date, string Host, string? MediaId);
internal sealed record Search(Vod[] Data);
internal sealed record ContentResponse(ContentItem? Item);
internal sealed record ContentItem(string Id, string Name, string? Date);
internal sealed record PlaybackResponse(string? Status, string? Src, string? Error);

public static class VodApi
{
    private const string LegacyApi = "https://a.maincast.tv/items/vod";
    private const string ContentApi = "https://api.maincast.tv/content/vods";
    private const string PlaybackApi = "https://maincast.tv/api/playback/vod";

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    // The legacy catalog only lists paid VODs. Free VODs are absent from it and
    // their stream is served through the playback API, which needs a signed-in
    // session cookie.
    public static async Task<VodInfo> ResolveAsync(string slug, string? cookie)
    {
        Vod? legacy = await TryFetchLegacyAsync(slug);
        if (legacy?.Link is { Length: > 0 } link)
        {
            string host = await ResolveHostAsync(link);
            return new VodInfo(legacy.Name, legacy.Date, host, MaincastId.ExtractMediaId(link));
        }

        ContentItem item = await TryFetchContentAsync(slug)
            ?? throw new Exception($"VOD not found for slug: {slug}");

        if (cookie is not { Length: > 0 })
            return new VodInfo(item.Name, item.Date ?? "", "", null);

        string src = await FetchPlaybackAsync(slug, cookie);
        return new VodInfo(item.Name, item.Date ?? "", await ResolveHostAsync(src),
            MaincastId.ExtractMediaId(src));
    }

    public static async Task<Vod?> TryFetchLegacyAsync(string slug)
    {
        Search? res = await Http.GetFromJsonAsync<Search>(
            $"{LegacyApi}?filter%5Bslug%5D%5B_eq%5D={Uri.EscapeDataString(slug)}&fields=name,date,link,is_free");
        return res?.Data is { Length: > 0 } data ? data[0] : null;
    }

    public static async Task<string> ResolveHostAsync(string playerUrl)
    {
        using HttpResponseMessage resp = await Http.GetAsync(playerUrl);
        return (resp.RequestMessage?.RequestUri ?? new Uri(playerUrl)).Host;
    }

    private static async Task<ContentItem?> TryFetchContentAsync(string slug)
    {
        try
        {
            ContentResponse? res = await Http.GetFromJsonAsync<ContentResponse>(
                $"{ContentApi}/{Uri.EscapeDataString(slug)}");
            return res?.Item;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> FetchPlaybackAsync(string slug, string cookie)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{PlaybackApi}/{Uri.EscapeDataString(slug)}");
        req.Headers.TryAddWithoutValidation("Cookie", NormalizeCookie(cookie));
        req.Headers.Referrer = new Uri($"https://maincast.tv/vod/{slug}");

        using HttpResponseMessage resp = await Http.SendAsync(req);
        PlaybackResponse? body = await resp.Content.ReadFromJsonAsync<PlaybackResponse>();
        if (body is { Status: "ready", Src.Length: > 0 })
            return body.Src;

        throw new Exception(
            $"Playback unavailable ({(int)resp.StatusCode} {body?.Error ?? resp.ReasonPhrase}) - check that the cookie is a valid Maincast session");
    }

    // Accepts the whole "Cookie" header, "name=value" pairs, or a bare session
    // token (the value of the mc_access cookie).
    private static string NormalizeCookie(string cookie)
    {
        string c = cookie.Trim();
        if (c.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
            c = c["Cookie:".Length..].Trim();
        return c.Contains('=') ? c : $"mc_access={c}";
    }
}

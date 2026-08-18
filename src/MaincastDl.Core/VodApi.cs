using System.Net;
using System.Net.Http.Json;

namespace MaincastDl.Core;

public sealed record Vod(string Name, string Date, string Link);
internal sealed record Search(Vod[] Data);

public static class VodApi
{
    private const string DirectusApi = "https://a.maincast.tv/items/vod";

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    public static async Task<Vod> FetchAsync(string slug)
    {
        var res = await Http.GetFromJsonAsync<Search>(
            $"{DirectusApi}?filter%5Bslug%5D%5B_eq%5D={Uri.EscapeDataString(slug)}&fields=name,date,link,is_free");
        return res?.Data is not { Length: > 0 } data ? throw new Exception($"VOD not found for slug: {slug}") : data[0];
    }

    public static async Task<string> ResolveHostAsync(string playerUrl)
    {
        using HttpResponseMessage resp = await Http.GetAsync(playerUrl);
        return (resp.RequestMessage?.RequestUri ?? new Uri(playerUrl)).Host;
    }
}

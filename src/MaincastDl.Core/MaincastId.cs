using System.Text.RegularExpressions;

namespace MaincastDl.Core;

public static class MaincastId
{
    public static string ResolveSlug(string arg)
    {
        string slug = Regex.Match(arg, "/vod/([^/?#]+)").Groups[1].Value;
        return slug.Length == 0 ? arg.Trim('/') : slug;
    }

    public static string ExtractMediaId(string link)
    {
        string mediaId = Regex.Match(link, "[?&]v=([0-9a-fA-F-]{36})").Groups[1].Value;
        return mediaId.Length == 0 ? throw new Exception($"No player link/media id in VOD record: {link}") : mediaId;
    }
}

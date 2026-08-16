using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MaincastDl;

internal static class Program
{
    private const string Usage = "usage: maincast_dl <maincast VOD URL or slug>... [--seconds N] [--reencode] [--progress] [--parallel N]";

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        ToolRunner.RegisterCancelHandlers();

        (Options opts, string[] vods) = Options.Parse(args);
        if (vods.Length == 0)
        {
            Console.Error.WriteLine(Usage);
            return 1;
        }

        int result = 0;
        for (int i = 0; i < vods.Length; i++)
        {
            if (i > 0)
                Console.WriteLine();
            try
            {
                result = await DownloadVodAsync(opts, vods[i]);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                result = 1;
            }
            if (ToolRunner.Cancelled)
                break;
        }
        return result;
    }

    private static async Task<int> DownloadVodAsync(Options opts, string arg)
    {
        string slug = ResolveSlug(arg);
        Vod vod = await VodApi.FetchAsync(slug);
        string mediaId = ExtractMediaId(vod.Link);

        Console.WriteLine($"Title: {vod.Name}");
        Console.WriteLine($"Date:  {vod.Date}");
        string host = await VodApi.ResolveHostAsync(vod.Link);
        Console.WriteLine($"Streaming host: {host}");

        await using var session = await SocketSession.ConnectAsync(host, mediaId);
        string hash = await session.GetHashAsync();

        string master = $"https://{host}/{mediaId}/master.m3u8?key={hash}";
        string outfile = Path.Combine(AppContext.BaseDirectory,
            $"{Output.FormatDate(vod.Date)}.{Output.SafeName(vod.Name ?? slug)}.mp4");
        Console.WriteLine("Output: " + outfile);

        Console.WriteLine("Downloading (parallel segments)...");
        int rc = await HlsDownloader.DownloadAsync(
            master, slug, outfile, opts, (sec, total) =>
            {
                Output.DrawProgress(sec, total);
                return Task.CompletedTask;
            });

        if (ToolRunner.Cancelled)
            return 130;
        if (rc != 0)
        {
            Console.WriteLine($"\nDownload failed (exit {rc}).");
            return rc;
        }
        if (!File.Exists(outfile) || new FileInfo(outfile).Length == 0)
        {
            Console.WriteLine("\nNo data downloaded.");
            return 1;
        }

        Console.WriteLine($"\nDone -> {outfile}");
        return 0;
    }

    private static string ResolveSlug(string arg)
    {
        string slug = Regex.Match(arg, "/vod/([^/?#]+)").Groups[1].Value;
        return slug.Length == 0 ? arg.Trim('/') : slug;
    }

    private static string ExtractMediaId(string link)
    {
        string mediaId = Regex.Match(link, "[?&]v=([0-9a-fA-F-]{36})").Groups[1].Value;
        if (mediaId.Length == 0)
            throw new Exception($"No player link/media id in VOD record: {link}");
        return mediaId;
    }
}

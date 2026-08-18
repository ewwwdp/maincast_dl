using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using MaincastDl.Core;

namespace MaincastDl;

internal static class Program
{
    private const string Usage = "usage: MaincastDl <maincast VOD URL or slug>... [--seconds N] [--reencode] [--progress] [--parallel N]";

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        ToolRunner.RegisterCancelHandlers();
        Console.WriteLine($"ffmpeg: {ToolRunner.NewProcess("ffmpeg").FileName}");

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

        await using SocketSession session = await SocketSession.ConnectAsync(host, mediaId);
        string hash = await session.GetHashAsync();

        string master = $"https://{host}/{mediaId}/master.m3u8?key={hash}";
        string outfile = Path.Combine(AppContext.BaseDirectory,
            $"{Output.FormatDate(vod.Date)}.{Output.SafeName(vod.Name ?? slug)}.mp4");
        Console.WriteLine("Output: " + outfile);

        Console.WriteLine("Downloading (parallel segments)...");
        int rc = await HlsDownloader.DownloadAsync(
            master, slug, outfile, opts, (phase, sec, total) =>
            {
                Output.DrawProgress(phase, sec, total);
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

    private static string ResolveSlug(string arg) => MaincastId.ResolveSlug(arg);

    private static string ExtractMediaId(string link) => MaincastId.ExtractMediaId(link);
}

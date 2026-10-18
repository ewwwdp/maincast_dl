using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using MaincastDl.Core;

namespace MaincastDl;

internal static class Program
{
    private const string Usage = "usage: MaincastDl <maincast VOD URL or slug>... [--seconds N] [--reencode] [--progress] [--parallel N] [--cookie <session>]";

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
        string slug = MaincastId.ResolveSlug(arg);
        VodInfo vod = await VodApi.ResolveAsync(slug, opts.Cookie);

        Console.WriteLine($"Title: {vod.Name}");
        Console.WriteLine($"Date:  {vod.Date}");
        Console.WriteLine($"Streaming host: {vod.Host}");

        if (vod.MediaId is not { Length: > 0 })
            throw new Exception("This VOD requires a Maincast login - pass your session cookie with --cookie or MAINCAST_COOKIE.");

        await using SocketSession session = await SocketSession.ConnectAsync(vod.Host, vod.MediaId);
        string hash = await session.GetHashAsync();

        string master = $"https://{vod.Host}/{vod.MediaId}/master.m3u8?key={hash}";
        string outfile = Path.Combine(AppContext.BaseDirectory,
            $"{Output.FormatDate(vod.Date)}.{Output.SafeName(vod.Name)}.mp4");
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
}

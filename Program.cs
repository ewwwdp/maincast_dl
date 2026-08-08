using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MaincastDl;

internal static class Program
{
    private const string Usage = "usage: maincast_dl <maincast VOD URL or slug>... [--seconds N] [--reencode] [--progress]";

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

        string outfile = Path.Combine(AppContext.BaseDirectory,
            $"{Output.FormatDate(vod.Date)}.{Output.SafeName(vod.Name ?? slug)}.mp4");
        string partFile = outfile + ".part.ts";
        Console.WriteLine("Output: " + outfile);

        string master = $"https://{host}/{mediaId}/master.m3u8?key={hash}";
        string headers = $"Referer: https://{host}/player?v={mediaId}\r\nOrigin: https://{host}\r\nAccept: */*\r\n";

        double start = await FfmpegTools.ProbeDurationAsync(partFile) ?? 0;
        if (start > 0)
            Console.WriteLine($"Resuming from {Output.FormatTime(start)} (partial file)");

        double? total = opts.Seconds ?? await FfmpegTools.ProbeDurationAsync(master, headers);
        if (total is not null)
            Console.WriteLine($"Duration: {Output.FormatTime(total.Value)}");

        double? remaining = total is not null ? Math.Max(0, total.Value - start) : null;

        int rc = 0;
        if (remaining is not null && remaining.Value <= 0)
        {
            Console.WriteLine("Already fully downloaded, remuxing...");
        }
        else
        {
            ProcessStartInfo psi = FfmpegTools.BuildDownloadCommand(opts, master, headers, start, remaining);
            Console.WriteLine($"ffmpeg: {string.Join(' ', psi.ArgumentList.Take(8))} ...");
            rc = await FfmpegTools.RunDownloadAsync(psi, partFile, start > 0 && File.Exists(partFile), start, total);
        }

        return await Finish(rc, partFile, outfile);
    }

    private static async Task<int> Finish(int rc, string partFile, string outfile)
    {
        if (ToolRunner.Cancelled)
        {
            Console.WriteLine($"\nInterrupted - partial saved. Re-run to resume from {partFile}");
            return 130;
        }
        if (rc != 0)
        {
            Console.WriteLine($"\nffmpeg failed (exit {rc}) - partial saved. Re-run to resume from {partFile}");
            return rc;
        }
        if (!File.Exists(partFile) || new FileInfo(partFile).Length == 0)
        {
            Console.WriteLine("\nNo data downloaded.");
            return 1;
        }

        Console.WriteLine("Remuxing to MP4...");
        int rr = await FfmpegTools.RemuxAsync(partFile, outfile);
        if (rr == 0 && File.Exists(outfile))
        {
            File.Delete(partFile);
            Console.WriteLine($"\nDone -> {outfile}");
            return 0;
        }
        Console.WriteLine($"\nRemux failed (exit {rr}) - raw TS kept at {partFile}");
        return rr;
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

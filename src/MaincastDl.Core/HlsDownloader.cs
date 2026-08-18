using System.Globalization;
using System.Security.Cryptography;

namespace MaincastDl.Core;

public static class HlsDownloader
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private sealed record Segment(string Url, double Duration);
    private sealed record Track(List<Segment> Segments, byte[] Key, byte[] Iv);

    public static async Task<int> DownloadAsync(string master, string slug,
        string outfile, Options opts, Func<string, double, double?, Task> reportProgress)
    {
        string dir = Path.Combine(Path.GetDirectoryName(outfile) ?? AppContext.BaseDirectory, "maincast-" + slug);
        Directory.CreateDirectory(dir);

        (Track video, Track audio) = await FetchTracksAsync(master);

        int count = Math.Min(video.Segments.Count, audio.Segments.Count);
        if (opts.Seconds is { } seconds)
            count = Math.Min(count, SegmentsFor(video.Segments, seconds));
        double totalSec = video.Segments.Take(count).Sum(s => s.Duration);

        int resumeFrom = ResumeIndex(dir, count);
        if (resumeFrom > 0)
            Console.WriteLine($"Resuming from segment {resumeFrom}/{count}");
        else
            Console.WriteLine($"Segments: {count} (parallel)");

        try
        {
            for (int i = resumeFrom; i < count; i += opts.Parallel)
            {
                if (ToolRunner.Cancelled)
                    return 130;

                int batch = Math.Min(opts.Parallel, count - i);
                await Task.WhenAll(Enumerable.Range(0, batch).Select(async j =>
                {
                    int idx = i + j;
                    Segment v = video.Segments[idx];
                    Segment a = audio.Segments[idx];
                    await Task.WhenAll(
                        SaveSegmentAsync(v.Url, Path.Combine(dir, $"v{idx:0000}.ts"), video.Key, video.Iv),
                        SaveSegmentAsync(a.Url, Path.Combine(dir, $"a{idx:0000}.ts"), audio.Key, audio.Iv));
                }));

                double done = video.Segments.Take(Math.Min(i + batch, count)).Sum(s => s.Duration);
                await reportProgress("Downloading", done, totalSec);
            }

            int rc = await MuxAsync(dir, count, outfile, opts.Reencode, totalSec,
                (sec, total) => reportProgress("Muxing", sec, total));
            if (rc != 0)
                return rc;
            try { Directory.Delete(dir, true); }
            catch { }
            return rc;
        }
        finally
        {
            if (ToolRunner.Cancelled)
                Console.WriteLine("\nInterrupted - partial segments kept. Re-run to resume.");
        }
    }

    private static async Task<(Track Video, Track Audio)> FetchTracksAsync(string master)
    {
        string masterText = await Http.GetStringAsync(master);
        string[] lines = masterText.Split('\n');

        string? videoUri = null, audioUri = null;
        long bestBw = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal))
                continue;
            string? uri = i + 1 < lines.Length ? lines[i + 1].Trim() : null;
            long bw = ParseBandwidth(lines[i]);
            if (uri is { Length: > 0 } && bw > bestBw)
            {
                bestBw = bw;
                videoUri = uri;
            }
        }
        foreach (string line in lines)
        {
            if (line.StartsWith("#EXT-X-MEDIA", StringComparison.Ordinal) &&
                line.Contains("TYPE=AUDIO", StringComparison.Ordinal) &&
                line.Contains("DEFAULT=YES", StringComparison.Ordinal))
            {
                audioUri = line.Split("URI=\"")[1].Split('"')[0];
                break;
            }
        }

        if (videoUri is null || audioUri is null)
            throw new Exception("No playable video/audio variant found");

        string baseUrl = master[..(master.LastIndexOf('/') + 1)];
        Track video = await FetchTrackAsync(baseUrl + videoUri);
        Track audio = await FetchTrackAsync(baseUrl + audioUri);
        return (video, audio);
    }

    private static async Task<Track> FetchTrackAsync(string playlistUrl)
    {
        string text = await Http.GetStringAsync(playlistUrl);

        string? keyUri = null, ivHex = null;
        var segments = new List<Segment>();
        double duration = 0;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("#EXT-X-KEY", StringComparison.Ordinal))
            {
                keyUri = ExtractAttr(line, "URI");
                ivHex = ExtractAttr(line, "IV");
            }
            else if (line.StartsWith("#EXTINF", StringComparison.Ordinal))
            {
                duration = double.TryParse(line.Split(':')[1].Split(',')[0],
                        NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                    ? d : 0;
            }
            else if (line.Length > 0 && !line.StartsWith('#'))
            {
                segments.Add(new Segment(line, duration));
            }
        }

        byte[] key = await Http.GetByteArrayAsync(keyUri ?? throw new Exception("No encryption key in playlist"));
        byte[] iv = ivHex is { } h && h.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.FromHexString(h[2..])
            : new byte[16];
        return new Track(segments, key, iv);
    }

    private static async Task SaveSegmentAsync(string url, string path, byte[] key, byte[] iv)
    {
        if (File.Exists(path) && new FileInfo(path).Length > 0)
            return;
        byte[] enc = await Http.GetByteArrayAsync(url);
        await File.WriteAllBytesAsync(path, Decrypt(enc, key, iv));
    }

    private static int ResumeIndex(string dir, int count)
    {
        for (int i = 0; i < count; i++)
        {
            string vf = Path.Combine(dir, $"v{i:0000}.ts");
            string af = Path.Combine(dir, $"a{i:0000}.ts");
            if (!File.Exists(vf) || !File.Exists(af) ||
                new FileInfo(vf).Length == 0 || new FileInfo(af).Length == 0)
                return i;
        }
        return count;
    }

    private static byte[] Decrypt(byte[] data, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using ICryptoTransform dec = aes.CreateDecryptor();
        return dec.TransformFinalBlock(data, 0, data.Length);
    }

    private static async Task<int> MuxAsync(string dir, int count, string outfile, bool reencode,
        double totalSec, Func<double, double?, Task> report)
    {
        int chunkSize = ChunkSizeFor(dir);
        string video = await ConcatChunksAsync(dir, 'v', count, chunkSize, report, totalSec, 0, 2);
        string audio = await ConcatChunksAsync(dir, 'a', count, chunkSize, report, totalSec, 1, 2);

        var psi = ToolRunner.NewProcess("ffmpeg");
        ToolRunner.AddArgs(psi, "-hide_banner", "-loglevel", "error", "-nostats", "-y",
            "-progress", "pipe:2",
            "-i", "concat:" + video, "-i", "concat:" + audio,
            "-c", "copy", "-bsf:a", "aac_adtstoasc",
            "-movflags", "+faststart",
            "-fflags", "+genpts");
        if (reencode)
            ToolRunner.AddArgs(psi, "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-c:a", "aac");
        ToolRunner.AddArgs(psi, outfile);

        return await ToolRunner.RunAsync(psi, proc => Task.WhenAll(
            Output.DrainAsync(proc.StandardOutput),
            ReadMuxProgress(proc.StandardError, totalSec, report)));
    }

    private static int ChunkSizeFor(string dir)
    {
        string sample = Path.Combine(dir, "v0000.ts");
        return Math.Clamp(30000 / (sample.Length + 1), 50, 500);
    }

    private static async Task<string> ConcatChunksAsync(string dir, char tag, int count, int chunkSize,
        Func<double, double?, Task> report, double totalSec, int runIndex, int totalRuns)
    {
        int chunkCount = (count + chunkSize - 1) / chunkSize;
        var chunks = new List<string>();
        for (int start = 0; start < count; start += chunkSize)
        {
            int end = Math.Min(start + chunkSize, count);
            string input = "concat:" + string.Join('|',
                Enumerable.Range(start, end - start).Select(i => Path.Combine(dir, $"{tag}{i:0000}.ts")));
            string chunkFile = Path.Combine(dir, $"{tag}c{start:0000}.ts");
            var psi = ToolRunner.NewProcess("ffmpeg");
            ToolRunner.AddArgs(psi, "-hide_banner", "-loglevel", "error", "-y",
                "-i", input, "-c", "copy", "-fflags", "+genpts", chunkFile);
            int rc = await ToolRunner.RunAsync(psi, proc => Task.WhenAll(
                Output.DrainAsync(proc.StandardOutput),
                Output.DrainAsync(proc.StandardError)));
            if (rc != 0)
                throw new Exception($"Failed to concat chunk {start}");
            chunks.Add(chunkFile);

            int done = start / chunkSize + 1;
            double frac = (double)(runIndex * chunkCount + done) / (totalRuns * chunkCount);
            await report(0.5 * frac * totalSec, totalSec);
        }
        return string.Join('|', chunks);
    }

    private static async Task ReadMuxProgress(StreamReader reader, double totalSec,
        Func<double, double?, Task> report)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal) ||
                line.StartsWith("out_time_ms=", StringComparison.Ordinal))
            {
                if (long.TryParse(line[(line.IndexOf('=') + 1)..], out long us))
                {
                    double frac = Math.Clamp(us / 1e6 / Math.Max(totalSec, 1), 0, 1);
                    await report(0.5 * totalSec + 0.5 * frac * totalSec, totalSec);
                }
            }
            else if (line.Length > 0 && !LooksLikeProgress(line))
            {
                Console.WriteLine(line);
            }
        }
    }

    private static bool LooksLikeProgress(string line)
    {
        int eq = line.IndexOf('=');
        if (eq <= 0)
            return false;
        for (int i = 0; i < eq; i++)
            if (!char.IsLetterOrDigit(line[i]) && line[i] != '_')
                return false;
        return true;
    }

    private static int SegmentsFor(List<Segment> segments, double seconds)
    {
        double acc = 0;
        int n = 0;
        foreach (Segment s in segments)
        {
            if (acc + s.Duration > seconds && n > 0)
                break;
            acc += s.Duration;
            n++;
        }
        return Math.Max(1, n);
    }

    private static long ParseBandwidth(string inf)
    {
        string? bw = ExtractAttr(inf, "BANDWIDTH");
        return long.TryParse(bw, out long v) ? v : -1;
    }

    private static string? ExtractAttr(string line, string name)
    {
        int idx = line.IndexOf(name + "=", StringComparison.Ordinal);
        if (idx < 0) return null;
        string rest = line[(idx + name.Length + 1)..];
        return rest.StartsWith('"') ? rest[1..].Split('"')[0] : rest.Split(',')[0];
    }
}

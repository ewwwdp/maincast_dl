using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MaincastDl
{
    internal static class Program
    {
        private const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36";
        private const string Origin = "https://vod-maincast.cosmonova-broadcast.tv";
        private const string Directus = "https://a.maincast.tv/items/vod";

        private static readonly HttpClient Http = new(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        private record Vod(string Name, string Date, string Link);
        private record Search(Vod[] Data);

        private static volatile bool _cancelled;
        private static Process? _proc;
        private static readonly object ProcLock = new();

        private static async Task<int> Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; Cancel(); };
            try { PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; Cancel(); }); }
            catch { }
            if (args.Length == 0)
            {
                Console.Error.WriteLine("usage: maincast_dl <maincast VOD URL or slug> [--seconds N] [--reencode] [--progress]");
                return 1;
            }

            double? limit = null;
            int sec = Array.IndexOf(args, "--seconds");
            if (sec >= 0 && sec + 1 < args.Length)
                limit = double.Parse(args[sec + 1], CultureInfo.InvariantCulture);
            bool reencode = args.Contains("--reencode");

            try
            {
                string slug = Regex.Match(args[0], "/vod/([^/?#]+)").Groups[1].Value;
                if (slug.Length == 0) slug = args[0].Trim('/');
                Vod vod = await FetchVod(slug);
                string link = vod.Link ?? "";
                string mediaId = Regex.Match(link, "[?&]v=([0-9a-fA-F-]{36})").Groups[1].Value;
                if (mediaId.Length == 0)
                {
                    Console.Error.WriteLine($"No player link/media id in VOD record: {link}");
                    return 1;
                }

                Console.WriteLine($"Title: {vod.Name}");
                Console.WriteLine($"Date:  {vod.Date}");
                string host = await ResolveHost(link);
                Console.WriteLine($"Streaming host: {host}");

                using var ws = new ClientWebSocket();
                ws.Options.SetRequestHeader("Origin", Origin);
                await ws.ConnectAsync(new Uri($"wss://{host}/ws"), CancellationToken.None);
                string hash = await GetHash(ws);

                using var cts = new CancellationTokenSource();
                Task hb = Heartbeat(ws, mediaId, cts.Token);

                string outfile = Path.Combine(AppContext.BaseDirectory, SafeName(vod.Name ?? slug) + ".mp4");
                string partFile = outfile + ".part.ts";
                Console.WriteLine("Output: " + outfile);

                string master = $"https://{host}/{mediaId}/master.m3u8?key={hash}";
                string headers = $"Referer: https://{host}/player?v={mediaId}\r\nOrigin: https://{host}\r\nAccept: */*\r\n";

                double start = await ProbeDuration(partFile) ?? 0;
                if (start > 0)
                    Console.WriteLine($"Resuming from {FormatTime(start)} (partial file)");

                double? total = limit ?? await ProbeDuration(master, headers);
                if (total is not null)
                    Console.WriteLine($"Duration: {FormatTime(total.Value)}");

                double? remaining = total is not null ? Math.Max(0, total.Value - start) : null;

                int rc = 0;
                if (remaining is not null && remaining.Value <= 0)
                {
                    Console.WriteLine("Already fully downloaded, remuxing...");
                }
                else
                {
                    var psi = new ProcessStartInfo("ffmpeg")
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    Add(psi, "-hide_banner", "-loglevel", args.Contains("--progress") ? "info" : "error",
                        "-nostats", "-progress", "pipe:2",
                        "-y", "-user_agent", UA,
                        "-headers", headers);
                    if (start > 0)
                        Add(psi, "-ss", start.ToString(CultureInfo.InvariantCulture));
                    Add(psi, "-i", master);
                    if (remaining is not null)
                        Add(psi, "-t", remaining.Value.ToString(CultureInfo.InvariantCulture));
                    if (reencode)
                        Add(psi, "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-c:a", "aac");
                    else
                        Add(psi, "-c", "copy");
                    Add(psi, "-f", "mpegts", "pipe:1");

                    Console.WriteLine($"ffmpeg: {string.Join(' ', psi.ArgumentList.Take(8))} ...");

                    using Process proc = Process.Start(psi) ?? throw new Exception("ffmpeg not found in PATH");
                    lock (ProcLock) _proc = proc;
                    bool append = start > 0 && File.Exists(partFile);
                    Task stream = CopyStream(proc.StandardOutput.BaseStream, partFile, append);
                    Task errors = ReadStderr(proc.StandardError, start, total);
                    await proc.WaitForExitAsync();
                    rc = proc.ExitCode;
                    await Task.WhenAll(stream, errors);
                    lock (ProcLock) _proc = null;
                }

                cts.Cancel();
                try { await hb; } catch { }
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);

                if (_cancelled)
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
                int rr = await Remux(partFile, outfile);
                if (rr == 0 && File.Exists(outfile))
                {
                    File.Delete(partFile);
                    Console.WriteLine($"\nDone -> {outfile}");
                    return 0;
                }
                Console.WriteLine($"\nRemux failed (exit {rr}) - raw TS kept at {partFile}");
                return rr;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 1;
            }
        }

        private static void Cancel()
        {
            _cancelled = true;
            lock (ProcLock)
            {
                try { _proc?.Kill(true); } catch { }
            }
        }

        private static async Task<Vod> FetchVod(string slug)
        {
            Search? res = await Http.GetFromJsonAsync<Search>(
                $"{Directus}?filter%5Bslug%5D%5B_eq%5D={Uri.EscapeDataString(slug)}&fields=name,date,link,is_free");
            if (res?.Data is not { Length: > 0 } data)
                throw new Exception($"VOD not found for slug: {slug}");
            return data[0];
        }

        private static async Task<string> ResolveHost(string playerUrl)
        {
            using HttpResponseMessage resp = await Http.GetAsync(playerUrl);
            return (resp.RequestMessage?.RequestUri ?? new Uri(playerUrl)).Host;
        }

        private static async Task<double?> ProbeDuration(string input, string headers = "")
        {
            try
            {
                var ppi = new ProcessStartInfo("ffprobe")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                Add(ppi, "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1");
                if (headers.Length > 0)
                    Add(ppi, "-user_agent", UA, "-headers", headers);
                Add(ppi, "-i", input);
                using Process p = Process.Start(ppi) ?? throw new Exception("ffprobe not found in PATH");
                string outp = await p.StandardOutput.ReadToEndAsync();
                await p.WaitForExitAsync();
                return double.TryParse(outp.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                    ? d : null;
            }
            catch { return null; }
        }

        private static async Task ReadStderr(StreamReader reader, double start, double? total)
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
            {
                if (line.StartsWith("out_time_ms=", StringComparison.Ordinal) &&
                    long.TryParse(line[(line.IndexOf('=') + 1)..], out long us))
                    DrawProgress(start + us / 1e6, total);
                else if (line == "progress=end")
                {
                    Console.Write(new string(' ', 80) + "\r");
                }
                else if (line.Length > 0 && !IsProgressLine(line))
                    Console.WriteLine(line);
            }
        }

        private static async Task CopyStream(Stream src, string path, bool append)
        {
            await using var fs = new FileStream(path, append ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.None);
            byte[] buf = new byte[1 << 20];
            int n;
            while ((n = await src.ReadAsync(buf)) > 0)
                await fs.WriteAsync(buf.AsMemory(0, n));
        }

        private static async Task<int> Remux(string part, string outfile)
        {
            try
            {
                var psi = new ProcessStartInfo("ffmpeg")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                Add(psi, "-hide_banner", "-loglevel", "error", "-y", "-i", part,
                    "-c", "copy", "-movflags", "+faststart", outfile);
                using Process p = Process.Start(psi) ?? throw new Exception("ffmpeg not found in PATH");
                lock (ProcLock) _proc = p;
                Task o = Copy(p.StandardOutput);
                Task e = Copy(p.StandardError);
                await p.WaitForExitAsync();
                int rc = p.ExitCode;
                await Task.WhenAll(o, e);
                lock (ProcLock) _proc = null;
                return rc;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Remux: " + ex.Message);
                return 1;
            }
        }

        private static bool IsProgressLine(string line)
        {
            int eq = line.IndexOf('=');
            if (eq <= 0 || !(char.IsLetter(line[0]) || line[0] == '_'))
                return false;
            for (int i = 0; i < eq; i++)
                if (!char.IsLetterOrDigit(line[i]) && line[i] != '_' && line[i] != '.')
                    return false;
            return true;
        }

        private static void DrawProgress(double sec, double? total)
        {
            int pct = total is > 0 ? (int)Math.Clamp(sec / total.Value * 100, 0, 100) : -1;
            Console.Write(pct >= 0
                ? $"\rProgress: {pct,3}%  {FormatTime(sec)} / {FormatTime(total!.Value)}"
                : $"\rDownloaded: {FormatTime(sec)}");
        }

        private static string FormatTime(double sec)
        {
            TimeSpan t = TimeSpan.FromSeconds(Math.Max(0, sec));
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
        }

        private static async Task<string> GetHash(ClientWebSocket ws)
        {
            var buf = new StringBuilder();
            byte[] chunk = new byte[65536];
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(new ArraySegment<byte>(chunk), CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(15));
                buf.Append(Encoding.UTF8.GetString(chunk, 0, r.Count));
            } while (!r.EndOfMessage);

            JsonElement payload = JsonDocument.Parse(buf.ToString()).RootElement.GetProperty("payload");
            return payload.ValueKind == JsonValueKind.String
                ? JsonDocument.Parse(payload.GetString()!).RootElement.GetProperty("hash").GetString()!
                : payload.GetProperty("hash").GetString()!;
        }

        private static async Task Heartbeat(ClientWebSocket ws, string mediaId, CancellationToken token)
        {
            byte[] msg = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                type = "currentTime",
                payload = new { profile_id = "", media_id = mediaId, time_stamp = 0.0 },
            }));
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await ws.SendAsync(msg, WebSocketMessageType.Text, true, token);
                    await Task.Delay(TimeSpan.FromSeconds(30), token);
                }
            }
            catch { }
        }

        private static async Task Copy(StreamReader reader)
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
                Console.WriteLine(line);
        }

        private static void Add(ProcessStartInfo psi, params string[] args)
            => Array.ForEach(args, psi.ArgumentList.Add);

        private static string SafeName(string name)
        {
            name = Regex.Replace(name, @"[\\/:*?""<>|]+", "_").Trim().Trim('.');
            return name.Length == 0 ? "video" : name;
        }
    }
}

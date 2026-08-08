using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;

namespace MaincastDl;

internal static class FfmpegTools
{
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36";

    public static async Task<double?> ProbeDurationAsync(string input, string headers = "")
    {
        try
        {
            ProcessStartInfo ppi = ToolRunner.NewProcess("ffprobe");
            ToolRunner.AddArgs(ppi, "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1");
            if (headers.Length > 0)
                ToolRunner.AddArgs(ppi, "-user_agent", UserAgent, "-headers", headers);
            ToolRunner.AddArgs(ppi, "-i", input);

            using Process p = Process.Start(ppi) ?? throw new Exception("ffprobe not found in PATH");
            string outp = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return double.TryParse(outp.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                ? d : null;
        }
        catch { return null; }
    }

    public static ProcessStartInfo BuildDownloadCommand(Options opts, string master, string headers,
        double start, double? remaining)
    {
        ProcessStartInfo psi = ToolRunner.NewProcess("ffmpeg");
        ToolRunner.AddArgs(psi, "-hide_banner", "-loglevel", opts.Progress ? "info" : "error",
            "-nostats", "-progress", "pipe:2",
            "-y", "-user_agent", UserAgent,
            "-headers", headers);
        if (start > 0)
            ToolRunner.AddArgs(psi, "-ss", start.ToString(CultureInfo.InvariantCulture));
        ToolRunner.AddArgs(psi, "-i", master);
        if (remaining is not null)
            ToolRunner.AddArgs(psi, "-t", remaining.Value.ToString(CultureInfo.InvariantCulture));
        if (opts.Reencode)
            ToolRunner.AddArgs(psi, "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-c:a", "aac");
        else
            ToolRunner.AddArgs(psi, "-c", "copy");
        ToolRunner.AddArgs(psi, "-f", "mpegts", "pipe:1");
        return psi;
    }

    public static Task<int> RunDownloadAsync(ProcessStartInfo psi, string partFile, bool append,
        double start, double? total)
    {
        return ToolRunner.RunAsync(psi, proc => Task.WhenAll(
            Output.CopyStream(proc.StandardOutput.BaseStream, partFile, append),
            Output.ReadStderr(proc.StandardError, start, total)));
    }

    public static Task<int> RemuxAsync(string part, string outfile)
    {
        try
        {
            ProcessStartInfo psi = ToolRunner.NewProcess("ffmpeg");
            ToolRunner.AddArgs(psi, "-hide_banner", "-loglevel", "error", "-y", "-i", part,
                "-c", "copy", "-movflags", "+faststart", outfile);
            return ToolRunner.RunAsync(psi, proc => Task.WhenAll(
                Output.DrainAsync(proc.StandardOutput),
                Output.DrainAsync(proc.StandardError)));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Remux: " + ex.Message);
            return Task.FromResult(1);
        }
    }
}

using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MaincastDl;

internal static class Output
{
    public static async Task ReadStderr(StreamReader reader, double start, double? total)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            if (line.StartsWith("out_time_ms=", StringComparison.Ordinal) &&
                long.TryParse(line[(line.IndexOf('=') + 1)..], out long us))
                DrawProgress(start + us / 1e6, total);
            else if (line == "progress=end")
                Console.Write(new string(' ', 80) + "\r");
            else if (line.Length > 0 && !IsProgressLine(line))
                Console.WriteLine(line);
        }
    }

    public static async Task CopyStream(Stream src, string path, bool append)
    {
        await using var fs = new FileStream(path, append ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.None);
        byte[] buf = new byte[1 << 20];
        int n;
        while ((n = await src.ReadAsync(buf)) > 0)
            await fs.WriteAsync(buf.AsMemory(0, n));
    }

    public static async Task DrainAsync(StreamReader reader)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
            Console.WriteLine(line);
    }

    public static string SafeName(string name)
    {
        name = Regex.Replace(name, @"[\\/:*?""<>|]+", "_").Trim().Trim('.');
        return name.Length == 0 ? "video" : name;
    }

    public static string FormatDate(string date)
    {
        return DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset d)
            ? d.ToString("dd-MM")
            : SafeName(date);
    }

    public static string FormatTime(double sec)
    {
        TimeSpan t = TimeSpan.FromSeconds(Math.Max(0, sec));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
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
}

using System.Globalization;
using System.Text.RegularExpressions;

namespace MaincastDl.Core;

public static class Output
{
    public static async Task DrainAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
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

    public static void DrawProgress(string phase, double sec, double? total)
    {
        int pct = total is > 0 ? (int)Math.Clamp(sec / total.Value * 100, 0, 100) : -1;
        Console.Write(pct >= 0
            ? $"\r{phase}: {pct,3}%  {FormatTime(sec)} / {FormatTime(total!.Value)}"
            : $"\r{phase}: {FormatTime(sec)}");
    }
}

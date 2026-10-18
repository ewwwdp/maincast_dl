using System.Globalization;

namespace MaincastDl.Core;

public sealed record Options(double? Seconds, bool Reencode, bool Progress, int Parallel, string? Cookie)
{
    public static (Options Options, string[] Vods) Parse(string[] args)
    {
        double? seconds = null;
        bool reencode = false;
        bool progress = false;
        int parallel = 8;
        string? cookie = Environment.GetEnvironmentVariable("MAINCAST_COOKIE");
        var vods = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--seconds" when i + 1 < args.Length:
                    seconds = double.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--seconds":
                    break;
                case "--reencode":
                    reencode = true;
                    break;
                case "--progress":
                    progress = true;
                    break;
                case "--parallel" when i + 1 < args.Length:
                    parallel = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--parallel":
                    break;
                case "--cookie" when i + 1 < args.Length:
                    cookie = args[++i];
                    break;
                case "--cookie":
                    break;
                default:
                    vods.Add(args[i]);
                    break;
            }
        }

        return (new Options(seconds, reencode, progress, parallel, cookie), vods.ToArray());
    }
}

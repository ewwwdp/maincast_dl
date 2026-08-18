using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MaincastDl.Core;

public static class ToolRunner
{
    private static volatile bool _cancelled;
    private static Process? _current;
    private static readonly Lock Gate = new();

    public static bool Cancelled
    {
        get => _cancelled;
    }

    public static void RegisterCancelHandlers()
    {
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Cancel(); };
        try { PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; Cancel(); }); }
        catch { }
    }

    public static ProcessStartInfo NewProcess(string tool) => new(ResolveTool(tool))
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };

    public static void AddArgs(ProcessStartInfo psi, params string[] args)
        => Array.ForEach(args, psi.ArgumentList.Add);

    public static async Task<int> RunAsync(ProcessStartInfo psi, Func<Process, Task>? reader = null)
    {
        using Process p = Process.Start(psi) ?? throw new Exception($"{psi.FileName} not found in PATH");
        lock (Gate) _current = p;
        try
        {
            Task? draining = reader?.Invoke(p);
            await p.WaitForExitAsync();
            int rc = p.ExitCode;
            if (draining is not null)
                await draining;
            return rc;
        }
        finally
        {
            lock (Gate) _current = null;
        }
    }

    public static void Cancel()
    {
        _cancelled = true;
        lock (Gate)
        {
            try { _current?.Kill(true); }
            catch { }
        }
    }

    private static string ResolveTool(string tool)
    {
        string exe = tool + (OperatingSystem.IsWindows() ? ".exe" : "");
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "ffmpeg", exe),
            Path.Combine(AppContext.BaseDirectory, exe),
            tool
        ];
        return candidates.FirstOrDefault(File.Exists) ?? tool;
    }
}

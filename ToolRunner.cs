using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace MaincastDl;

internal static class ToolRunner
{
    private static volatile bool _cancelled;
    private static Process? _current;
    private static readonly object Gate = new();

    public static bool Cancelled => _cancelled;

    public static void RegisterCancelHandlers()
    {
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Cancel(); };
        try { PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; Cancel(); }); }
        catch { }
    }

    public static ProcessStartInfo NewProcess(string tool) => new(tool)
    {
        UseShellExecute = false,
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

    private static void Cancel()
    {
        _cancelled = true;
        lock (Gate)
        {
            try { _current?.Kill(true); } catch { }
        }
    }
}

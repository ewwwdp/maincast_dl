using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MaincastDl.Core;

namespace MaincastDl.Desktop;

public partial class MainWindow : Window
{
    private Task? _task;
    private CancellationTokenSource? _debounce;
    private string? _lastFetched;

    private sealed record DownloadInput(string Arg, double? Seconds, bool Reencode, int Parallel);

    public MainWindow()
    {
        InitializeComponent();
        Log("Ready. Paste a Maincast VOD link and press Enter (or Download).");
        Log("ffmpeg: " + ToolRunner.NewProcess("ffmpeg").FileName);
    }

    private void OnFetch(object? sender, RoutedEventArgs e) => StartBackground(FetchAsync);

    private void OnDownload(object? sender, RoutedEventArgs e) => StartBackground(DownloadAsync);

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        ToolRunner.Cancel();
        Log("Cancelling...");
    }

    private async void OnPaste(object? sender, RoutedEventArgs e)
    {
        string? text = await GetClipboardTextAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            Log("Clipboard is empty.");
            return;
        }
        VodInput.Text = text.Trim();
    }

    private async void OnVodTextChanged(object? sender, TextChangedEventArgs e)
    {
        _debounce?.Cancel();
        CancellationTokenSource cts = _debounce = new CancellationTokenSource();
        try
        {
            await Task.Delay(600, cts.Token);
            if (!cts.IsCancellationRequested)
                AutoFetchInfo();
        }
        catch (TaskCanceledException) { }
    }

    private void OnVodKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            OnDownload(sender, e);
    }

    private void AutoFetchInfo()
    {
        if (_task is { IsCompleted: false })
            return;
        string arg = VodInput.Text?.Trim() ?? "";
        if (arg.Length == 0)
            return;
        string slug = MaincastId.ResolveSlug(arg);
        if (slug == _lastFetched)
            return;
        _lastFetched = slug;
        StartBackground(FetchAsync);
    }

    private async Task<string?> GetClipboardTextAsync()
    {
        try
        {
            IClipboard? cb = GetTopLevel(this)?.Clipboard;
            return cb is null ? null : await cb.TryGetTextAsync();
        }
        catch
        {
            return null;
        }
    }

    private void StartBackground(Func<DownloadInput, Task> action)
    {
        if (_task is { IsCompleted: false })
            return;

        string arg = VodInput.Text?.Trim() ?? "";
        if (arg.Length == 0)
        {
            Log("Enter a VOD URL or slug first.");
            return;
        }

        var input = new DownloadInput(
            arg,
            SecondsSpin.Value is { } s and > 0 ? (double)s : null,
            ReencodeCheck.IsChecked == true,
            ParallelSpin.Value is { } p and > 0 ? (int)p : 8);

        ResetProgress();
        SetBusy(true);
        _task = Task.Run(async () =>
        {
            try
            {
                await action(input);
            }
            catch (Exception ex)
            {
                await UiAsync(() => Log("Error: " + ex.Message));
            }
            finally
            {
                await UiAsync(() => SetBusy(false));
            }
        });
    }

    private async Task FetchAsync(DownloadInput input)
    {
        string slug = MaincastId.ResolveSlug(input.Arg);
        Vod vod = await VodApi.FetchAsync(slug);
        string host = await VodApi.ResolveHostAsync(vod.Link);
        await UiAsync(() =>
        {
            InfoTitle.Text = "Title: " + vod.Name;
            InfoMeta.Text = $"Date: {vod.Date}  ·  Host: {host}";
            Log($"Found: {vod.Name} ({vod.Date}) on {host}");
        });
    }

    private async Task DownloadAsync(DownloadInput input)
    {
        string slug = MaincastId.ResolveSlug(input.Arg);
        Vod vod = await VodApi.FetchAsync(slug);
        string mediaId = MaincastId.ExtractMediaId(vod.Link);
        string host = await VodApi.ResolveHostAsync(vod.Link);

        await UiAsync(() =>
        {
            InfoTitle.Text = "Title: " + vod.Name;
            InfoMeta.Text = $"Date: {vod.Date}  ·  Host: {host}";
            Log($"Downloading {vod.Name} ({vod.Date}) from {host}");
        });

        await using SocketSession session = await SocketSession.ConnectAsync(host, mediaId);
        string hash = await session.GetHashAsync();
        string master = $"https://{host}/{mediaId}/master.m3u8?key={hash}";

        string outfile = Path.Combine(
            AppContext.BaseDirectory,
            $"{Output.FormatDate(vod.Date)}.{Output.SafeName(vod.Name ?? slug)}.mp4");

        await UiAsync(() =>
        {
            InfoOutput.Text = "Output: " + outfile;
            Log("Output: " + outfile);
        });

        var opts = new Options(input.Seconds, input.Reencode, false, input.Parallel);

        int rc = await HlsDownloader.DownloadAsync(master, slug, outfile, opts,
            (phase, sec, total) =>
            {
                Dispatcher.UIThread.Post(() => UpdateProgress(phase, sec, total));
                return Task.CompletedTask;
            });

        if (ToolRunner.Cancelled)
            await UiAsync(() => Log("Interrupted - segments kept, re-run to resume."));
        else if (rc == 0 && File.Exists(outfile))
            await UiAsync(() => Log("Done -> " + outfile));
        else
            await UiAsync(() => Log($"Download failed (exit {rc})."));
    }

    private void UpdateProgress(string phase, double sec, double? total)
    {
        try
        {
            int pct = total is > 0 ? (int)Math.Clamp(sec / total.Value * 100, 0, 100) : -1;
            Progress.Value = Math.Max(0, pct);
            ProgressText.Text = pct >= 0 ? $"{phase} {pct}%" : phase;
            ProgressTime.Text = total is > 0
                ? $"{Output.FormatTime(sec)} / {Output.FormatTime(total.Value)}"
                : Output.FormatTime(sec);
        }
        catch { }
    }

    private void ResetProgress()
    {
        Progress.Value = 0;
        ProgressText.Text = "0%";
        ProgressTime.Text = "00:00";
    }

    private void SetBusy(bool busy)
    {
        DownloadButton.IsEnabled = !busy;
        FetchButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        VodInput.IsEnabled = !busy;
    }

    private void Log(string line)
    {
        try
        {
            LogBox.Text += line + Environment.NewLine;
            LogBox.CaretIndex = LogBox.Text?.Length ?? 0;
        }
        catch { }
    }

    private static Task UiAsync(Action action)
    {
        var tcs = new TaskCompletionSource();
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }
}

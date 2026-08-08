# maincast_dl

Download Maincast.tv VODs (via URL or slug) as MP4, with resumable downloads.

## Requirements

- .NET 10 SDK (to build/publish)
- [ffmpeg](https://ffmpeg.org/) and `ffprobe` in PATH

## Usage

```
maincast_dl <maincast VOD URL or slug>... [--seconds N] [--reencode] [--progress]
```

Pass one or more URLs/slugs to download them in sequence. Each video is saved
next to the executable, named after the VOD title.

| Option | Description |
| --- | --- |
| `--seconds N` | Download only the first N seconds |
| `--reencode` | Re-encode to H.264/AAC instead of stream copy |
| `--progress` | Show ffmpeg's verbose log |

## Resuming

Interrupt a download (Ctrl+C / SIGTERM) and the partial data is kept in a
`.part.ts` file. Re-run the same command to resume from where it stopped.

## Build & publish

```
dotnet publish -c Release -r linux-x64 -o publish/linux
dotnet publish -c Release -r win-x64 -o publish/win
```

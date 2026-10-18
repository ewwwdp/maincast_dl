# MaincastDl

Download Maincast.tv VODs (via URL or slug) as MP4, with resumable downloads.

The repo contains three projects under `src/`:

| Project | Path | Description |
| --- | --- | --- |
| `MaincastDl` | `src/MaincastDl` | Console CLI (the original tool) |
| `MaincastDl.Desktop` | `src/MaincastDl.Desktop` | Avalonia GUI (`MaincastDl.Gui`) |
| `MaincastDl.Core` | `src/MaincastDl.Core` | Shared download logic used by both |

Open `MaincastDl.slnx` in an IDE or build from the repo root.

## Requirements

- .NET 10 SDK (to build/publish)
- [ffmpeg](https://ffmpeg.org/) in PATH (for development; published apps bundle it)

## Console usage

```
MaincastDl <maincast VOD URL or slug>... [--seconds N] [--reencode] [--progress] [--parallel N] [--cookie <session>]
```

Pass one or more URLs/slugs to download them in sequence. Each video is saved
next to the executable, named after the VOD title.

| Option | Description |
| --- | --- |
| `--seconds N` | Download only the first N seconds |
| `--reencode` | Re-encode to H.264/AAC instead of stream copy |
| `--progress` | Show ffmpeg's verbose log |
| `--parallel N` | Segment download concurrency (default 8) |
| `--cookie <session>` | Maincast session cookie (or set `MAINCAST_COOKIE`) — needed for free VODs |

## Free VODs and the session cookie

Maincast keeps two catalogs. The legacy one (`a.maincast.tv`) that this tool
reads first only lists **paid** VODs. **Free** VODs (e.g. most NASCAR races) are
only in the new API, and their stream is served through `api/playback`, which
requires a signed-in session.

For those, log in at [maincast.tv](https://maincast.tv) and pass your session
cookie — either the value of the **`mc_access`** cookie, or the whole `Cookie`
request header:

```
MaincastDl https://maincast.tv/vod/nascar-enjoy-illinois-300 --cookie "eyJhbGciOiJIUzI1Ni..."
MaincastDl https://maincast.tv/vod/nascar-enjoy-illinois-300 --cookie "mc_access=eyJ...; mc_refresh=m_..."
MAINCAST_COOKIE="eyJhbGciOiJIUzI1Ni..." MaincastDl https://maincast.tv/vod/nascar-enjoy-illinois-300
```

`MAINCAST_COOKIE` is just the environment-variable name for the app (it is not a
browser cookie). Get the value from devtools → **Application → Cookies →
`https://maincast.tv` → `mc_access`**, or devtools → **Network → any request to
`maincast.tv` → Request Headers → copy the whole `Cookie` line**. The GUI has a
"Cookie" field for the same purpose.

## GUI usage

Run `MaincastDl.Gui` (desktop app for Windows / Linux / macOS). Paste a VOD
URL or slug, optionally adjust re-encode / parallel / max-seconds, then press
Download. Progress and logs are shown in the window; Cancel stops the download
(the `maincast-<slug>/` segment folder is kept for resuming).

For free VODs, paste your Maincast session cookie into the "Cookie" field (it
pre-fills from `MAINCAST_COOKIE` if set).

From source (note the `--project` flag — plain `dotnet run` picks the console app):

```
dotnet run --project src/MaincastDl.Desktop/MaincastDl.Desktop.csproj
```

## Resuming

Interrupt a download (Ctrl+C / Cancel) and downloaded segments are kept in a
`maincast-<slug>/` folder next to the output. Re-run the same command to resume
from where it stopped; the folder is removed after a successful remux.

## Bundling ffmpeg

Published GUI apps include a static ffmpeg next to the executable, so end users
don't need it installed. Fetch the binaries before publishing:

```
./scripts/fetch-ffmpeg.sh linux-x64
./scripts/fetch-ffmpeg.sh win-x64
./scripts/fetch-ffmpeg.sh osx-arm64
```

The binaries are git-ignored; `ToolRunner` prefers them over the system PATH.

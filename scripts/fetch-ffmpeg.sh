#!/usr/bin/env bash
# Download static ffmpeg builds and place them where the app expects them:
#
#   MaincastDl.Desktop/ffmpeg/<rid>/ffmpeg[.exe]
#
# Usage: ./scripts/fetch-ffmpeg.sh [linux-x64|win-x64|osx-arm64]
#        (defaults to the current OS)
#
# The binaries are copied next to the published GUI app and used automatically
# (ToolRunner prefers them over the system PATH ffmpeg).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DEST="$ROOT/src/MaincastDl.Desktop/ffmpeg"

current_rid() {
  case "$(uname -s)" in
    Linux*)  echo linux-x64 ;;
    Darwin*) echo osx-arm64 ;;
    MINGW*|MSYS*|CYGWIN*) echo win-x64 ;;
    *) echo linux-x64 ;;
  esac
}

RID="${1:-$(current_rid)}"
mkdir -p "$DEST/$RID"

case "$RID" in
  linux-x64)
    TMP="$(mktemp -d)"
    curl -fL -o "$TMP/ff.tar.xz" \
      https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-amd64-static.tar.xz
    tar -xJf "$TMP/ff.tar.xz" -C "$TMP"
    cp "$(find "$TMP" -name ffmpeg -type f | head -1)" "$DEST/$RID/ffmpeg"
    rm -rf "$TMP"
    echo "Bundled: $DEST/$RID/ffmpeg"
    ;;
  win-x64)
    TMP="$(mktemp -d)"
    curl -fL -o "$TMP/ff.zip" \
      https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip
    unzip -o -j "$TMP/ff.zip" "*/bin/ffmpeg.exe" -d "$DEST/$RID"
    rm -rf "$TMP"
    echo "Bundled: $DEST/$RID/ffmpeg.exe"
    ;;
  osx-arm64)
    curl -fL -o "$DEST/$RID/ffmpeg" \
      https://github.com/eugeneware/ffmpeg-static/releases/download/b6.1.1/ffmpeg-darwin-arm64
    chmod +x "$DEST/$RID/ffmpeg"
    echo "Bundled: $DEST/$RID/ffmpeg (ffmpeg-static b6.1.1)"
    ;;
  *)
    echo "Unknown RID: $RID (expected linux-x64, win-x64 or osx-arm64)" >&2
    exit 1
    ;;
esac

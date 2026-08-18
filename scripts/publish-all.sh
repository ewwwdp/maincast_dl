#!/usr/bin/env bash
# Publish the console CLI and the GUI for every supported platform and zip them.
#
# Usage: ./scripts/publish-all.sh [rid...]
#   With no arguments, builds all: linux-x64, win-x64, osx-arm64.
#   Example: ./scripts/publish-all.sh linux-x64 win-x64
#
# Output (in publish/):
#   MaincastDl-<rid>.zip          console CLI
#   MaincastDl.Gui-<rid>.zip      GUI (with bundled ffmpeg)
set -euo pipefail
cd "$(dirname "$0")/.."

if [ $# -eq 0 ]; then
  RIDS=(linux-x64 win-x64 osx-arm64)
else
  RIDS=("$@")
fi

make_zip() {
  local dir="$1" out="$2"
  if command -v zip >/dev/null 2>&1; then
    (cd "$dir" && zip -q -r "../$(basename "$out")" . -x "*.pdb")
  else
    python3 - "$dir" "$out" <<'PY'
import os, sys, zipfile
src, out = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    for root, _, files in os.walk(src):
        for f in files:
            if f.endswith(".pdb"):
                continue
            full = os.path.join(root, f)
            z.write(full, os.path.relpath(full, src))
PY
  fi
}

for RID in "${RIDS[@]}"; do
  echo "=== $RID ==="

  ./scripts/fetch-ffmpeg.sh "$RID"

  echo "-- console --"
  CONSOLE_OUT="publish/console-$RID"
  rm -rf "$CONSOLE_OUT"
  dotnet publish src/MaincastDl -c Release -r "$RID" -o "$CONSOLE_OUT" >/dev/null
  if [ "$RID" = "win-x64" ]; then
    mv "$CONSOLE_OUT/MaincastDl.exe" "$CONSOLE_OUT/MaincastDl-$RID.exe"
  else
    mv "$CONSOLE_OUT/MaincastDl" "$CONSOLE_OUT/MaincastDl-$RID"
  fi
  make_zip "$CONSOLE_OUT" "publish/MaincastDl-$RID.zip"

  echo "-- gui --"
  GUI_OUT="publish/gui-$RID"
  rm -rf "$GUI_OUT"
  dotnet publish src/MaincastDl.Desktop -c Release -r "$RID" -o "$GUI_OUT" >/dev/null
  if [ "$RID" = "win-x64" ]; then
    mv "$GUI_OUT/MaincastDl.Gui.exe" "$GUI_OUT/MaincastDl.Gui-$RID.exe"
  else
    mv "$GUI_OUT/MaincastDl.Gui" "$GUI_OUT/MaincastDl.Gui-$RID"
  fi
  make_zip "$GUI_OUT" "publish/MaincastDl.Gui-$RID.zip"
done

echo
echo "Done. Archives in publish/:"
ls -lh publish/*.zip

#!/usr/bin/env bash
# P8-11: turn the live realtime demo recording into the README GIF and screenshots.
#
# 1. Record (operator's own terminal, demo passwords in env; creates and then cancels one deployment):
#      cd src/AegisOps.Web && AEGISOPS_LIVE_VIDEO=1 npm run test:e2e:live
# 2. Build docs/assets/demo.gif (developer left, approver right) and copy the screenshots:
#      scripts/make-demo-gif.sh
#
# Needs ffmpeg (brew install ffmpeg). Output width defaults to 1280 px at 8 fps; override with GIF_WIDTH / GIF_FPS.
set -euo pipefail
cd "$(dirname "$0")/.."
results="src/AegisOps.Web/test-results"
left="$results/videos/developer.webm"
right="$results/videos/approver.webm"
out_dir="docs/assets"
width="${GIF_WIDTH:-1280}"
fps="${GIF_FPS:-8}"

command -v ffmpeg >/dev/null || { echo "ffmpeg is required (brew install ffmpeg)." >&2; exit 1; }
for video in "$left" "$right"; do
  [[ -f "$video" ]] || { echo "Missing $video — record it first with AEGISOPS_LIVE_VIDEO=1 npm run test:e2e:live." >&2; exit 1; }
done
mkdir -p "$out_dir"

palette="$(mktemp -t aegisops-palette).png"
trap 'rm -f "$palette"' EXIT
# Side by side (developer left, approver right; the README caption says so — Homebrew's ffmpeg has no drawtext),
# trimmed to the shorter clip, then a two-pass palette for a small, sharp GIF.
filter="[0:v][1:v]hstack=inputs=2:shortest=1,fps=${fps},scale=${width}:-1:flags=lanczos"
ffmpeg -loglevel error -y -i "$left" -i "$right" -filter_complex "${filter},palettegen=stats_mode=diff" "$palette"
ffmpeg -loglevel error -y -i "$left" -i "$right" -i "$palette" \
  -filter_complex "${filter}[v];[v][2:v]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle" \
  "$out_dir/demo.gif"

for shot in approval-toast deployment-awaiting-approval deployment-one-approval; do
  if [[ -f "$results/screenshots/$shot.png" ]]; then
    cp "$results/screenshots/$shot.png" "$out_dir/$shot.png"
  fi
done

duration="$(ffprobe -v error -show_entries format=duration -of csv=p=0 "$out_dir/demo.gif" 2>/dev/null || echo "?")"
size="$(du -h "$out_dir/demo.gif" | cut -f1)"
echo "Wrote $out_dir/demo.gif (${duration}s, ${size}) and screenshots:"
ls -1 "$out_dir"/*.png 2>/dev/null || true

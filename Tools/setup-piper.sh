#!/bin/sh
# Puts Harley's offline voices (Piper, via sherpa-onnx) into Assets/StreamingAssets/Piper.
# Run once from the project folder:  sh Tools/setup-piper.sh
# Voices: en_US kristin (public-domain LibriVox recordings), ar_JO kareem. ~130 MB, not in git.
set -e
cd "$(dirname "$0")/.."
DST=Assets/StreamingAssets/Piper
TMP=$(mktemp -d)
BASE=https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models
get() { # name lang
  echo "Downloading $1 ..."
  curl -sfL "$BASE/$1.tar.bz2" -o "$TMP/$1.tar.bz2"
  tar xjf "$TMP/$1.tar.bz2" -C "$TMP"
  mkdir -p "$DST/$2"
  cp "$TMP/$1"/*.onnx "$TMP/$1/tokens.txt" "$TMP/$1/MODEL_CARD" "$DST/$2/"
  [ -d "$DST/espeak-ng-data" ] || cp -R "$TMP/$1/espeak-ng-data" "$DST/"
}
get vits-piper-en_US-kristin-medium en
get vits-piper-ar_JO-kareem-medium ar
( cd "$DST" && find . -type f ! -name files.txt ! -name "*.meta" | sed 's|^\./||' | sort > files.txt )
rm -rf "$TMP"
echo "Done: $(wc -l < $DST/files.txt) files in $DST"

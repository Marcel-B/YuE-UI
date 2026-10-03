#!/bin/sh
# Trains a sound LoRA for YuE2 on this Mac from a folder of songs and puts it where Tonwerk's form offers it.
#
#   deploy/train-lora.sh ~/dubstep dubstep                 folder of songs, trigger word (also the LoRA's name)
#   deploy/train-lora.sh ~/dubstep dubstep --steps 3000    further options go to the trainer (see --help)
#   deploy/train-lora.sh --background ~/dubstep dubstep    keeps running after the SSH session ends; log next to the LoRA
#
# The folder holds 5 to 30 songs in one style (mp3, wav, flac, m4a, ...); a .txt of the same name beside a song
# describes it ("wobble bass, half-time drums, 140 bpm"), the trigger word leads every caption. Every 500 steps a
# checkpoint (name-step500, ...) lands beside the LoRA, and the form lists those too, to compare.
#
# Runs with YuE Studio's Python (torch, yue2) on the GPU. It needs about 6 GB: while it runs, songs still work, but
# don't also convert voices or try the speech lab. Ctrl-C (or kill) stops it and keeps what it has learned so far.
set -eu
START="$(pwd)"
cd "$(dirname "$0")/.."
REPO="$(pwd)"

URL="${YUEUI_URL:-http://127.0.0.1:5090}"
YUE_ROOT="${YUE_ROOT:-$HOME/Library/Application Support/YuE Studio}"
PYTHON="$YUE_ROOT/env/bin/python"
TRAINER="$REPO/src/YueUI.Api/Worker/yueui_lora_train.py"

BACKGROUND=false
if [ "${1:-}" = "--background" ]; then
  BACKGROUND=true
  shift
fi
if [ $# -lt 2 ] || [ "${1:-}" = "--help" ]; then
  sed -n '2,14p' "$0" | sed 's/^# \{0,1\}//'
  [ -x "$PYTHON" ] && echo && "$PYTHON" "$TRAINER" --help | sed -n '/^options:/,$p'
  exit 2
fi
case "$1" in
  /*) DATA_DIR="$1" ;;
  *) DATA_DIR="$START/$1" ;;
esac
TRIGGER="$2"
shift 2

if [ ! -x "$PYTHON" ]; then
  echo "YuE Studio's Python is not at $PYTHON (deploy/setup-mac.sh installs it; YUE_ROOT names another place)." >&2
  exit 1
fi
if [ ! -d "$DATA_DIR" ]; then
  echo "No folder $DATA_DIR" >&2
  exit 1
fi
NAME="$(printf '%s' "$TRIGGER" | tr -c 'A-Za-z0-9._-' '-' | sed 's/^-*//; s/-*$//')"
if [ -z "$NAME" ]; then
  echo "The trigger word must contain a letter or digit; it names the file." >&2
  exit 1
fi

# The server says where its form looks; without a running server, its default.
FOLDER="$(curl -fsS --max-time 5 "$URL/api/loras" 2>/dev/null | /usr/bin/python3 -c 'import json, sys; print(json.load(sys.stdin)["folder"])' 2>/dev/null || true)"
if [ -z "$FOLDER" ]; then
  FOLDER="$HOME/Library/Application Support/YuE UI/loras"
  echo "Tonwerk does not answer at $URL; writing to the default folder."
fi
mkdir -p "$FOLDER"
OUTPUT="$FOLDER/$NAME.safetensors"

# A generating YuE2 and the trainer fit side by side only just; an idle worker gives its memory back first.
if busy="$(curl -fsS --max-time 5 "$URL/api/busy" 2>/dev/null)"; then
  if echo "$busy" | grep -q '"busy":true'; then
    echo "Tonwerk is busy: $busy" >&2
    echo "Start the training once it is idle." >&2
    exit 1
  fi
  curl -fsS --max-time 30 -X POST "$URL/api/worker/shutdown" >/dev/null 2>&1 || true
fi

# yue2 is installed in that environment; HF_HOME is its model cache, so nothing is downloaded.
export HF_HOME="$YUE_ROOT/models"
export HF_HUB_OFFLINE=1
export PYTHONUNBUFFERED=1
export PYTORCH_ENABLE_MPS_FALLBACK=1
export PATH="/opt/homebrew/bin:/usr/local/bin:$PATH"      # ffmpeg, also in a non-login shell

set -- "$TRAINER" --data "$DATA_DIR" --output "$OUTPUT" --trigger "$TRIGGER" "$@"
echo "Training $OUTPUT from $DATA_DIR"
if [ "$BACKGROUND" = true ]; then
  LOG="$FOLDER/$NAME.log"
  # caffeinate keeps the Mac awake for as long as the training runs.
  nohup caffeinate -is "$PYTHON" "$@" >"$LOG" 2>&1 &
  echo "Runs in the background (process $!). Follow it with:  tail -f \"$LOG\""
else
  exec caffeinate -is "$PYTHON" "$@"
fi

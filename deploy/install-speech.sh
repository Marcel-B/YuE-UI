#!/bin/sh
# Sets up the speech lab: a Python environment of its own with mlx-audio, where Tonwerk looks for it (Speech:Python).
# The models are downloaded by their first take, into Speech:ModelCache next to it.
#
#   deploy/install-speech.sh           install or update mlx-audio
#   deploy/install-speech.sh --test    then have every model speak a sentence, which downloads them all (about 28 GB)
#
# Run it on the Mac as the user Tonwerk runs as, after deploy/install.sh. Running it again updates mlx-audio.
set -eu

URL="${YUEUI_URL:-http://127.0.0.1:5090}"
TEST=false
for argument in "$@"; do
  case "$argument" in
    --test) TEST=true ;;
    *) echo "Unknown argument: $argument" >&2; exit 2 ;;
  esac
done

# The server says where it looks for the environment; without a running server, its default.
PYTHON="$(curl -fsS "$URL/api/speech" 2>/dev/null | /usr/bin/python3 -c 'import json, sys; print(json.load(sys.stdin)["python"])' 2>/dev/null || true)"
if [ -z "$PYTHON" ]; then
  PYTHON="$HOME/Library/Application Support/YuE UI/speech/env/bin/python"
  echo "Tonwerk does not answer at $URL; using the default location."
fi
ENV="$(dirname "$(dirname "$PYTHON")")"
echo "Environment: $ENV"

# mlx-audio needs Python 3.10 or newer; macOS's own python3 is 3.9. uv brings a suitable one along.
if command -v uv >/dev/null 2>&1; then
  [ -x "$PYTHON" ] || uv venv --python 3.12 "$ENV"
  uv pip install --python "$PYTHON" --upgrade "mlx-audio[tts]"
else
  BASE=""
  for candidate in python3.13 python3.12 python3.11 python3.10 /opt/homebrew/bin/python3 python3; do
    if command -v "$candidate" >/dev/null 2>&1 && "$candidate" -c 'import sys; sys.exit(sys.version_info < (3, 10))' 2>/dev/null; then
      BASE="$(command -v "$candidate")"
      break
    fi
  done
  if [ -z "$BASE" ]; then
    echo "No Python 3.10 or newer found. Install one (brew install python@3.12, or uv) and run this again." >&2
    exit 1
  fi
  [ -x "$PYTHON" ] || "$BASE" -m venv "$ENV"
  "$PYTHON" -m pip install --quiet --upgrade pip
  "$PYTHON" -m pip install --upgrade "mlx-audio[tts]"
fi
"$PYTHON" -c 'import mlx_audio, importlib.metadata as m; print("mlx-audio", m.version("mlx-audio"), "is installed.")'

if [ "$TEST" = false ]; then
  echo "Open the speech lab in Tonwerk; each model downloads itself with its first take."
  exit 0
fi

# One take per model through the server, so that the lab's memory rules apply (it waits for YuE2 and the others)
# and the results are in the lab to listen to. The takes run one after the other; the page shows their progress.
curl -fsS "$URL/api/speech" | "$PYTHON" -c '
import json, sys, urllib.request
url = sys.argv[1]
models = [m["id"] for m in json.load(sys.stdin)["models"]]
body = json.dumps({"text": "Hallo! Das ist ein kurzer Test, ob dieses Modell natürlich klingt.", "models": models}).encode()
request = urllib.request.Request(url + "/api/speech/takes", data=body, headers={"Content-Type": "application/json"})
takes = json.load(urllib.request.urlopen(request))
print("Queued a test take for", ", ".join(t["modelLabel"] for t in takes) + ".")
print("Follow them in the speech lab; the downloads take a while.")
' "$URL"

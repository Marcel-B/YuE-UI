#!/bin/sh
# Sets up audio to MIDI (a sung or played track to a MIDI file): a Python environment of its own with Spotify's
# Basic Pitch, where Tonwerk looks for it (AudioMidi:Python). The model ships inside the package (a few MB); nothing
# is downloaded later.
#
#   deploy/install-midi.sh    install Basic Pitch, or move it to the versions pinned here
#
# Run it on the Mac as the user Tonwerk runs as, after deploy/install.sh.
set -eu

URL="${YUEUI_URL:-http://127.0.0.1:5090}"
# Basic Pitch 0.4.0 asks for TensorFlow on Linux and for CoreML on the Mac; Tonwerk runs its ONNX model instead, the
# same on both, so the package comes without its own requirements and these are pinned instead (as tested together).
# setuptools below 81 keeps pkg_resources, which resampy still imports.
PACKAGE="basic-pitch==0.4.0"
REQUIREMENTS="onnxruntime==1.30.0 librosa==0.11.0 soundfile==0.14.0 resampy==0.4.2 mir_eval==0.8.2 pretty_midi==0.2.11.post0 scipy==1.17.1 scikit-learn==1.9.1 numpy==2.4.6 numba==0.68.0 typing-extensions setuptools==80.10.2"

# The server says where it looks for the environment; without a running server, its default.
PYTHON="$(curl -fsS "$URL/api/audio-midi" 2>/dev/null | /usr/bin/python3 -c 'import json, sys; print(json.load(sys.stdin)["python"])' 2>/dev/null || true)"
if [ -z "$PYTHON" ]; then
  PYTHON="$HOME/Library/Application Support/YuE UI/midi/env/bin/python"
  echo "Tonwerk does not answer at $URL; using the default location."
fi
ENV="$(dirname "$(dirname "$PYTHON")")"
echo "Environment: $ENV"

# Tested with Python 3.11; macOS's own python3 is 3.9, which numpy 2 no longer supports. uv brings one along.
if command -v uv >/dev/null 2>&1; then
  [ -x "$PYTHON" ] || uv venv --python 3.11 "$ENV"
  # shellcheck disable=SC2086 # one word per requirement
  uv pip install --python "$PYTHON" $REQUIREMENTS
  uv pip install --python "$PYTHON" --no-deps "$PACKAGE"
else
  BASE=""
  for candidate in python3.11 python3.12 /opt/homebrew/bin/python3.11 /opt/homebrew/bin/python3.12; do
    if command -v "$candidate" >/dev/null 2>&1; then
      BASE="$(command -v "$candidate")"
      break
    fi
  done
  if [ -z "$BASE" ]; then
    echo "No Python 3.11 or 3.12 found. Install uv (brew install uv) and run this again." >&2
    exit 1
  fi
  [ -x "$PYTHON" ] || "$BASE" -m venv "$ENV"
  "$PYTHON" -m pip install --quiet --upgrade pip
  # shellcheck disable=SC2086
  "$PYTHON" -m pip install $REQUIREMENTS
  "$PYTHON" -m pip install --no-deps "$PACKAGE"
fi
"$PYTHON" -W ignore -c 'import logging; logging.disable(logging.WARNING); import importlib.metadata as m; from basic_pitch.inference import predict; print("Basic Pitch", m.version("basic-pitch"), "is installed.")'
echo "Tonwerk turns a track into MIDI on the transcription page and by every stem on the voices page."

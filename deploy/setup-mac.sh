#!/bin/sh
# Sets Tonwerk up on a Mac that has nothing but macOS and the Command Line Tools (for git): Homebrew, .NET 10,
# Node.js, ffmpeg and uv, then YuE2 with its Python environment and models, without the YuE Studio app, and
# Tonwerk itself as a LaunchAgent (deploy/install.sh).
#
#   deploy/setup-mac.sh                    everything Tonwerk needs to make songs
#   deploy/setup-mac.sh --transcription    plus SheetSage2 for transcriptions (about 2 GB more)
#   deploy/setup-mac.sh --speech           plus the speech lab's environment (deploy/install-speech.sh)
#   deploy/setup-mac.sh --all              both
#   deploy/setup-mac.sh --engine-from-source
#                                          replace an engine the YuE Studio app installed by a git checkout
#
# YuE2 comes from YuE Studio's repository (github.com/tonywestonuk/YuE-Studio), not from upstream YuE2: upstream
# runs on CUDA only, YuE Studio's fork adds the Apple Silicon engines (Neural Engine, MLX) and the worker
# (tools/yue2_worker.py) Tonwerk talks to. The steps are the ones the app's own installer takes (Installer.swift,
# SheetSageInstaller.swift), into the same folders, so Tonwerk's defaults (Yue:InstallRoot) need no change and the
# app, installed later, finds the models. The source is a git checkout at ENGINE_REF instead of the app's copy.
#
# Every step is skipped when it is already done, so running it again is safe; with a new ENGINE_REF it moves the
# engine to that version. Run it as the user Tonwerk will run as, logged in (Metal and the Neural Engine need the
# user's session), from a terminal: Homebrew and the .NET installer ask for the password.
set -eu
cd "$(dirname "$0")/.."
REPO="$(pwd)"

# The YuE Studio version whose worker yueui_worker.py's SEAMS were checked against. Raise it only after checking
# them against the new worker; the last step of this script reports whether they still fit.
ENGINE_REPO="https://github.com/tonywestonuk/YuE-Studio.git"
ENGINE_REF="${ENGINE_REF:-v0.4.0}"
ROOT="$HOME/Library/Application Support/YuE Studio"
OUTPUT="$HOME/Music/YuE Studio"
SHEETSAGE_RECIPE=1   # SheetSageInstaller.recipe: the app reinstalls when its recipe differs from the marker's

TRANSCRIPTION=false
SPEECH=false
FROM_SOURCE=false
for argument in "$@"; do
  case "$argument" in
    --transcription) TRANSCRIPTION=true ;;
    --speech) SPEECH=true ;;
    --all) TRANSCRIPTION=true; SPEECH=true ;;
    --engine-from-source) FROM_SOURCE=true ;;
    -h|--help) sed -n '2,21p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "Unknown argument: $argument" >&2; exit 2 ;;
  esac
done

step() { printf '\n== %s\n' "$1"; }
fail() { echo "$1" >&2; exit 1; }

step "Checking the Mac"
[ "$(uname -s)" = Darwin ] || fail "This sets up a Mac; this is $(uname -s)."
[ "$(uname -m)" = arm64 ] || fail "YuE2's engines need Apple Silicon; this Mac is $(uname -m)."
MACOS="$(sw_vers -productVersion)"
[ "${MACOS%%.*}" -ge 14 ] || fail "YuE Studio's engines need macOS 14 or later; this is $MACOS."
MEMORY_GB=$(( $(sysctl -n hw.memsize) / 1073741824 ))
echo "macOS $MACOS, $MEMORY_GB GB memory"
[ "$MEMORY_GB" -ge 16 ] || echo "Warning: YuE2 needs at least 16 GB; with $MEMORY_GB GB it will hardly run."
# clang builds the Neural Engine bridge; the Command Line Tools are enough, Xcode is not needed.
xcode-select -p >/dev/null 2>&1 || fail "The Command Line Tools are missing: run xcode-select --install, then this again."
FREE_GB=$(( $(df -k "$HOME" | awk 'NR == 2 { print $4 }') / 1048576 ))
[ "$FREE_GB" -ge 20 ] || fail "Only $FREE_GB GB free; the models, environments and tools need about 20 GB."

step "Homebrew"
if ! command -v brew >/dev/null 2>&1; then
  [ -x /opt/homebrew/bin/brew ] || /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
  # A non-login shell (an ssh host command) has no Homebrew in its PATH.
  eval "$(/opt/homebrew/bin/brew shellenv)"
fi
brew --version | head -1

step "Tools: uv, ffmpeg, Node.js, .NET 10"
for formula in uv ffmpeg; do
  command -v "$formula" >/dev/null 2>&1 || brew install "$formula"
done
# The frontend build needs Node.js 22.12 or later.
if ! command -v node >/dev/null 2>&1 \
  || ! node -e 'const [a, b] = process.versions.node.split(".").map(Number); process.exit(a > 22 || (a === 22 && b >= 12) ? 0 : 1)'; then
  brew install node
fi
# The cask installs Microsoft's package to /usr/local/share/dotnet, which deploy/install.sh puts into the LaunchAgent.
PATH="$PATH:/usr/local/share/dotnet"
if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks | grep -q '^10\.'; then
  brew install --cask dotnet-sdk
fi
dotnet --list-sdks | grep -q '^10\.' \
  || fail "No .NET 10 SDK after installing Homebrew's dotnet-sdk; install it from https://dotnet.microsoft.com/download/dotnet/10.0 and run this again."
echo "$(uv --version), node $(node --version), .NET $(dotnet --list-sdks | grep '^10\.' | tail -1 | cut -d' ' -f1)"

# Where uv puts Python and where the models go, as the app's installer does.
export UV_PYTHON_INSTALL_DIR="$ROOT/python"
export HF_HOME="$ROOT/models"
export HF_HUB_DISABLE_TELEMETRY=1
PYTHON="$ROOT/env/bin/python"
WORKER_SOURCE="$ROOT/src"
mkdir -p "$ROOT" "$OUTPUT"

step "YuE2 engine ($ENGINE_REF)"
CHECKOUT="$WORKER_SOURCE"
if [ -d "$CHECKOUT" ] && [ ! -d "$CHECKOUT/.git" ]; then
  if [ "$FROM_SOURCE" = true ]; then
    # The environment and the models stay; only the app's copy of the source is replaced.
    mv "$CHECKOUT" "$ROOT/src.app-$(date +%Y%m%d-%H%M%S)"
    echo "The app's copy of the source was moved aside."
  else
    echo "The YuE Studio app installed this engine; it is left as it is (--engine-from-source replaces it by a checkout)."
    CHECKOUT=""
  fi
fi
if [ -n "$CHECKOUT" ]; then
  CHANGED=false
  if [ ! -d "$CHECKOUT/.git" ]; then
    git clone --filter=blob:none --quiet "$ENGINE_REPO" "$CHECKOUT"
    CHANGED=true
  fi
  git -C "$CHECKOUT" fetch --quiet --tags origin
  WANTED="$(git -C "$CHECKOUT" rev-parse "$ENGINE_REF^{commit}")"
  if [ "$(git -C "$CHECKOUT" rev-parse HEAD)" != "$WANTED" ]; then
    git -C "$CHECKOUT" checkout --quiet --detach "$WANTED"
    CHANGED=true
  fi
  git -C "$CHECKOUT" describe --tags --always

  # The Neural Engine bridge, built as YuE Studio's app/package.sh builds it.
  if [ "$CHANGED" = true ] || [ ! -f "$CHECKOUT/src/yue2/ane/libyue2ane.dylib" ]; then
    (cd "$CHECKOUT/src/yue2/ane" && clang -O2 -fobjc-arc -dynamiclib libyue2ane.m -o libyue2ane.dylib -framework Foundation -framework IOSurface)
    echo "Neural Engine bridge built."
  fi

  step "Python environment"
  [ -x "$PYTHON" ] || { uv python install 3.12; uv venv "$ROOT/env" --python 3.12; }
  # Editable: the package's version (0.1.6) stays the same across YuE Studio releases, so a plain install of a
  # newer checkout would be skipped as already satisfied.
  uv pip install --python "$PYTHON" --quiet -e "${CHECKOUT}[apple]"
  "$PYTHON" -c 'import importlib.metadata as m; print("torch", m.version("torch") + ", mlx", m.version("mlx"))'
fi
[ -x "$PYTHON" ] || fail "No Python environment at $ROOT/env."

step "YuE2 models (about 7 GB)"
models_present() {
  [ -d "$HF_HOME/hub/models--m-a-p--YuE2-3B/snapshots" ] && [ -d "$HF_HOME/hub/models--m-a-p--YuE2-Vae/snapshots" ]
}
if models_present; then
  echo "Already downloaded."
else
  # The script prints {"bytes", "total", "rate_mbps"} lines; this shows every tenth. Without pipefail a failed
  # download would pass unnoticed, hence the check after it.
  "$PYTHON" "$WORKER_SOURCE/tools/download_models.py" | "$PYTHON" -c '
import json, sys
shown = -1
for line in sys.stdin:
    try:
        p = json.loads(line)
    except ValueError:
        print(line, end="")
        continue
    done, total = p.get("bytes", 0), p.get("total") or 0
    if total and int(10 * done / total) > shown:
        shown = int(10 * done / total)
        print("%.1f of %.1f GB" % (done / 1e9, total / 1e9), flush=True)
'
  models_present || fail "The models did not download completely; run this again to resume."
fi
# YuE Studio's marker; `yue doctor` reads the version from it. The app, opened later, compares it with its own
# build and installs its copy over the checkout, which leaves a working engine without the git history.
if [ -n "$CHECKOUT" ]; then
  printf '{"version": "%s", "source": "%s"}\n' "$(git -C "$CHECKOUT" describe --tags --always)" "$ENGINE_REPO" > "$ROOT/installed.json"
fi

if [ "$TRANSCRIPTION" = true ]; then
  step "SheetSage2 for transcriptions (about 2 GB)"
  SHEETSAGE="$ROOT/sheetsage-env"
  if [ -x "$SHEETSAGE/bin/python" ] && grep -q "\"recipe\": *\"$SHEETSAGE_RECIPE\"" "$ROOT/sheetsage-installed.json" 2>/dev/null; then
    echo "Already installed."
  else
    # An environment of its own: SheetSage2's pins (torch 2.8, transformers 4.45, numpy 1.24) clash with YuE2's.
    "$PYTHON" "$WORKER_SOURCE/tools/download_models.py" m-a-p/SheetSage2 m-a-p/MERT-v2-FullSong >/dev/null
    uv python install 3.11
    uv venv "$SHEETSAGE" --python 3.11 --clear
    uv pip install --python "$SHEETSAGE/bin/python" torch==2.8.0 torchaudio==2.8.0
    for SNAPSHOT in "$HF_HOME"/hub/models--m-a-p--SheetSage2/snapshots/*/; do break; done
    uv pip install --python "$SHEETSAGE/bin/python" -r "${SNAPSHOT}requirements.txt" soundfile
    printf '{"recipe": "%s"}\n' "$SHEETSAGE_RECIPE" > "$ROOT/sheetsage-installed.json"
  fi
fi

step "Tonwerk"
"$REPO/deploy/install.sh"

if [ "$SPEECH" = true ]; then
  step "Speech lab"
  "$REPO/deploy/install-speech.sh"
fi

step "Checking the worker"
# The worker announces itself before it loads a model; the ready event says whether Tonwerk's extension could
# patch it (SEAMS in yueui_worker.py).
READY="$(echo '{"cmd": "quit"}' | YUE2_OUTPUT_DIR="$OUTPUT" YUE2_ANE_CACHE="$ROOT/ane-cache" \
  "$PYTHON" -u "$REPO/src/YueUI.Api/Worker/yueui_worker.py" "$WORKER_SOURCE/tools/yue2_worker.py" 2>/dev/null \
  | grep '"ready"' | head -1 || true)"
case "$READY" in
  *'"yueui_extensions": true'*) echo "The worker starts, with Tonwerk's extensions." ;;
  *'"yueui_extensions": false'*) echo "Warning: the worker starts without Tonwerk's extensions; check SEAMS in yueui_worker.py against this engine." ;;
  *) echo "Warning: the worker did not report ready; run it by hand to see why (see CLAUDE.md, the worker)." ;;
esac

cat <<EOF

Tonwerk runs on http://127.0.0.1:5090/ui/. Not set up by this script:
  - access from the phone: install Tailscale, then  tailscale serve --bg --https=8443 5090
  - lyrics drafts: LM Studio (brew install --cask lm-studio) with a model, see the README
  - voices and stems: ChangeMyVoice and StemMyWav, each from its own repository
EOF

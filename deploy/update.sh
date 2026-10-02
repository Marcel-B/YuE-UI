#!/bin/sh
# Keeps a Mac set up by deploy/setup-mac.sh up to date: new commits on main are deployed as soon as Tonwerk has
# nothing running, and with them whatever they change in setup-mac.sh (a raised ENGINE_REF moves YuE2, a raised
# SEED_VC_REF or SEPARATOR_PACKAGE the voice engines), since a deploy runs `setup-mac.sh --update`.
#
#   deploy/update.sh             one round, as the LaunchAgent runs it
#   deploy/update.sh on [min]    run a round every few minutes (default 5) from a LaunchAgent; setup-mac.sh does this
#   deploy/update.sh off         stop that
#   deploy/update.sh status      whether it runs, and the end of its log
#
# Nothing is taken from anywhere but this repository's main: a new YuE Studio release is only reported in the log,
# since yueui_worker.py's SEAMS have to be checked against its worker before ENGINE_REF may move to it.
# The agent has the label `yue watch` (Marcel-B/scripts) used, so the two never run side by side.
set -eu
cd "$(dirname "$0")/.."
REPO="$(pwd)"

LABEL=de.bvelop.yueui-watch
URL="${YUEUI_URL:-http://127.0.0.1:5090}"
DATA="$HOME/Library/Application Support/YuE UI"
STATE="$DATA/update"
LOG="$HOME/Library/Logs/tonwerk-update.log"
BACKUPS="$HOME/Backups/YueUI"
BACKUP_KEEP=30
ENGINE_REPO="https://github.com/tonywestonuk/YuE-Studio.git"

log() { printf '%s %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$*"; }

# The database, the push keys and the local settings, as `yue backup` keeps them, in the same folder.
backup() {
  target="$BACKUPS/$(date +%Y-%m-%d_%H%M%S)"
  mkdir -p "$target"
  chmod 700 "$BACKUPS" "$target"
  # .backup rather than cp: Tonwerk may be writing, and a copied file would then be half of one.
  [ -f "$DATA/yueui.db" ] && sqlite3 "$DATA/yueui.db" ".backup '$target/yueui.db'"
  [ -f "$DATA/push.json" ] && cp "$DATA/push.json" "$target/"
  SETTINGS="$HOME/Library/Application Support/YueUI/app/appsettings.Production.json"
  [ -f "$SETTINGS" ] && cp "$SETTINGS" "$target/"
  # shellcheck disable=SC2012 # the names are our own timestamps
  ls -1d "$BACKUPS"/20*_* 2>/dev/null | sort -r | tail -n +$((BACKUP_KEEP + 1)) | while read -r old; do rm -rf "$old"; done
}

# The highest of vX.Y.Z versions on stdin (macOS's sort has no -V).
newest() { grep -E '^v[0-9]+\.[0-9]+(\.[0-9]+)?$' | sed 's/^v//' | sort -t. -k1,1n -k2,2n -k3,3n | tail -1 | sed 's/^/v/'; }

# Once per release: a YuE Studio tag newer than the one setup-mac.sh pins.
report_engine() {
  # shellcheck disable=SC2016 # the $ is setup-mac.sh's, matched literally
  pinned="$(sed -n 's/^ENGINE_REF="\${ENGINE_REF:-\(.*\)}"$/\1/p' "$REPO/deploy/setup-mac.sh")"
  latest="$(git ls-remote --tags --refs "$ENGINE_REPO" 'v*' 2>/dev/null | sed 's|.*refs/tags/||' | newest)"
  [ "$latest" != v ] && [ -n "$pinned" ] || return 0
  if [ "$latest" != "$pinned" ] && [ "$(printf '%s\n%s\n' "$pinned" "$latest" | newest)" = "$latest" ] \
    && [ "$(cat "$STATE/engine-reported" 2>/dev/null)" != "$latest" ]; then
    log "YuE Studio $latest is out (pinned: $pinned). Check SEAMS in yueui_worker.py against its worker, then raise ENGINE_REF."
    echo "$latest" > "$STATE/engine-reported"
  fi
}

round() {
  mkdir -p "$STATE"
  export GIT_TERMINAL_PROMPT=0
  branch="$(git symbolic-ref --short HEAD 2>/dev/null || true)"
  if [ "$branch" != main ]; then
    log "The checkout is on ${branch:-no branch}, not main; nothing done."
    return 1
  fi
  git fetch -q origin main || { log "git fetch failed."; return 1; }
  report_engine
  # The last deployed commit, not HEAD: after a failed deploy the checkout is already ahead. `yue watch` kept it in
  # its own state folder before this script took over.
  deployed="$(cat "$STATE/deployed" 2>/dev/null || cat "$HOME/.local/state/yue-tools/deployed" 2>/dev/null || git rev-parse HEAD)"
  remote="$(git rev-parse origin/main)"
  if [ "$deployed" = "$remote" ] || git merge-base --is-ancestor "$remote" "$deployed" 2>/dev/null; then
    rm -f "$STATE/waiting"
    return 0
  fi
  # A commit that failed is not tried again every few minutes, only the next one.
  [ "$(cat "$STATE/failed" 2>/dev/null)" = "$remote" ] && return 0

  # A deploy restarts Tonwerk, which would cut off songs, transcriptions, drafts, versions and takes. Jobs that only
  # wait survive it. When Tonwerk does not answer there is nothing to cut off.
  if busy="$(curl -fsS --max-time 5 "$URL/api/busy" 2>/dev/null)" && echo "$busy" | grep -q '"busy":true'; then
    if [ "$(cat "$STATE/waiting" 2>/dev/null)" != "$remote" ]; then
      log "$(echo "$remote" | cut -c1-7) waits: Tonwerk is busy ($busy)."
      echo "$remote" > "$STATE/waiting"
    fi
    return 0
  fi
  rm -f "$STATE/waiting"

  log "Deploying $(echo "$deployed" | cut -c1-7) -> $(echo "$remote" | cut -c1-7):"
  git log --format='    %h %s' "$deployed..$remote" 2>/dev/null || true
  backup || log "The backup failed; deploying anyway."
  if git merge -q --ff-only origin/main && "$REPO/deploy/setup-mac.sh" --update; then
    rm -f "$STATE/failed"
    echo "$remote" > "$STATE/deployed"
    log "Deployed $(echo "$remote" | cut -c1-7)."
  else
    echo "$remote" > "$STATE/failed"
    log "Deploying $(echo "$remote" | cut -c1-7) failed; the next commit tries again, or deploy/setup-mac.sh --update by hand."
    return 1
  fi
}

case "${1:-run}" in
  run)
    mkdir -p "$STATE" "$(dirname "$LOG")"
    if [ -f "$LOG" ] && [ "$(wc -c < "$LOG")" -gt 1048576 ]; then
      tail -n 2000 "$LOG" > "$LOG.tmp" && mv "$LOG.tmp" "$LOG"
    fi
    # Against a round by hand while the agent's is deploying.
    mkdir "$STATE/lock" 2>/dev/null || { echo "Another round is running."; exit 0; }
    trap 'rmdir "$STATE/lock"' EXIT
    round
    ;;
  on)
    minutes="${2:-5}"
    case "$minutes" in ''|*[!0-9]*) echo "deploy/update.sh on [minutes]" >&2; exit 2 ;; esac
    [ "$minutes" -ge 1 ] || minutes=1
    PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"
    mkdir -p "$(dirname "$LOG")" "$HOME/Library/LaunchAgents"
    # Through a login shell: git, uv, dotnet, npm and sqlite3's Homebrew siblings are not in launchd's PATH.
    cat > "$PLIST" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key>
  <string>$LABEL</string>
  <key>ProgramArguments</key>
  <array>
    <string>/bin/zsh</string>
    <string>-lc</string>
    <string>exec "\$0" run</string>
    <string>$REPO/deploy/update.sh</string>
  </array>
  <key>StartInterval</key>
  <integer>$((minutes * 60))</integer>
  <key>RunAtLoad</key>
  <true/>
  <key>StandardOutPath</key>
  <string>$LOG</string>
  <key>StandardErrorPath</key>
  <string>$LOG</string>
</dict>
</plist>
EOF
    launchctl bootout "gui/$(id -u)/$LABEL" 2>/dev/null || true
    # bootout returns before the agent is gone, and bootstrap fails while it is still there.
    for _ in 1 2 3 4 5 6 7 8 9 10; do
      launchctl print "gui/$(id -u)/$LABEL" >/dev/null 2>&1 || break
      sleep 0.5
    done
    launchctl bootstrap "gui/$(id -u)" "$PLIST"
    echo "Updates on: main is checked every $minutes minute(s) and deployed once Tonwerk is idle. Log: $LOG"
    ;;
  off)
    launchctl bootout "gui/$(id -u)/$LABEL" 2>/dev/null || true
    rm -f "$HOME/Library/LaunchAgents/$LABEL.plist"
    echo "Updates off."
    ;;
  status)
    if launchctl print "gui/$(id -u)/$LABEL" >/dev/null 2>&1; then echo "Updates on."; else echo "Updates off (deploy/update.sh on)."; fi
    echo "Deployed: $(cut -c1-7 "$STATE/deployed" 2>/dev/null || echo unknown)"
    [ -f "$LOG" ] && tail -n 15 "$LOG"
    ;;
  -h|--help) sed -n '2,13p' "$0" | sed 's/^# \{0,1\}//' ;;
  *) echo "deploy/update.sh [run|on [minutes]|off|status]" >&2; exit 2 ;;
esac

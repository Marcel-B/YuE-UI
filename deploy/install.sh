#!/bin/sh
# Publishes YueUI and runs it as a LaunchAgent of the logged-in user: it starts at login, comes back after a
# crash and listens on 127.0.0.1:5090 only. `tailscale serve --bg --https=8443 5090` then makes it reachable in the tailnet.
# Run it again after pulling changes; deploy/uninstall.sh removes the agent.
set -eu
cd "$(dirname "$0")/.."

LABEL=de.bvelop.yueui
TARGET="$HOME/Library/Application Support/YueUI"
PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"
DOTNET="$(command -v dotnet)"

dotnet publish src/YueUI.Api -c Release -o "$TARGET/app"
mkdir -p "$TARGET/logs" "$HOME/Library/LaunchAgents"

# A LaunchAgent, not a daemon: the worker needs the user's session for Metal and the Neural Engine.
cat > "$PLIST" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key>
  <string>$LABEL</string>
  <key>ProgramArguments</key>
  <array>
    <string>$DOTNET</string>
    <string>$TARGET/app/YueUI.Api.dll</string>
  </array>
  <key>WorkingDirectory</key>
  <string>$TARGET/app</string>
  <key>RunAtLoad</key>
  <true/>
  <key>KeepAlive</key>
  <true/>
  <key>StandardOutPath</key>
  <string>$TARGET/logs/yueui.log</string>
  <key>StandardErrorPath</key>
  <string>$TARGET/logs/yueui.log</string>
</dict>
</plist>
EOF

# bootout returns before the old instance is gone, and bootstrapping a label that is still loaded fails with
# "Input/output error": wait until launchd has let go of it.
DOMAIN="gui/$(id -u)"
if launchctl bootout "$DOMAIN/$LABEL" 2>/dev/null; then
  for _ in $(seq 1 50); do
    launchctl print "$DOMAIN/$LABEL" >/dev/null 2>&1 || break
    sleep 0.2
  done
fi
launchctl bootstrap "$DOMAIN" "$PLIST"
echo "YueUI runs on http://127.0.0.1:5090 (log: $TARGET/logs/yueui.log)."
echo "For the tailnet: tailscale serve --bg --https=8443 5090"

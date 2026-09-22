#!/bin/sh
# Stops the YueUI LaunchAgent and removes it and the published app. The songs stay where they are.
set -eu
LABEL=de.bvelop.yueui
launchctl bootout "gui/$(id -u)/$LABEL" 2>/dev/null || true
rm -f "$HOME/Library/LaunchAgents/$LABEL.plist"
rm -rf "$HOME/Library/Application Support/YueUI/app"
echo "YueUI removed. If you set up tailscale serve for it: tailscale serve --https=8443 off"

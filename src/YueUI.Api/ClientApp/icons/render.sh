#!/bin/sh
# Renders the app icons in public/ from these sources (needs rsvg-convert, from librsvg).
# favicon.svg in public/ is the source of the rounded manifest icons; iOS rounds the apple-touch-icon itself,
# so that one is full-bleed and opaque, and the maskable one keeps the glyph inside Android's 80 % safe zone.
# The badge is the glyph alone: Android draws a notification badge from its alpha channel only.
set -e
cd "$(dirname "$0")"
pub=../public
rsvg-convert -w 192 -h 192 "$pub/favicon.svg" -o "$pub/icon-192.png"
rsvg-convert -w 512 -h 512 "$pub/favicon.svg" -o "$pub/icon-512.png"
rsvg-convert -w 180 -h 180 apple-touch-icon.svg -o "$pub/apple-touch-icon.png"
rsvg-convert -w 512 -h 512 icon-maskable.svg -o "$pub/icon-maskable-512.png"
rsvg-convert -w 96 -h 96 badge.svg -o "$pub/badge-96.png"

import { barAt } from './score'
import type { ScoreDocument, VoiceTrack } from './types'

/**
 * Draws the score as a piano roll on a canvas: a bar ruler with the song sections, a row of chord symbols and
 * one lane per track. Canvas rather than SVG because a song easily has several thousand notes, which as DOM
 * nodes would make scrolling crawl. Only the visible slice is drawn, so the cost does not grow with the song.
 */

export const RULER_HEIGHT = 26
export const CHORD_HEIGHT = 22
export const LANE_HEIGHT = 58
export const LANE_GAP = 4
export const GUTTER_WIDTH = 92

/** One lane per track, in the order the score lists them. */
export interface Lane {
  track: VoiceTrack
  /** Lowest and highest note, padded a little so nothing touches the edge. */
  low: number
  high: number
  top: number
}

export function lanesOf(voices: VoiceTrack[]): Lane[] {
  let top = RULER_HEIGHT + CHORD_HEIGHT
  return voices.map((track) => {
    const pitches = track.notes.map((n) => n.noteNumber)
    // An empty track still gets a lane, so its name stays visible; the range is then arbitrary.
    const low = pitches.length > 0 ? Math.min(...pitches) : 60
    const high = pitches.length > 0 ? Math.max(...pitches) : 72
    const lane: Lane = { track, low: low - 1, high: Math.max(high + 1, low + 6), top }
    top += LANE_HEIGHT + LANE_GAP
    return lane
  })
}

export function contentHeight(lanes: Lane[]): number {
  return RULER_HEIGHT + CHORD_HEIGHT + lanes.length * (LANE_HEIGHT + LANE_GAP)
}

/** Colours taken from the stylesheet, so the roll follows the light and dark themes. */
interface Palette {
  text: string
  muted: string
  border: string
  surface: string
  sunken: string
  accent: string
}

function palette(canvas: HTMLCanvasElement): Palette {
  // The variables follow PrimeVue's tokens, which are written as light-dark(...) and color-mix(...); a canvas takes
  // neither. A probe element lets the browser resolve them to plain rgb() for the current colour scheme.
  const probe = document.createElement('span')
  probe.style.display = 'none'
  canvas.parentElement?.append(probe)
  const read = (name: string, fallback: string) => {
    probe.style.color = fallback
    probe.style.color = `var(${name}, ${fallback})`
    return getComputedStyle(probe).color || fallback
  }
  const colours = {
    text: read('--text', '#16181d'),
    muted: read('--text-muted', '#5d6474'),
    border: read('--border', '#e3e6ee'),
    surface: read('--surface', '#ffffff'),
    sunken: read('--surface-sunken', '#f1f3f8'),
    accent: read('--accent', '#4f46e5'),
  }
  probe.remove()
  return colours
}

/** One hue per track, distinct in both themes; tracks beyond the palette start over. */
const TRACK_HUES = [255, 15, 200, 140, 40, 300, 175]

export function trackColour(index: number, alpha = 1): string {
  return `hsl(${TRACK_HUES[index % TRACK_HUES.length]} 65% 55% / ${alpha})`
}

export interface DrawOptions {
  score: ScoreDocument
  lanes: Lane[]
  /** Horizontal scale; the width one bar takes. */
  pxPerBar: number
  /** Leftmost visible tick position. */
  scrollTicks: number
  /** Playhead in ticks, or null when stopped. */
  playhead: number | null
}

/** Ticks per bar at a position, honouring meter changes. */
function measureTicks(score: ScoreDocument, ticks: number): number {
  let signature = score.timeSignatures[0]
  for (const candidate of score.timeSignatures) {
    if (candidate.startTicks <= ticks) {
      signature = candidate
    }
  }
  if (!signature) {
    return 4 * score.ticksPerQuarterNote
  }
  return ((4 * score.ticksPerQuarterNote) / signature.denominator) * signature.numerator
}

/** Pixels per tick at a position. Meter changes alter how wide a bar is in ticks, not on screen. */
export function scaleAt(score: ScoreDocument, ticks: number, pxPerBar: number): number {
  return pxPerBar / measureTicks(score, ticks)
}

/**
 * Total width of the song at this zoom. Summed per meter segment, so a song that changes meter keeps every
 * bar the same width on screen.
 */
export function totalWidth(score: ScoreDocument, pxPerBar: number): number {
  return (barAt(score, score.lengthTicks) - 1) * pxPerBar
}

/** Tick position of an x coordinate in the scrolling content (without the gutter). */
export function ticksAtX(score: ScoreDocument, x: number, pxPerBar: number): number {
  let bars = Math.max(0, x / pxPerBar)
  let ticks = 0
  for (let i = 0; i < score.timeSignatures.length; i++) {
    const signature = score.timeSignatures[i]!
    const next = score.timeSignatures[i + 1]?.startTicks ?? score.lengthTicks
    const size = ((4 * score.ticksPerQuarterNote) / signature.denominator) * signature.numerator
    const barsHere = (next - signature.startTicks) / size
    if (bars <= barsHere) {
      return signature.startTicks + bars * size
    }
    bars -= barsHere
    ticks = next
  }
  return Math.min(ticks + bars * measureTicks(score, ticks), score.lengthTicks)
}

/** X coordinate of a tick position in the scrolling content. */
export function xAtTicks(score: ScoreDocument, ticks: number, pxPerBar: number): number {
  let x = 0
  for (let i = 0; i < score.timeSignatures.length; i++) {
    const signature = score.timeSignatures[i]!
    const next = score.timeSignatures[i + 1]?.startTicks ?? Number.POSITIVE_INFINITY
    const size = ((4 * score.ticksPerQuarterNote) / signature.denominator) * signature.numerator
    if (ticks < next) {
      return x + ((ticks - signature.startTicks) / size) * pxPerBar
    }
    x += ((next - signature.startTicks) / size) * pxPerBar
  }
  return x
}

export function draw(canvas: HTMLCanvasElement, options: DrawOptions): void {
  const { score, lanes, pxPerBar, scrollTicks, playhead } = options
  const ratio = window.devicePixelRatio || 1
  const width = canvas.clientWidth
  const height = canvas.clientHeight
  if (width === 0 || height === 0) {
    return
  }
  if (canvas.width !== Math.round(width * ratio) || canvas.height !== Math.round(height * ratio)) {
    canvas.width = Math.round(width * ratio)
    canvas.height = Math.round(height * ratio)
  }

  const ctx = canvas.getContext('2d')
  if (!ctx) {
    return
  }
  ctx.setTransform(ratio, 0, 0, ratio, 0, 0)
  const colours = palette(canvas)
  ctx.clearRect(0, 0, width, height)
  ctx.fillStyle = colours.surface
  ctx.fillRect(0, 0, width, height)

  const originX = xAtTicks(score, scrollTicks, pxPerBar)
  const lastBar = barAt(score, score.lengthTicks) - 1
  const toScreen = (ticks: number) => GUTTER_WIDTH + xAtTicks(score, ticks, pxPerBar) - originX
  const visible = (from: number, to: number) => to >= GUTTER_WIDTH && from <= width

  ctx.font = '11px system-ui, sans-serif'
  ctx.textBaseline = 'middle'

  // ---- lanes: background, note rows -------------------------------------------------------------
  lanes.forEach((lane, index) => {
    ctx.fillStyle = colours.sunken
    ctx.fillRect(GUTTER_WIDTH, lane.top, width - GUTTER_WIDTH, LANE_HEIGHT)

    const span = Math.max(1, lane.high - lane.low)
    const noteHeight = Math.max(2, LANE_HEIGHT / span - 1)
    ctx.fillStyle = trackColour(index)
    for (const note of lane.track.notes) {
      const x = toScreen(note.startTicks)
      const w = Math.max(
        1.5,
        xAtTicks(score, note.startTicks + note.durationTicks, pxPerBar) - xAtTicks(score, note.startTicks, pxPerBar),
      )
      if (!visible(x, x + w)) {
        continue
      }
      const y = lane.top + LANE_HEIGHT - ((note.noteNumber - lane.low) / span) * LANE_HEIGHT
      ctx.globalAlpha = 0.35 + 0.65 * ((note.velocity ?? 96) / 127)
      ctx.fillRect(
        Math.max(GUTTER_WIDTH, x),
        y - noteHeight / 2,
        Math.min(w, width - Math.max(GUTTER_WIDTH, x)),
        noteHeight,
      )
    }
    ctx.globalAlpha = 1
  })

  // ---- bar lines over the lanes -----------------------------------------------------------------
  const laneTop = RULER_HEIGHT + CHORD_HEIGHT
  const laneBottom = laneTop + lanes.length * (LANE_HEIGHT + LANE_GAP)
  // A label every bar is unreadable when zoomed out; step up in musical amounts.
  const step = pxPerBar >= 48 ? 1 : pxPerBar >= 20 ? 4 : pxPerBar >= 8 ? 8 : 16
  ctx.strokeStyle = colours.border
  ctx.lineWidth = 1
  for (let bar = 1; bar <= lastBar + 1; bar += step) {
    const x = Math.round(GUTTER_WIDTH + (bar - 1) * pxPerBar - originX) + 0.5
    if (x < GUTTER_WIDTH || x > width) {
      continue
    }
    ctx.beginPath()
    ctx.moveTo(x, laneTop)
    ctx.lineTo(x, laneBottom)
    ctx.stroke()
  }

  // ---- ruler with the sections ------------------------------------------------------------------
  ctx.fillStyle = colours.surface
  ctx.fillRect(0, 0, width, RULER_HEIGHT + CHORD_HEIGHT)
  score.sections.forEach((section, index) => {
    const end = score.sections[index + 1]?.startTicks ?? score.lengthTicks
    const x = toScreen(section.startTicks)
    const w = toScreen(end) - x
    if (!visible(x, x + w)) {
      return
    }
    const left = Math.max(GUTTER_WIDTH, x)
    ctx.fillStyle = trackColour(index, 0.18)
    ctx.fillRect(left, 2, Math.min(w - (left - x), width - left), RULER_HEIGHT - 6)
    ctx.fillStyle = colours.text
    ctx.save()
    ctx.beginPath()
    ctx.rect(left + 4, 0, Math.max(0, Math.min(w - (left - x), width - left) - 8), RULER_HEIGHT)
    ctx.clip()
    ctx.fillText(section.name, left + 6, RULER_HEIGHT / 2 - 1)
    ctx.restore()
  })

  ctx.fillStyle = colours.muted
  for (let bar = 1; bar <= lastBar; bar += step) {
    const x = GUTTER_WIDTH + (bar - 1) * pxPerBar - originX
    if (x < GUTTER_WIDTH || x > width - 12) {
      continue
    }
    ctx.fillText(String(bar), x + 3, RULER_HEIGHT + CHORD_HEIGHT - 11)
  }

  // ---- chord symbols ----------------------------------------------------------------------------
  if (pxPerBar >= 24) {
    ctx.fillStyle = colours.text
    let lastRight = GUTTER_WIDTH
    for (const chord of score.chords) {
      const x = toScreen(chord.startTicks)
      if (x < lastRight + 4 || x > width - 10) {
        continue
      }
      ctx.fillText(chord.text, x + 2, RULER_HEIGHT + 10)
      lastRight = x + ctx.measureText(chord.text).width + 2
    }
  }

  // ---- playhead ---------------------------------------------------------------------------------
  if (playhead !== null) {
    const x = Math.round(toScreen(playhead)) + 0.5
    if (x >= GUTTER_WIDTH && x <= width) {
      ctx.strokeStyle = colours.accent
      ctx.lineWidth = 2
      ctx.beginPath()
      ctx.moveTo(x, 0)
      ctx.lineTo(x, laneBottom)
      ctx.stroke()
    }
  }

  // ---- gutter with the track names, drawn last so nothing scrolls under it ----------------------
  ctx.fillStyle = colours.surface
  ctx.fillRect(0, 0, GUTTER_WIDTH, height)
  ctx.strokeStyle = colours.border
  ctx.lineWidth = 1
  ctx.beginPath()
  ctx.moveTo(GUTTER_WIDTH + 0.5, 0)
  ctx.lineTo(GUTTER_WIDTH + 0.5, laneBottom)
  ctx.stroke()

  lanes.forEach((lane, index) => {
    ctx.fillStyle = trackColour(index)
    ctx.fillRect(4, lane.top + 4, 3, LANE_HEIGHT - 8)
    ctx.fillStyle = colours.text
    ctx.save()
    ctx.beginPath()
    ctx.rect(12, lane.top, GUTTER_WIDTH - 16, LANE_HEIGHT)
    ctx.clip()
    ctx.fillText(lane.track.id, 12, lane.top + LANE_HEIGHT / 2)
    ctx.restore()
  })
}

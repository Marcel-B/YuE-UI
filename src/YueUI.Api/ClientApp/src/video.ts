import { ref } from 'vue'
import { drawCover, type ExportTarget } from './export'
import type { VideoLayerImages } from './api'
import type { VideoEffect, VideoFormat } from './types'

/**
 * A music video of a song: the browser draws the still layers (blurred background, cover, title, a field of particles)
 * as PNGs of the frame's size, the server animates them with ffmpeg (Video/FfmpegVideoRenderer.cs) and encodes the
 * MP4. `VideoDialog.vue` (one, in App.vue) asks for the settings; the library and the song menu open it.
 */
export const videoTarget = ref<ExportTarget | null>(null)

export function openVideo(target: ExportTarget): void {
  videoTarget.value = target
}

export const videoFormats: VideoFormat[] = ['landscape', 'portrait']
export const videoEffects: VideoEffect[] = ['bars', 'wave', 'none']

/**
 * Where things go; width, height and the band must match the server's `VideoLayout`, which puts the analyzer or the
 * waveform into the band. Cover and title are the browser's own.
 */
interface Layout {
  width: number
  height: number
  bandY: number
  bandHeight: number
  coverX: number
  coverY: number
  coverSize: number
  /** Baseline of the title's first line, and its largest size. */
  titleY: number
  titleSize: number
}

export const videoLayouts: Record<VideoFormat, Layout> = {
  landscape: {
    width: 1920,
    height: 1080,
    bandY: 850,
    bandHeight: 180,
    coverX: 680,
    coverY: 90,
    coverSize: 560,
    titleY: 735,
    titleSize: 64,
  },
  // Everything above the bottom fifth, which the apps cover with caption and buttons.
  portrait: {
    width: 1080,
    height: 1920,
    bandY: 1270,
    bandHeight: 220,
    coverX: 160,
    coverY: 230,
    coverSize: 760,
    titleY: 1100,
    titleSize: 68,
  },
}

export interface VideoSettings {
  format: VideoFormat
  effect: VideoEffect
  particles: boolean
  showTitle: boolean
}

const storageKey = 'yue-ui.video'

/** Each browser remembers the last choice: a channel tends to post the same kind of video. */
export function loadVideoSettings(): VideoSettings {
  const defaults: VideoSettings = { format: 'landscape', effect: 'bars', particles: true, showTitle: true }
  try {
    const stored = JSON.parse(localStorage.getItem(storageKey) ?? '{}') as Partial<VideoSettings>
    return {
      format: videoFormats.includes(stored.format as VideoFormat) ? (stored.format as VideoFormat) : defaults.format,
      effect: videoEffects.includes(stored.effect as VideoEffect) ? (stored.effect as VideoEffect) : defaults.effect,
      particles: typeof stored.particles === 'boolean' ? stored.particles : defaults.particles,
      showTitle: typeof stored.showTitle === 'boolean' ? stored.showTitle : defaults.showTitle,
    }
  } catch {
    return defaults
  }
}

export function saveVideoSettings(settings: VideoSettings): void {
  try {
    localStorage.setItem(storageKey, JSON.stringify(settings))
  } catch {
    // private mode: only this video has them
  }
}

/** The layers as canvases; `toPngs` turns them into what the server takes. */
export interface VideoLayers {
  background: HTMLCanvasElement
  cover: HTMLCanvasElement
  title: HTMLCanvasElement | null
  particles: HTMLCanvasElement | null
}

/**
 * Draws every layer for the song. The cover is the song's own, else the drawn one the export would use; a line under
 * the title names the artist when the export dialog has one.
 */
export async function drawVideoLayers(
  target: ExportTarget,
  settings: VideoSettings,
  subtitle: string,
): Promise<VideoLayers> {
  const layout = videoLayouts[settings.format]
  const image = await coverImage(target)
  return {
    background: drawBackground(layout, image),
    cover: drawCoverLayer(layout, image),
    title: settings.showTitle ? drawTitle(layout, target.title || 'Tonwerk', subtitle) : null,
    particles: settings.particles ? drawParticles(layout, target.songId) : null,
  }
}

export async function toPngs(layers: VideoLayers): Promise<VideoLayerImages> {
  return {
    background: await png(layers.background),
    cover: await png(layers.cover),
    title: layers.title ? await png(layers.title) : null,
    particles: layers.particles ? await png(layers.particles) : null,
  }
}

/**
 * One still of the video as it will look, for the dialog: the layers on top of each other and, in the band, bars or
 * a wave of made-up heights where ffmpeg will draw the music. Scaled down, since a phone shows it a few hundred pixels
 * wide.
 */
export function drawPreview(layers: VideoLayers, settings: VideoSettings): string {
  const layout = videoLayouts[settings.format]
  const scale = 0.3
  const { canvas, context } = frame(layout.width * scale, layout.height * scale)
  context.scale(scale, scale)
  context.drawImage(layers.background, 0, 0)
  if (layers.particles) {
    context.drawImage(layers.particles, 0, 0)
  }
  if (settings.effect !== 'none') {
    context.fillStyle = accent
    const random = seeded(7)
    if (settings.effect === 'bars') {
      for (let x = 0; x + barWidth <= layout.width; x += barPitch) {
        // Louder in the lows, like a song's spectrum.
        const height = layout.bandHeight * (0.15 + 0.75 * random() * (1 - (0.7 * x) / layout.width))
        context.fillRect(x, layout.bandY + layout.bandHeight - height, barWidth, height)
      }
    } else {
      const middle = layout.bandY + layout.bandHeight / 2
      context.beginPath()
      context.moveTo(0, middle)
      for (let x = 0; x <= layout.width; x += 6) {
        const amplitude = (layout.bandHeight / 2) * (0.2 + 0.6 * Math.abs(Math.sin(x / 37) * Math.sin(x / 211)))
        context.lineTo(x, middle - amplitude * (x % 12 === 0 ? 1 : -1))
      }
      context.lineWidth = 3
      context.strokeStyle = accent
      context.stroke()
    }
  }
  context.drawImage(layers.cover, 0, 0)
  if (layers.title) {
    context.drawImage(layers.title, 0, 0)
  }
  return canvas.toDataURL('image/jpeg', 0.85)
}

/** The server's `FfmpegVideoRenderer.Accent` and the bars' pitch and width (`VideoLayout`), for the preview. */
const accent = '#34d399'
const barPitch = 20
const barWidth = 14

async function coverImage(target: ExportTarget): Promise<HTMLImageElement> {
  const url = target.cover ?? URL.createObjectURL(await drawCover(target))
  try {
    const image = new Image()
    image.src = url
    await image.decode()
    return image
  } finally {
    if (!target.cover) {
      URL.revokeObjectURL(url)
    }
  }
}

/**
 * The cover blown up to fill the frame, blurred and darkened, so every song gets a background in its own colours.
 * Blurred by drawing it tiny and scaling it up, since Safari's canvas long had no `filter`.
 */
function drawBackground(layout: Layout, image: HTMLImageElement): HTMLCanvasElement {
  const small = frame(12, Math.round((12 * layout.height) / layout.width))
  small.context.imageSmoothingQuality = 'high'
  drawCovering(small.context, image, small.canvas.width, small.canvas.height)
  const { canvas, context } = frame(layout.width, layout.height)
  context.imageSmoothingEnabled = true
  context.imageSmoothingQuality = 'high'
  context.drawImage(small.canvas, 0, 0, layout.width, layout.height)
  context.fillStyle = 'rgb(0 0 0 / 0.45)'
  context.fillRect(0, 0, layout.width, layout.height)
  // Darker towards the bottom, where the band and the title need contrast.
  const shade = context.createLinearGradient(0, 0, 0, layout.height)
  shade.addColorStop(0, 'rgb(0 0 0 / 0)')
  shade.addColorStop(0.55, 'rgb(0 0 0 / 0.1)')
  shade.addColorStop(1, 'rgb(0 0 0 / 0.55)')
  context.fillStyle = shade
  context.fillRect(0, 0, layout.width, layout.height)
  const vignette = context.createRadialGradient(
    layout.width / 2,
    layout.height / 2,
    Math.min(layout.width, layout.height) * 0.35,
    layout.width / 2,
    layout.height / 2,
    Math.max(layout.width, layout.height) * 0.75,
  )
  vignette.addColorStop(0, 'rgb(0 0 0 / 0)')
  vignette.addColorStop(1, 'rgb(0 0 0 / 0.45)')
  context.fillStyle = vignette
  context.fillRect(0, 0, layout.width, layout.height)
  return canvas
}

function drawCoverLayer(layout: Layout, image: HTMLImageElement): HTMLCanvasElement {
  const { canvas, context } = frame(layout.width, layout.height)
  const { coverX: x, coverY: y, coverSize: size } = layout
  const radius = size * 0.04
  context.save()
  context.shadowColor = 'rgb(0 0 0 / 0.55)'
  context.shadowBlur = size * 0.1
  context.shadowOffsetY = size * 0.03
  context.fillStyle = '#000'
  roundedRect(context, x, y, size, size, radius)
  context.fill()
  context.restore()
  context.save()
  roundedRect(context, x, y, size, size, radius)
  context.clip()
  drawCovering(context, image, size, size, x, y)
  context.restore()
  return canvas
}

function drawTitle(layout: Layout, title: string, subtitle: string): HTMLCanvasElement {
  const { canvas, context } = frame(layout.width, layout.height)
  const width = layout.width - 2 * 100
  const font = (weight: number, size: number) => `${weight} ${size}px system-ui, -apple-system, "Segoe UI", sans-serif`
  let size = layout.titleSize
  let lines: string[] = []
  // As large as fits in two lines.
  for (; size >= 36; size -= 4) {
    context.font = font(700, size)
    lines = wrap(context, title, width)
    if (lines.length <= 2 && lines.every((line) => context.measureText(line).width <= width)) {
      break
    }
  }
  context.textAlign = 'center'
  context.textBaseline = 'alphabetic'
  context.shadowColor = 'rgb(0 0 0 / 0.6)'
  context.shadowBlur = 18
  context.shadowOffsetY = 3
  context.fillStyle = 'rgb(255 255 255 / 0.97)'
  let y = layout.titleY
  for (const line of lines.slice(0, 2)) {
    context.fillText(line, layout.width / 2, y, width)
    y += size * 1.15
  }
  if (subtitle) {
    context.font = font(500, Math.round(size * 0.55))
    context.fillStyle = 'rgb(255 255 255 / 0.75)'
    context.fillText(subtitle, layout.width / 2, y - size * 0.15 + size * 0.45, width)
  }
  return canvas
}

/**
 * Specks of light and a few out-of-focus ones, in the app's mint and white. The server scrolls two copies of the
 * picture stacked, so whatever crosses the top or bottom edge is drawn on the other side as well. Seeded by the song,
 * so the same song gets the same sky.
 */
function drawParticles(layout: Layout, songId: string): HTMLCanvasElement {
  const { canvas, context } = frame(layout.width, layout.height)
  const random = seeded(hashOf(songId))
  const count = Math.round((layout.width * layout.height) / 9000)
  const colours = ['255 255 255', '209 250 229', '167 243 208']
  for (let i = 0; i < count; i++) {
    const bokeh = random() < 0.12
    const radius = bokeh ? 6 + random() * 12 : 1 + random() * 2.5
    const alpha = bokeh ? 0.12 + random() * 0.2 : 0.45 + random() * 0.5
    const colour = colours[Math.floor(random() * colours.length)]!
    const x = random() * layout.width
    const y = random() * layout.height
    const reach = radius * 3
    for (const shift of [-layout.height, 0, layout.height]) {
      const cy = y + shift
      if (cy + reach < 0 || cy - reach > layout.height) {
        continue
      }
      const glow = context.createRadialGradient(x, cy, 0, x, cy, reach)
      glow.addColorStop(0, `rgb(${colour} / ${alpha})`)
      glow.addColorStop(bokeh ? 0.3 : 0.25, `rgb(${colour} / ${alpha * (bokeh ? 0.8 : 0.6)})`)
      glow.addColorStop(1, `rgb(${colour} / 0)`)
      context.fillStyle = glow
      context.fillRect(x - reach, cy - reach, reach * 2, reach * 2)
    }
  }
  return canvas
}

/** Draws the image cut to fill the box, like `object-fit: cover`. */
function drawCovering(
  context: CanvasRenderingContext2D,
  image: HTMLImageElement,
  width: number,
  height: number,
  x = 0,
  y = 0,
): void {
  const scale = Math.max(width / image.naturalWidth, height / image.naturalHeight)
  const sourceWidth = width / scale
  const sourceHeight = height / scale
  context.drawImage(
    image,
    (image.naturalWidth - sourceWidth) / 2,
    (image.naturalHeight - sourceHeight) / 2,
    sourceWidth,
    sourceHeight,
    x,
    y,
    width,
    height,
  )
}

function roundedRect(
  context: CanvasRenderingContext2D,
  x: number,
  y: number,
  width: number,
  height: number,
  radius: number,
): void {
  context.beginPath()
  if (typeof context.roundRect === 'function') {
    context.roundRect(x, y, width, height, radius)
  } else {
    context.rect(x, y, width, height)
  }
}

function frame(width: number, height: number): { canvas: HTMLCanvasElement; context: CanvasRenderingContext2D } {
  const canvas = document.createElement('canvas')
  canvas.width = width
  canvas.height = height
  const context = canvas.getContext('2d')
  if (!context) {
    throw new Error('No canvas to draw the video.')
  }
  return { canvas, context }
}

function png(canvas: HTMLCanvasElement): Promise<Blob> {
  return new Promise((resolve, reject) =>
    canvas.toBlob((blob) => (blob ? resolve(blob) : reject(new Error('The picture could not be drawn.'))), 'image/png'),
  )
}

function wrap(context: CanvasRenderingContext2D, text: string, width: number): string[] {
  const lines: string[] = []
  let line = ''
  for (const word of text.split(/\s+/).filter((w) => w !== '')) {
    const candidate = line ? `${line} ${word}` : word
    if (line && context.measureText(candidate).width > width) {
      lines.push(line)
      line = word
    } else {
      line = candidate
    }
  }
  if (line) {
    lines.push(line)
  }
  return lines
}

/** Mulberry32: a small generator that gives the same numbers for the same seed. */
function seeded(seed: number): () => number {
  let state = seed >>> 0
  return () => {
    state = (state + 0x6d2b79f5) >>> 0
    let value = state
    value = Math.imul(value ^ (value >>> 15), value | 1)
    value ^= value + Math.imul(value ^ (value >>> 7), value | 61)
    return ((value ^ (value >>> 14)) >>> 0) / 4294967296
  }
}

/** FNV-1a, as for the drawn cover's colours. */
function hashOf(text: string): number {
  let hash = 0x811c9dc5
  for (let i = 0; i < text.length; i++) {
    hash ^= text.charCodeAt(i)
    hash = Math.imul(hash, 0x01000193)
  }
  return hash >>> 0
}

<script setup lang="ts">
import { onBeforeUnmount, onMounted, useTemplateRef } from 'vue'
import { t } from '../../logic/i18n'

/**
 * An oscilloscope: the waveform of what a sound plays, so a change of shape (pulse width, a modulator's index, the
 * filter) can be seen as well as heard. Triggered on a rising zero crossing, so a held note stands still instead of
 * running through. It reads the analysers `sources` names on every frame, since the audio behind them is only built
 * on the first note, and several when a track sounds in more than one output.
 */
const props = defineProps<{ sources: () => AnalyserNode[] }>()

const canvas = useTemplateRef<HTMLCanvasElement>('canvas')
let frame = 0
let buffer = new Float32Array(2048)
const sum = new Float32Array(2048)

function draw(): void {
  frame = requestAnimationFrame(draw)
  const element = canvas.value
  // Hidden (another page, a closed section): nothing to draw.
  if (!element || element.offsetParent === null) {
    return
  }
  const ratio = window.devicePixelRatio || 1
  const width = Math.round(element.clientWidth * ratio)
  const height = Math.round(element.clientHeight * ratio)
  if (element.width !== width || element.height !== height) {
    element.width = width
    element.height = height
  }
  const context = element.getContext('2d')
  if (!context) {
    return
  }

  const analysers = props.sources()
  const length = Math.min(sum.length, ...analysers.map((analyser) => analyser.fftSize))
  sum.fill(0, 0, length)
  for (const analyser of analysers) {
    if (buffer.length < analyser.fftSize) {
      buffer = new Float32Array(analyser.fftSize)
    }
    const samples = buffer.subarray(0, analyser.fftSize)
    analyser.getFloatTimeDomainData(samples)
    for (let i = 0; i < length; i++) {
      sum[i]! += samples[i]!
    }
  }

  // Half the buffer is shown; the trigger is looked for in the first half, so the view always has enough after it.
  const shown = Math.floor(length / 2)
  let start = 0
  for (let i = 1; i < length - shown; i++) {
    if (sum[i - 1]! <= 0 && sum[i]! > 0) {
      start = i
      break
    }
  }
  let peak = 0
  for (let i = 0; i < length; i++) {
    peak = Math.max(peak, Math.abs(sum[i]!))
  }
  // Quiet sounds are scaled up to be seen, but not a hum of noise to full height.
  const scale = peak > 0.02 ? 0.9 / peak : 0

  // The theme's colours, resolved by the canvas's own style (a custom property read directly may still be a var()).
  const style = getComputedStyle(element)
  context.clearRect(0, 0, width, height)
  context.strokeStyle = style.borderTopColor
  context.lineWidth = ratio
  context.beginPath()
  context.moveTo(0, height / 2)
  context.lineTo(width, height / 2)
  context.stroke()

  if (analysers.length === 0 || shown === 0) {
    return
  }
  context.strokeStyle = style.color
  context.lineWidth = 2 * ratio
  context.lineJoin = 'round'
  context.beginPath()
  for (let x = 0; x < width; x++) {
    const sample = sum[start + Math.floor((x / width) * shown)]! * scale
    const y = (1 - sample) * (height / 2)
    if (x === 0) {
      context.moveTo(x, y)
    } else {
      context.lineTo(x, y)
    }
  }
  context.stroke()
}

onMounted(() => {
  frame = requestAnimationFrame(draw)
})
onBeforeUnmount(() => cancelAnimationFrame(frame))
</script>

<template>
  <canvas ref="canvas" class="scope" role="img" :aria-label="t('synthScope')" />
</template>

<style scoped>
.scope {
  display: block;
  width: 100%;
  height: 5.5rem;
  border-radius: 0.5rem;
  background: var(--surface-sunken);
  /* Not shown: the colours the drawing reads. */
  color: var(--accent);
  border-color: var(--border);
}
</style>

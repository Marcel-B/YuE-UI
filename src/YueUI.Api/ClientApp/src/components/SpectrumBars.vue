<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, useTemplateRef, watch } from 'vue'
import { readBands, reducedMotion, resolveColour, type SpectrumSource } from '../spectrum'

/**
 * A spectrum analyzer: log-spaced bars that rise at once and fall back slowly, as a hardware one does. It draws only
 * while its sources play and the page is visible, then lets the bars settle and stops, so an idle page costs nothing.
 */
const props = withDefaults(
  defineProps<{
    sources: SpectrumSource[]
    bars?: number
    /** Little caps that hold the highest level for a moment; worth it only at a size where they can be seen. */
    peaks?: boolean
  }>(),
  { bars: 24, peaks: false },
)

const canvas = useTemplateRef<HTMLCanvasElement>('canvas')
const playing = computed(() => props.sources.some((source) => source.playing.value))
const active = computed(() => playing.value && !reducedMotion.value)

let bands = new Float32Array(props.bars)
let shown = new Float32Array(props.bars)
let caps = new Float32Array(props.bars)
let capHold = new Float32Array(props.bars)
let frame = 0
let colour = ''
let capColour = ''

watch(
  () => props.bars,
  (count) => {
    bands = new Float32Array(count)
    shown = new Float32Array(count)
    caps = new Float32Array(count)
    capHold = new Float32Array(count)
  },
)

function resize(): boolean {
  const element = canvas.value
  if (!element) {
    return false
  }
  const ratio = window.devicePixelRatio || 1
  const width = Math.round(element.clientWidth * ratio)
  const height = Math.round(element.clientHeight * ratio)
  if (width === 0 || height === 0) {
    return false
  }
  if (element.width !== width || element.height !== height) {
    element.width = width
    element.height = height
  }
  return true
}

function draw(): void {
  const element = canvas.value
  const context = element?.getContext('2d')
  if (!element || !context || !resize()) {
    return
  }
  const { width, height } = element
  context.clearRect(0, 0, width, height)
  const gap = Math.max(1, Math.round(width / props.bars / 5))
  const barWidth = (width - gap * (props.bars - 1)) / props.bars
  const capHeight = Math.max(1, Math.round(height / 40))
  context.fillStyle = colour
  for (let index = 0; index < props.bars; index++) {
    // Even silence keeps a sliver, so the analyzer reads as one while nothing plays.
    const barHeight = Math.max(capHeight, shown[index]! * height)
    context.fillRect(index * (barWidth + gap), height - barHeight, barWidth, barHeight)
  }
  if (props.peaks) {
    context.fillStyle = capColour
    for (let index = 0; index < props.bars; index++) {
      if (caps[index]! > 0.02) {
        const y = height - caps[index]! * height - capHeight * 2
        context.fillRect(index * (barWidth + gap), Math.max(0, y), barWidth, capHeight)
      }
    }
  }
}

function step(): void {
  const live = active.value && document.visibilityState === 'visible'
  if (live) {
    readBands(
      props.sources.flatMap((source) => source.analysers()),
      bands,
    )
  } else {
    bands.fill(0)
  }
  let moving = false
  for (let index = 0; index < props.bars; index++) {
    // Up at once, down at a steady fall: a bar that jumps both ways is hard to read.
    const target = bands[index]!
    const value = target > shown[index]! ? target : Math.max(target, shown[index]! - 0.025)
    shown[index] = value
    if (value >= caps[index]!) {
      caps[index] = value
      capHold[index] = 30
    } else if (capHold[index]! > 0) {
      capHold[index]!--
    } else {
      caps[index] = Math.max(0, caps[index]! - 0.01)
    }
    moving ||= value > 0 || caps[index]! > 0
  }
  draw()
  frame = live || moving ? requestAnimationFrame(step) : 0
}

function start(): void {
  if (frame === 0 && canvas.value) {
    // Resolved on every start: the colour scheme may have changed since.
    colour = resolveColour(canvas.value.parentElement ?? document.body, '--accent', '#10b981')
    capColour = resolveColour(canvas.value.parentElement ?? document.body, '--text-muted', '#64748b')
    frame = requestAnimationFrame(step)
  }
}

function onVisibility(): void {
  if (document.visibilityState === 'visible' && active.value) {
    start()
  }
}

watch(active, (value) => value && start())

let observer: ResizeObserver | null = null
onMounted(() => {
  document.addEventListener('visibilitychange', onVisibility)
  observer = new ResizeObserver(() => {
    if (frame === 0) {
      draw()
    }
  })
  if (canvas.value) {
    observer.observe(canvas.value)
    colour = resolveColour(canvas.value.parentElement ?? document.body, '--accent', '#10b981')
  }
  if (active.value) {
    start()
  }
})

onBeforeUnmount(() => {
  cancelAnimationFrame(frame)
  frame = 0
  observer?.disconnect()
  document.removeEventListener('visibilitychange', onVisibility)
})
</script>

<template>
  <canvas ref="canvas" class="block h-full w-full" aria-hidden="true" />
</template>

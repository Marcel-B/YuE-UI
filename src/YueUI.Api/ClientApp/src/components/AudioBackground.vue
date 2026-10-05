<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, useTemplateRef, watch } from 'vue'
import { expanded } from '../player'
import { allSources, anythingPlaying, readBands, reducedMotion, visuals } from '../spectrum'

/**
 * The page's background moving with whatever plays in the browser: three soft glows behind the cards, one each for
 * bass, mids and highs, that swell and brighten with their range. Only transform and opacity change per frame, which
 * the compositor handles without repainting the page (Safari reloads pages that burn energy). Runs only when switched
 * on, while something plays and the page is visible, and never when the system asks for less motion.
 */
const glows = useTemplateRef<HTMLDivElement[]>('glows')

// Hidden behind the full-screen player, which has glows of its own.
const active = computed(
  () => visuals.value.background && !reducedMotion.value && anythingPlaying.value && !expanded.value,
)

const bands = new Float32Array(24)
/** Bass, mids, highs: smoothed, so the glows breathe rather than flicker. */
const levels = [0, 0, 0]
const RANGES: [from: number, to: number][] = [
  [0, 6],
  [6, 15],
  [15, 24],
]
let frame = 0

function step(): void {
  readBands(
    allSources.value.flatMap((source) => source.analysers()),
    bands,
  )
  RANGES.forEach(([from, to], index) => {
    let sum = 0
    for (let band = from; band < to; band++) {
      sum += bands[band]!
    }
    const level = sum / (to - from)
    // Rising fast, settling slowly, as the analyzers do.
    levels[index] = level > levels[index]! ? levels[index]! * 0.5 + level * 0.5 : levels[index]! * 0.93 + level * 0.07
  })
  glows.value?.forEach((glow, index) => {
    const level = levels[index]!
    glow.style.transform = `translate(-50%, -50%) scale(${0.7 + level * 0.8})`
    glow.style.opacity = String(0.15 + level * 0.85)
  })
  frame = requestAnimationFrame(step)
}

function update(): void {
  const run = active.value && document.visibilityState === 'visible'
  if (run && frame === 0) {
    frame = requestAnimationFrame(step)
  } else if (!run && frame !== 0) {
    cancelAnimationFrame(frame)
    frame = 0
  }
}

watch(active, update)

onMounted(() => {
  document.addEventListener('visibilitychange', update)
  update()
})

onBeforeUnmount(() => {
  cancelAnimationFrame(frame)
  document.removeEventListener('visibilitychange', update)
})
</script>

<template>
  <div class="audio-background" :class="{ on: active }" aria-hidden="true">
    <div v-for="index in 3" :key="index" ref="glows" class="glow" :class="`glow-${index}`" />
  </div>
</template>

<style scoped>
/* Fixed behind the page: #app isolates its stacking context, so z-index -1 lands above the body's background and
   below every card. */
.audio-background {
  position: fixed;
  inset: 0;
  z-index: -1;
  overflow: hidden;
  pointer-events: none;
  opacity: 0;
  transition: opacity 1.2s ease;
}

.audio-background.on {
  opacity: 1;
}

.glow {
  position: absolute;
  width: 70vmax;
  height: 70vmax;
  border-radius: 50%;
  opacity: 0.15;
  transform: translate(-50%, -50%) scale(0.7);
  will-change: transform, opacity;
}

.glow-1 {
  top: 85%;
  left: 20%;
  background: radial-gradient(circle, color-mix(in srgb, var(--accent) 45%, transparent), transparent 65%);
}

.glow-2 {
  top: 35%;
  left: 80%;
  background: radial-gradient(circle, color-mix(in srgb, var(--p-sky-500) 35%, transparent), transparent 65%);
}

.glow-3 {
  top: 10%;
  left: 35%;
  background: radial-gradient(circle, color-mix(in srgb, var(--p-violet-500) 30%, transparent), transparent 65%);
}
</style>

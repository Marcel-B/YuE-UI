<script setup lang="ts">
import { computed } from 'vue'

/**
 * A recording's waveform with the chosen part highlighted and the playback position as a line; a tap seeks there.
 * SVG rather than canvas, so the app's colour variables (Aura's light-dark() tokens) apply as they are.
 */
const props = defineProps<{
  /** From `peaksOf`, each 0..1. */
  peaks: number[]
  duration: number
  start: number
  end: number
  position: number
}>()

const emit = defineEmits<{ seek: [seconds: number] }>()

/** One bar per peak, mirrored around the middle; the viewBox is one unit per bar and 100 high. */
const path = computed(() =>
  props.peaks
    .map((peak, index) => {
      const height = Math.max(1, peak * 96)
      return `M${index + 0.15} ${50 - height / 2}h0.7v${height}h-0.7z`
    })
    .join(''),
)

function x(seconds: number): number {
  return props.duration > 0 ? (Math.min(Math.max(seconds, 0), props.duration) / props.duration) * props.peaks.length : 0
}

function seek(event: MouseEvent): void {
  const box = (event.currentTarget as SVGElement).getBoundingClientRect()
  if (box.width > 0) {
    emit('seek', ((event.clientX - box.left) / box.width) * props.duration)
  }
}
</script>

<template>
  <svg
    class="waveform block h-16 w-full cursor-pointer"
    :viewBox="`0 0 ${peaks.length} 100`"
    preserveAspectRatio="none"
    aria-hidden="true"
    @click="seek"
  >
    <defs>
      <clipPath id="waveform-part">
        <rect :x="x(start)" y="0" :width="Math.max(0, x(end) - x(start))" height="100" />
      </clipPath>
    </defs>
    <rect :x="x(start)" y="0" :width="Math.max(0, x(end) - x(start))" height="100" class="part" />
    <path :d="path" class="outside" />
    <path :d="path" class="inside" clip-path="url(#waveform-part)" />
    <line :x1="x(position)" :x2="x(position)" y1="0" y2="100" class="position" vector-effect="non-scaling-stroke" />
  </svg>
</template>

<style scoped>
.waveform .part {
  fill: var(--accent-soft);
}
.waveform .outside {
  fill: var(--text-muted);
  opacity: 0.45;
}
.waveform .inside {
  fill: var(--accent);
}
.waveform .position {
  stroke: var(--text);
  stroke-width: 1.5;
}
</style>

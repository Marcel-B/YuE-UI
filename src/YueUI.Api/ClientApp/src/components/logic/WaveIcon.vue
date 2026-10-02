<script setup lang="ts">
import type { Wave } from '../../logic/envelope'

/** A waveform drawn as on a synthesizer's panel, for the wave switches; the name goes along for screen readers. */
defineProps<{ wave: Wave; label: string }>()

const PATHS: Record<Wave, string> = {
  sine: 'M1 8 C4 -1.5 7 -1.5 10 8 S16 17.5 19 8',
  triangle: 'M1 8 L5.5 2 L14.5 14 L19 8',
  sawtooth: 'M1 14 L10 2 L10 14 L19 2 L19 14',
  square: 'M1 14 L1 2 L10 2 L10 14 L19 14 L19 2',
}
</script>

<template>
  <span class="inline-flex items-center" :title="label">
    <svg viewBox="0 0 20 16" class="wave" aria-hidden="true"><path :d="PATHS[wave]" /></svg>
    <span class="sr-only">{{ label }}</span>
  </span>
</template>

<style scoped>
.wave {
  width: 1.4rem;
  height: 1.1rem;
  overflow: visible;
}

path {
  fill: none;
  stroke: currentColor;
  stroke-width: 1.8;
  stroke-linejoin: round;
  stroke-linecap: round;
}
</style>

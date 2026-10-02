<script setup lang="ts">
import { computed } from 'vue'
import { ENVELOPE_RANGES, type Envelope } from '../../logic/envelope'

/**
 * The shape of an ADSR envelope above its four knobs, so a change of attack or release is seen and not only read.
 * Times are drawn on the knobs' own curve (a cube root of the range), as they are turned: a linear time axis would
 * make every attack under 100 ms a vertical line next to a release of seconds.
 */
const props = defineProps<{ envelope: Envelope }>()

const HEIGHT = 30
const TOP = 2
const BOTTOM = HEIGHT - 1
/** The note held: a fixed stretch, since how long a key is down is not part of the sound. */
const HOLD = 22

function width(value: number, [min, max]: readonly [number, number]): number {
  const share = Math.max(0, Math.min(1, (value - min) / (max - min))) ** (1 / 3)
  return 3 + share * 24
}

const shape = computed(() => {
  const env = props.envelope
  const attack = width(env.attack, ENVELOPE_RANGES.attack)
  const decay = width(env.decay, ENVELOPE_RANGES.decay)
  const release = width(env.release, ENVELOPE_RANGES.release)
  const sustain = BOTTOM - env.sustain * (BOTTOM - TOP)
  const a = 1 + attack
  const d = a + decay
  const s = d + HOLD
  const r = s + release
  // Decay and release as quadratic curves bent towards their end, roughly the exponential fall Web Audio makes.
  const line = `M1 ${BOTTOM} L${a} ${TOP} Q${a + decay * 0.25} ${sustain} ${d} ${sustain} L${s} ${sustain} Q${s + release * 0.25} ${BOTTOM} ${r} ${BOTTOM}`
  return { line, area: `${line} Z`, total: r + 1 }
})
</script>

<template>
  <svg class="curve" :viewBox="`0 0 ${shape.total} ${HEIGHT}`" preserveAspectRatio="none" aria-hidden="true">
    <path class="area" :d="shape.area" />
    <path class="line" :d="shape.line" vector-effect="non-scaling-stroke" />
  </svg>
</template>

<style scoped>
.curve {
  display: block;
  width: 100%;
  height: 2.25rem;
  margin-bottom: 0.375rem;
  border-radius: 0.25rem;
  background: var(--surface);
}

.area {
  fill: var(--accent);
  opacity: 0.15;
}

.line {
  fill: none;
  stroke: var(--accent);
  stroke-width: 2;
  stroke-linejoin: round;
}
</style>

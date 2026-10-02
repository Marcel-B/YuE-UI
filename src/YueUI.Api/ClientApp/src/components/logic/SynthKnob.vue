<script setup lang="ts">
import { computed, useId } from 'vue'

/**
 * One parameter of the synthesizer as a rotary knob, the way the Logic plugin (Marcel-B/Synth) and hardware show it:
 * the name above, the value below, so a module's knobs line up in a compact row instead of one slider per line.
 * Dragged up or right to turn it (a quarter of the width finer with Shift), arrow keys and Page Up/Down on the
 * keyboard. `curve` above 1 gives the low end of the range more of the turn, which times and cutoff need: the
 * difference between 5 and 50 ms matters, between 3.5 and 4 s hardly. A range around zero (detune, octave, envelope
 * amounts) draws its arc from the middle, so the sign can be seen.
 */
const props = withDefaults(
  defineProps<{
    label: string
    min: number
    max: number
    /** Rounds the value; 1 for octaves. */
    step?: number
    curve?: number
    disabled?: boolean
    format: (value: number) => string
  }>(),
  { step: 0, curve: 1, disabled: false },
)

const model = defineModel<number>({ required: true })
const id = useId()

/** Pixels of drag for the whole range: long enough to hit a value on a phone, short enough for one thumb stroke. */
const TRAVEL = 200
const SWEEP = 270

const share = computed(() => {
  const linear = (model.value - props.min) / (props.max - props.min)
  return Math.max(0, Math.min(1, linear)) ** (1 / props.curve)
})
/** Where the arc starts: the bottom left, or straight up for a range around zero. */
const origin = computed(() => {
  if (props.min >= 0 || props.max <= 0) {
    return 0
  }
  return (-props.min / (props.max - props.min)) ** (1 / props.curve)
})

function set(next: number): void {
  const clamped = Math.max(0, Math.min(1, next))
  const raw = props.min + clamped ** props.curve * (props.max - props.min)
  const value = props.step > 0 ? Math.round(raw / props.step) * props.step : raw
  if (value !== model.value) {
    model.value = value
  }
}

// The drag goes by where it started, not by the rounded value, so a stepped knob still moves under a slow finger.
let drag: { x: number; y: number; share: number } | null = null

function down(event: PointerEvent): void {
  if (props.disabled || event.button !== 0) {
    return
  }
  ;(event.currentTarget as HTMLElement).setPointerCapture(event.pointerId)
  drag = { x: event.clientX, y: event.clientY, share: share.value }
  event.preventDefault()
}

function move(event: PointerEvent): void {
  if (!drag) {
    return
  }
  const distance = drag.y - event.clientY + (event.clientX - drag.x)
  set(drag.share + distance / (event.shiftKey ? TRAVEL * 4 : TRAVEL))
}

function up(): void {
  drag = null
}

function key(event: KeyboardEvent): void {
  if (props.disabled) {
    return
  }
  // A stepped knob moves by at least one step per press.
  const smallest = props.step > 0 ? props.step / (props.max - props.min) : 0
  const fine = Math.max(0.01, smallest)
  const moves: Record<string, () => void> = {
    ArrowUp: () => set(share.value + fine),
    ArrowRight: () => set(share.value + fine),
    ArrowDown: () => set(share.value - fine),
    ArrowLeft: () => set(share.value - fine),
    PageUp: () => set(share.value + 0.1),
    PageDown: () => set(share.value - 0.1),
    Home: () => set(0),
    End: () => set(1),
  }
  const action = moves[event.key]
  if (action) {
    action()
    event.preventDefault()
  }
}

const RADIUS = 15
const CENTER = 20

function point(position: number): [number, number] {
  const angle = ((position * SWEEP - SWEEP / 2) * Math.PI) / 180
  return [CENTER + RADIUS * Math.sin(angle), CENTER - RADIUS * Math.cos(angle)]
}

function arc(from: number, to: number): string {
  const [start, end] = from <= to ? [from, to] : [to, from]
  if (end - start < 0.001) {
    return ''
  }
  const [x1, y1] = point(start)
  const [x2, y2] = point(end)
  const large = (end - start) * SWEEP > 180 ? 1 : 0
  return `M ${x1} ${y1} A ${RADIUS} ${RADIUS} 0 ${large} 1 ${x2} ${y2}`
}

const track = arc(0, 1)
const value = computed(() => arc(origin.value, share.value))
const pointer = computed(() => {
  const angle = ((share.value * SWEEP - SWEEP / 2) * Math.PI) / 180
  const reach = (from: number) => [CENTER + from * Math.sin(angle), CENTER - from * Math.cos(angle)]
  const [x1, y1] = reach(4)
  const [x2, y2] = reach(10)
  return { x1, y1, x2, y2 }
})
</script>

<template>
  <div class="knob" :class="{ disabled }">
    <span :id="id" class="label">{{ label }}</span>
    <div
      class="dial"
      role="slider"
      :tabindex="disabled ? -1 : 0"
      :aria-labelledby="id"
      :aria-valuemin="min"
      :aria-valuemax="max"
      :aria-valuenow="model"
      :aria-valuetext="format(model)"
      :aria-disabled="disabled || undefined"
      @pointerdown="down"
      @pointermove="move"
      @pointerup="up"
      @pointercancel="up"
      @keydown="key"
    >
      <svg viewBox="0 0 40 40" aria-hidden="true">
        <path class="track" :d="track" />
        <path v-if="value" class="value" :d="value" />
        <circle class="cap" :cx="CENTER" :cy="CENTER" r="10.5" />
        <line class="pointer" v-bind="pointer" />
      </svg>
    </div>
    <span class="text-xs tabular-nums text-muted-color">{{ format(model) }}</span>
  </div>
</template>

<style scoped>
.knob {
  display: flex;
  flex-direction: column;
  align-items: center;
  width: 4.25rem;
  gap: 0.1rem;
  text-align: center;
}

.label {
  max-width: 100%;
  overflow: hidden;
  font-size: 0.75rem;
  line-height: 1.2;
  white-space: nowrap;
  text-overflow: ellipsis;
}

.dial {
  width: 2.75rem;
  height: 2.75rem;
  border-radius: 50%;
  cursor: ns-resize;
  /* The knob takes the finger's drag; the page scrolls beside it. */
  touch-action: none;
  outline: none;
}

.dial:focus-visible {
  box-shadow: 0 0 0 2px var(--accent-soft);
}

svg {
  display: block;
  width: 100%;
  height: 100%;
  overflow: visible;
}

.track,
.value {
  fill: none;
  stroke-width: 3.5;
  stroke-linecap: round;
}

.track {
  stroke: var(--border-strong);
}

.value {
  stroke: var(--accent);
}

.cap {
  fill: var(--surface-sunken);
  stroke: var(--border-strong);
  stroke-width: 1;
}

.pointer {
  stroke: var(--text);
  stroke-width: 2.5;
  stroke-linecap: round;
}

.disabled {
  opacity: 0.45;
}

.disabled .dial {
  cursor: default;
}
</style>

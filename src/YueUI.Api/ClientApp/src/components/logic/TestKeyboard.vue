<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { t } from '../../logic/i18n'
import { hold, type KeyboardTarget, type Release } from '../../logic/keyboardVoice'
import { noteName } from '../../logic/notes'
import { percent } from '../../logic/synthFormats'
import SynthKnob from './SynthKnob.vue'

/**
 * Two octaves to try a sound or a MIDI instrument out. Keys are played by pointer, several fingers at once and by
 * sliding across them, and on a computer by the letter row (A W S E D … K), as in Logic's musical typing.
 */
const props = defineProps<{ target: KeyboardTarget | null; active: boolean }>()

/** The lowest C; 48 is C2 in Logic's naming, the octave below middle C. */
const base = ref(48)
const velocity = ref(0.8)
const SPAN = 25

const BLACK = new Set([1, 3, 6, 8, 10])
const keys = computed(() =>
  Array.from({ length: SPAN }, (_, index) => ({ pitch: base.value + index, black: BLACK.has(index % 12) })),
)
const whites = computed(() => keys.value.filter((key) => !key.black))
/** A black key sits on the line between the two white keys around it. */
function blackOffset(pitch: number): number {
  const whiteBefore = keys.value.filter((key) => !key.black && key.pitch < pitch).length
  return (whiteBefore / whites.value.length) * 100
}

const range = computed(() => `${noteName(base.value)} – ${noteName(base.value + SPAN - 1)}`)

/** What sounds, by what holds it: a pointer id or a computer key. */
const held = new Map<string, { pitch: number; release: Release }>()
const down = ref<Set<number>>(new Set())

function press(holder: string, pitch: number): void {
  const current = held.get(holder)
  if (current?.pitch === pitch || !props.target) {
    return
  }
  lift(holder)
  held.set(holder, { pitch, release: hold(props.target, pitch, velocity.value) })
  down.value = new Set([...held.values()].map((entry) => entry.pitch))
}

function lift(holder: string): void {
  const current = held.get(holder)
  if (!current) {
    return
  }
  current.release()
  held.delete(holder)
  down.value = new Set([...held.values()].map((entry) => entry.pitch))
}

function liftAll(): void {
  for (const holder of [...held.keys()]) {
    lift(holder)
  }
}

// ---- Pointer: the key under each finger, found anew as it slides ------------------------------------

function pitchAt(x: number, y: number): number | null {
  const element = document.elementFromPoint(x, y)
  const pitch = element instanceof HTMLElement ? element.dataset.pitch : undefined
  return pitch === undefined ? null : Number(pitch)
}

function pointerDown(event: PointerEvent): void {
  ;(event.currentTarget as HTMLElement).setPointerCapture(event.pointerId)
  const pitch = pitchAt(event.clientX, event.clientY)
  if (pitch !== null) {
    press(`p${event.pointerId}`, pitch)
  }
}

function pointerMove(event: PointerEvent): void {
  if (!held.has(`p${event.pointerId}`)) {
    return
  }
  const pitch = pitchAt(event.clientX, event.clientY)
  if (pitch === null) {
    lift(`p${event.pointerId}`)
  } else {
    press(`p${event.pointerId}`, pitch)
  }
}

function pointerUp(event: PointerEvent): void {
  lift(`p${event.pointerId}`)
}

// ---- Computer keyboard ----------------------------------------------------------------------------

/** Logic's musical typing: the home row for the white keys, the row above for the black ones. */
const TYPING: Record<string, number> = {
  KeyA: 0,
  KeyW: 1,
  KeyS: 2,
  KeyE: 3,
  KeyD: 4,
  KeyF: 5,
  KeyT: 6,
  KeyG: 7,
  KeyY: 8,
  KeyH: 9,
  KeyU: 10,
  KeyJ: 11,
  KeyK: 12,
  KeyO: 13,
  KeyL: 14,
  KeyP: 15,
  Semicolon: 16,
}

function typing(event: KeyboardEvent): boolean {
  const target = event.target as HTMLElement | null
  return (
    !props.active ||
    event.metaKey ||
    event.ctrlKey ||
    event.altKey ||
    !!target?.closest('input, textarea, select, [contenteditable="true"], [role="combobox"]')
  )
}

function keyDown(event: KeyboardEvent): void {
  if (typing(event) || event.repeat) {
    return
  }
  const offset = TYPING[event.code]
  if (offset !== undefined) {
    press(`k${event.code}`, base.value + offset)
    event.preventDefault()
  } else if (event.code === 'KeyZ') {
    shift(-12)
  } else if (event.code === 'KeyX') {
    shift(12)
  }
}

function keyUp(event: KeyboardEvent): void {
  lift(`k${event.code}`)
}

function shift(by: number): void {
  // Whole octaves, and the top key stays on MIDI's last note.
  base.value = Math.max(0, Math.min(96, base.value + by))
}

onMounted(() => {
  window.addEventListener('keydown', keyDown)
  window.addEventListener('keyup', keyUp)
  window.addEventListener('blur', liftAll)
})
onBeforeUnmount(() => {
  window.removeEventListener('keydown', keyDown)
  window.removeEventListener('keyup', keyUp)
  window.removeEventListener('blur', liftAll)
  liftAll()
})
// A note held while the sound or the page changes would otherwise never be let go.
watch(
  () => [props.target, props.active],
  () => liftAll(),
)
</script>

<template>
  <div>
    <div class="mb-2 flex flex-wrap items-center gap-2">
      <Button
        icon="pi pi-minus"
        size="small"
        severity="secondary"
        outlined
        :aria-label="t('keyboardDown')"
        v-tooltip.bottom="t('keyboardDown')"
        @click="shift(-12)"
      />
      <span class="min-w-24 text-center text-sm tabular-nums">{{ range }}</span>
      <Button
        icon="pi pi-plus"
        size="small"
        severity="secondary"
        outlined
        :aria-label="t('keyboardUp')"
        v-tooltip.bottom="t('keyboardUp')"
        @click="shift(12)"
      />
      <SynthKnob v-model="velocity" :label="t('keyboardVelocity')" :min="0.05" :max="1" :format="percent" />
    </div>
    <div
      class="keyboard"
      :class="{ 'opacity-50': !target }"
      role="group"
      :aria-label="t('keyboardTitle')"
      @pointerdown="pointerDown"
      @pointermove="pointerMove"
      @pointerup="pointerUp"
      @pointercancel="pointerUp"
    >
      <div
        v-for="key in whites"
        :key="key.pitch"
        class="white"
        :class="{ down: down.has(key.pitch) }"
        :data-pitch="key.pitch"
      >
        <span v-if="key.pitch % 12 === 0" class="label">{{ noteName(key.pitch) }}</span>
      </div>
      <div
        v-for="key in keys.filter((entry) => entry.black)"
        :key="key.pitch"
        class="black"
        :class="{ down: down.has(key.pitch) }"
        :data-pitch="key.pitch"
        :style="{ left: `calc(${blackOffset(key.pitch)}% - var(--black-width) / 2)` }"
      />
    </div>
    <p class="muted mt-2 mb-0 text-xs">{{ target ? t('keyboardHint') : t('keyboardNoTarget') }}</p>
  </div>
</template>

<style scoped>
.keyboard {
  --black-width: calc(100% / 15 * 0.6);
  position: relative;
  display: flex;
  height: 8rem;
  touch-action: none;
  user-select: none;
  -webkit-user-select: none;
}

.white {
  position: relative;
  flex: 1;
  border: 1px solid var(--border-strong);
  border-left-width: 0;
  border-radius: 0 0 4px 4px;
  background: var(--surface);
}

.white:first-child {
  border-left-width: 1px;
}

.black {
  position: absolute;
  top: 0;
  z-index: 1;
  width: var(--black-width);
  height: 60%;
  border-radius: 0 0 3px 3px;
  background: var(--text);
}

.white.down,
.black.down {
  background: var(--accent);
}

.label {
  position: absolute;
  bottom: 0.25rem;
  left: 0;
  right: 0;
  color: var(--text-muted);
  font-size: 0.65rem;
  text-align: center;
  pointer-events: none;
}
</style>

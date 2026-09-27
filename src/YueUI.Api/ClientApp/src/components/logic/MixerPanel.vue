<script setup lang="ts">
import Slider from 'primevue/slider'
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { t } from '../../logic/i18n'
import type { OutputPool } from '../../logic/midiPlayer'
import { trackColour } from '../../logic/pianoRoll'
import { MAX_VOLUME, mixer, setMasterVolume, setTrackMix, trackKey, trackMix } from '../../logic/synths'

/**
 * The preview's mixer: a strip per track with fader, pan, mute, solo and a meter, and the master. Fader and pan act on
 * the sound in the browser only; mute and solo decide which tracks play at all, on hardware as well.
 */
export interface MixerTrack {
  id: string
  /** Whether the track sounds in the browser; a track on a MIDI port has its level on the device. */
  inBrowser: boolean
  muted: boolean
  solo: boolean
}

const emit = defineEmits<{
  mute: [index: number, muted: boolean]
  solo: [index: number, solo: boolean]
}>()

const props = defineProps<{ tracks: MixerTrack[]; pool: OutputPool }>()

/** Slider positions: a square curve gives the range around 0 dB, where a mix is made, most of the travel. */
const STEPS = 1000
const toPosition = (volume: number) => Math.round(Math.sqrt(volume / MAX_VOLUME) * STEPS)
const toVolume = (position: number) => (position / STEPS) ** 2 * MAX_VOLUME

function decibels(volume: number): string {
  if (volume < 0.001) {
    return '−∞'
  }
  const db = 20 * Math.log10(volume)
  return `${db > 0.05 ? '+' : ''}${db.toFixed(1).replace('-', '−')}`
}

function panLabel(pan: number): string {
  const amount = Math.round(Math.abs(pan) * 100)
  return amount < 2 ? 'C' : `${pan < 0 ? 'L' : 'R'}${amount}`
}

function single(value: number | number[]): number {
  return Array.isArray(value) ? value[0]! : value
}

// ---- Meters ------------------------------------------------------------------------------------------

/** What the meters show, 0 to 1 over -48 to +6 dB; a peak falls back slowly, as on a desk. */
const shown = ref<Record<string, number>>({})
const master = ref(0)
const clipped = ref<Record<string, boolean>>({})
let frame = 0

function height(peak: number): number {
  if (peak <= 0) {
    return 0
  }
  return Math.min(1, Math.max(0, (20 * Math.log10(peak) + 48) / 54))
}

function tick(): void {
  const levels = props.pool.levels()
  const next: Record<string, number> = {}
  const clips: Record<string, boolean> = { ...clipped.value }
  for (const track of props.tracks) {
    const key = trackKey(track.id)
    const peak = levels.tracks.get(track.id) ?? 0
    next[key] = Math.max(height(peak), (shown.value[key] ?? 0) * 0.92)
    if (peak > 1) {
      clips[key] = true
    }
  }
  shown.value = next
  master.value = Math.max(height(levels.master), master.value * 0.92)
  if (levels.master > 1) {
    clips.master = true
  }
  clipped.value = clips
  frame = requestAnimationFrame(tick)
}

onMounted(() => (frame = requestAnimationFrame(tick)))
onBeforeUnmount(() => cancelAnimationFrame(frame))

/** A clip lamp stays lit until it is clicked, so a short overload is not missed. */
function clearClip(key: string): void {
  const next = { ...clipped.value }
  delete next[key]
  clipped.value = next
}
</script>

<template>
  <div class="flex gap-2 overflow-x-auto pb-2">
    <div v-for="(track, index) in tracks" :key="track.id" class="strip" :class="{ 'opacity-50': track.muted }">
      <div class="flex items-center gap-1 text-xs font-medium" :title="track.id">
        <span class="swatch" :style="{ background: trackColour(index) }" />
        <span class="truncate">{{ track.id }}</span>
      </div>

      <div class="flex gap-1">
        <Button
          size="small"
          :severity="track.muted ? 'warn' : 'secondary'"
          :outlined="!track.muted"
          label="M"
          class="toggle"
          :aria-pressed="track.muted"
          :title="t('mixerMute')"
          @click="emit('mute', index, !track.muted)"
        />
        <Button
          size="small"
          :severity="track.solo ? 'info' : 'secondary'"
          :outlined="!track.solo"
          label="S"
          class="toggle"
          :aria-pressed="track.solo"
          :title="t('mixerSolo')"
          @click="emit('solo', index, !track.solo)"
        />
      </div>

      <div class="fader">
        <Slider
          orientation="vertical"
          :model-value="toPosition(trackMix(track.id).volume)"
          :min="0"
          :max="STEPS"
          :disabled="!track.inBrowser"
          :aria-label="`${t('mixerVolume')} ${track.id}`"
          class="h-full"
          @update:model-value="setTrackMix(track.id, { volume: toVolume(single($event)) })"
        />
        <button
          type="button"
          class="meter"
          :class="{ clip: clipped[trackKey(track.id)] }"
          :title="t('mixerClip')"
          @click="clearClip(trackKey(track.id))"
        >
          <span :style="{ height: `${(shown[trackKey(track.id)] ?? 0) * 100}%` }" />
        </button>
      </div>

      <button
        type="button"
        class="value"
        :disabled="!track.inBrowser"
        :title="t('mixerReset')"
        @click="setTrackMix(track.id, { volume: 1 })"
      >
        {{ track.inBrowser ? decibels(trackMix(track.id).volume) : 'MIDI' }}
      </button>

      <Slider
        :model-value="Math.round(trackMix(track.id).pan * 100)"
        :min="-100"
        :max="100"
        :disabled="!track.inBrowser"
        :aria-label="`${t('mixerPan')} ${track.id}`"
        class="mx-1"
        @update:model-value="setTrackMix(track.id, { pan: single($event) / 100 })"
      />
      <button
        type="button"
        class="value"
        :disabled="!track.inBrowser"
        :title="t('mixerCentre')"
        @click="setTrackMix(track.id, { pan: 0 })"
      >
        {{ panLabel(trackMix(track.id).pan) }}
      </button>
    </div>

    <div class="strip master">
      <div class="text-xs font-medium">{{ t('mixerMaster') }}</div>
      <div class="h-[1.75rem]" />
      <div class="fader">
        <Slider
          orientation="vertical"
          :model-value="toPosition(mixer.master)"
          :min="0"
          :max="STEPS"
          :aria-label="t('mixerMaster')"
          class="h-full"
          @update:model-value="setMasterVolume(toVolume(single($event)))"
        />
        <button
          type="button"
          class="meter"
          :class="{ clip: clipped.master }"
          :title="t('mixerClip')"
          @click="clearClip('master')"
        >
          <span :style="{ height: `${master * 100}%` }" />
        </button>
      </div>
      <button type="button" class="value" :title="t('mixerReset')" @click="setMasterVolume(1)">
        {{ decibels(mixer.master) }}
      </button>
    </div>
  </div>
</template>

<style scoped>
.strip {
  flex: none;
  display: flex;
  flex-direction: column;
  align-items: stretch;
  gap: 0.5rem;
  width: 5rem;
  padding: 0.5rem;
  border: 1px solid var(--border);
  border-radius: var(--radius-small);
}

.strip.master {
  background: var(--surface-sunken);
}

.swatch {
  flex: none;
  width: 0.6rem;
  height: 0.6rem;
  border-radius: 2px;
}

.toggle {
  flex: 1;
  padding-inline: 0;
}

.fader {
  display: flex;
  justify-content: center;
  gap: 0.75rem;
  height: 9rem;
  padding-block: 0.5rem;
}

/* The meter: a bar from green to red over -48 to +6 dB; its top edge lights when the level passed 0 dB. */
.meter {
  position: relative;
  width: 0.5rem;
  padding: 0;
  border: none;
  border-top: 3px solid var(--border);
  border-radius: 2px;
  background: var(--surface-sunken);
  overflow: hidden;
  cursor: pointer;
}

.meter.clip {
  border-top-color: var(--danger);
}

.meter span {
  position: absolute;
  inset: auto 0 0 0;
  background: linear-gradient(to top, #22c55e 0%, #22c55e 70%, #eab308 85%, #ef4444 100%);
  background-size: 100% 9rem;
  background-position: bottom;
}

.value {
  padding: 0;
  border: none;
  background: none;
  color: var(--text-muted);
  font: inherit;
  font-size: 0.75rem;
  font-variant-numeric: tabular-nums;
  text-align: center;
  cursor: pointer;
}

.value:disabled {
  cursor: default;
}
</style>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { t } from '../logic/i18n'
import { instruments } from '../logic/instrumentLibrary'
import { setKeyboardEffects, type KeyboardTarget } from '../logic/keyboardVoice'
import { midiUsable } from '../logic/midiPlayer'
import { normalizePatch, type SynthPatch } from '../logic/synth'
import type { Instrument } from '../logic/types'
import InstrumentLibrary from './logic/InstrumentLibrary.vue'
import SoundLibrary from './logic/SoundLibrary.vue'
import TestKeyboard from './logic/TestKeyboard.vue'

/**
 * The sounds a song can be played on: the browser synthesizers' saved sounds and the MIDI instruments of the studio,
 * each tried out on a small keyboard. Which track plays what is chosen on the Logic page.
 */
defineProps<{ active: boolean }>()

/** The sound in the editor, played by the keyboard as it is edited. */
const draft = ref<SynthPatch>(normalizePatch(null, 'Melody'))

// A tail still ringing after the key follows the effect sliders, not only the next note.
watch(
  () => draft.value.fx,
  (fx) => setKeyboardEffects(fx),
  { deep: true },
)

/** What the keyboard plays: `sound` for the editor's, or `midi:<id>` for an instrument. */
const source = ref('sound')
/** Only browsers with Web MIDI (Chromium, Firefox) reach MIDI ports; elsewhere the keyboard plays the browser sound only. */
const canPlayMidi = midiUsable()

const sources = computed(() => [
  { label: t('keyboardSound'), value: 'sound' },
  ...(canPlayMidi
    ? instruments.value.map((instrument) => ({
        label: `${instrument.name} (${instrument.port} · ${instrument.channel})`,
        value: `midi:${instrument.id}`,
      }))
    : []),
])

const target = computed<KeyboardTarget | null>(() => {
  if (source.value === 'sound') {
    return { kind: 'sound', patch: draft.value }
  }
  const instrument = instruments.value.find((entry) => `midi:${entry.id}` === source.value)
  return instrument ? { kind: 'midi', instrument } : null
})

const keyboardCard = ref<HTMLElement | null>(null)

function play(instrument: Instrument): void {
  source.value = `midi:${instrument.id}`
  keyboardCard.value?.scrollIntoView({ behavior: 'smooth', block: 'nearest' })
}
</script>

<template>
  <div class="grid grid-cols-[minmax(0,1fr)] gap-4">
    <Card>
      <template #title>
        <h2 class="m-0">{{ t('soundsTitle') }}</h2>
      </template>
      <template #content>
        <p class="muted mt-0 mb-4 text-sm">{{ t('soundsIntro') }}</p>
        <SoundLibrary v-model="draft" />
      </template>
    </Card>

    <div ref="keyboardCard">
      <Card>
        <template #title>
          <div class="flex flex-wrap items-center justify-between gap-2">
            <h2 class="m-0">{{ t('keyboardTitle') }}</h2>
            <Select
              v-model="source"
              :options="sources"
              option-label="label"
              option-value="value"
              size="small"
              :aria-label="t('keyboardSource')"
              class="max-w-full"
            />
          </div>
        </template>
        <template #content>
          <TestKeyboard :target="target" :active="active" />
        </template>
      </Card>
    </div>

    <Card>
      <template #title>
        <h2 class="m-0">{{ t('instrumentsTitle') }}</h2>
      </template>
      <template #content>
        <InstrumentLibrary @play="play" />
        <p v-if="!canPlayMidi" class="muted mb-0 text-xs">{{ t('keyboardMidiChromium') }}</p>
      </template>
    </Card>
  </div>
</template>

<script setup lang="ts">
import Checkbox from 'primevue/checkbox'
import Message from 'primevue/message'
import { onMounted, ref } from 'vue'
import { ApiError, audioToMidi, type MidiTrack } from '../api'
import { loadMidiInfo, midiInfo, midiSettings } from '../audioMidi'
import { t } from '../i18n'
import FieldHelp from './FieldHelp.vue'
import MidiOptions from './MidiOptions.vue'
import MidiResult from './MidiResult.vue'
import NumberField from './NumberField.vue'

/**
 * A recording to MIDI with Basic Pitch, beside the transcription: where SheetSage2 writes a score on the beat, this
 * keeps the notes where they were sung. Best with a bare voice, such as a stem's dry vocals.
 */
const file = ref<File | null>(null)
const fileInput = ref<HTMLInputElement | null>(null)
const converting = ref(false)
const error = ref<string | null>(null)
const result = ref<MidiTrack | null>(null)
/** The grid needs a tempo; without one the file is written at 120 bpm. */
const tempo = ref<number>(midiSettings.value.tempo ?? 120)
const tempoKnown = ref(midiSettings.value.tempo != null)

onMounted(() => void loadMidiInfo())

function pick(event: Event): void {
  file.value = (event.target as HTMLInputElement).files?.[0] ?? null
  result.value = null
  error.value = null
}

async function convert(): Promise<void> {
  if (!file.value || converting.value) {
    return
  }
  converting.value = true
  error.value = null
  result.value = null
  midiSettings.value.tempo = tempoKnown.value ? tempo.value : null
  try {
    result.value = await audioToMidi(file.value, midiSettings.value)
  } catch (caught) {
    error.value = caught instanceof ApiError && caught.status === 0 ? t('errorNetwork') : (caught as Error).message
  } finally {
    converting.value = false
  }
}
</script>

<template>
  <section class="flex flex-col gap-4">
    <FieldHelp id="audio-midi-help" :hint="t('midiIntro')" :more="t('midiMore')" />

    <Message v-if="midiInfo && !midiInfo.installed" severity="warn" role="note">{{ t('midiNotInstalled') }}</Message>

    <form class="flex flex-col gap-4" @submit.prevent="convert">
      <div class="flex flex-col gap-1">
        <label for="audio-midi-file" class="text-sm font-semibold">{{ t('recording') }}</label>
        <input
          id="audio-midi-file"
          ref="fileInput"
          type="file"
          accept="audio/*,.mp3,.m4a,.aac,.wav,.aif,.aiff,.flac,.caf"
          class="hidden"
          @change="pick"
        />
        <div class="flex min-w-0 items-center gap-2">
          <Button
            type="button"
            icon="pi pi-folder-open"
            :label="t('chooseRecording')"
            severity="secondary"
            outlined
            class="shrink-0"
            @click="fileInput?.click()"
          />
          <span class="muted min-w-0 truncate">{{ file?.name ?? t('noRecording') }}</span>
        </div>
      </div>

      <MidiOptions id-prefix="audio-midi" />
      <div class="flex flex-wrap items-center gap-x-3 gap-y-2">
        <div class="flex items-center gap-2">
          <Checkbox v-model="tempoKnown" input-id="audio-midi-tempo-known" binary />
          <label for="audio-midi-tempo-known" class="text-sm">{{ t('midiTempo') }}</label>
        </div>
        <NumberField v-if="tempoKnown" v-model="tempo" :min="20" :max="300" class="w-20" :aria-label="t('midiTempo')" />
        <span v-if="tempoKnown" class="muted text-sm">bpm</span>
        <span v-else-if="midiSettings.quantize" class="danger text-sm">{{ t('midiQuantizeNeedsTempo') }}</span>
      </div>

      <div class="flex flex-wrap items-center gap-3">
        <Button
          type="submit"
          icon="pi pi-wave-pulse"
          :label="t('midiConvert')"
          :loading="converting"
          :disabled="!file || converting || midiInfo?.installed === false || (midiSettings.quantize && !tempoKnown)"
        />
        <span v-if="error" class="danger" role="status">{{ error }}</span>
      </div>
    </form>

    <MidiResult v-if="result" :track="result" />
  </section>
</template>
